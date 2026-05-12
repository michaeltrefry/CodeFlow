using CodeFlow.Contracts;
using CodeFlow.Host;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Testcontainers.MariaDb;
using Testcontainers.RabbitMq;

namespace CodeFlow.Orchestration.Tests;

/// <summary>
/// End-to-end coverage for the ForEach node kind (sc-942 / sc-943 / epic 941). Each test seeds a
/// parent workflow Start → ForEach → Sink plus a single-node child workflow, scripts the agents
/// through a fake invoker, and asserts the saga's terminal state + decision ledger reflect the
/// expected per-iteration behavior.
/// </summary>
[Collection("Bus integration")]
[Trait("Category", "EndToEnd")]
public sealed class WorkflowSagaForEachEndToEndTests : IAsyncLifetime
{
    private const string ChildWorkflowKey = "foreach-child-flow";

    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_foreach_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly RabbitMqContainer rabbitMqContainer = new RabbitMqBuilder("rabbitmq:4.0-management")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly string artifactRoot = Path.Combine(
        Path.GetTempPath(),
        "codeflow-saga-foreach-tests",
        Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(artifactRoot);
        await Task.WhenAll(mariaDbContainer.StartAsync(), rabbitMqContainer.StartAsync());
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(artifactRoot))
        {
            Directory.Delete(artifactRoot, recursive: true);
        }

        await rabbitMqContainer.DisposeAsync();
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task ForEachNode_HappyPath_ThreeItems_RunsChildThreeTimesAndRoutesContinue()
    {
        // Parent: kickoff seeds workflow.items via setWorkflow → ForEach iterates 3 times →
        // sink consumes the aggregate output. Child: a single agent that emits per-item output.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "kickoff-output");
        invoker.Queue("child-agent", new AgentDecision("Completed"), output: "iter-1");
        invoker.Queue("child-agent", new AgentDecision("Completed"), output: "iter-2");
        invoker.Queue("child-agent", new AgentDecision("Completed"), output: "iter-3");
        invoker.Queue("sink", new AgentDecision("Completed"), output: "sink-done");

        await using var harness = await SetupAsync(invoker);
        await SeedChildWorkflowAsync(harness.Services);
        var workflowKey = await SeedForEachParentWorkflowAsync(harness.Services);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(
            nameof(WorkflowSagaStateMachine.Completed),
            because: $"saga should reach Completed; FailureReason: {saga.FailureReason ?? "<none>"}");

        invoker.CallsFor("child-agent").Should().Be(3, "child agent must run once per item");
        invoker.CallsFor("sink").Should().Be(1, "sink runs once on the ForEach Continue port");

        // Each child invocation saw its iteration's loop.* template variables.
        var childInvocations = invoker.AllInvocationsFor("child-agent");
        childInvocations.Should().HaveCount(3);
        childInvocations.Select(i => i.LoopIndex).Should().BeEquivalentTo(new int?[] { 0, 1, 2 });
        childInvocations.Should().OnlyContain(i => i.LoopCount == 3);
        childInvocations.Select(i => i.LoopIsLast).Should().Equal(false, false, true);
        childInvocations.Select(i => i.LoopItem).Should().BeEquivalentTo(new[] { "alpha", "bravo", "charlie" });
    }

    [Fact]
    public async Task ForEachNode_EmptyCollection_RoutesContinueWithoutChildInvocation()
    {
        // The kickoff seeds workflow.items to an empty array. ForEach should short-circuit to
        // Continue with no child invocation; sink fires once.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "kickoff-output");
        invoker.Queue("sink", new AgentDecision("Completed"), output: "sink-done");

        await using var harness = await SetupAsync(invoker);
        await SeedChildWorkflowAsync(harness.Services);
        var workflowKey = await SeedForEachParentWorkflowAsync(harness.Services, kickoffOutputScript: SeedEmptyItemsScript);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
        invoker.CallsFor("child-agent").Should().Be(0, "empty collection must not invoke the child");
        invoker.CallsFor("sink").Should().Be(1);
    }

    [Fact]
    public async Task ForEachNode_FirstChildFailure_AbortsIterationAndRoutesFailed()
    {
        // 3-item iteration but the second child fails. The saga must record which iteration failed
        // and route Failed without invoking the child for items 3+ or the sink.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "kickoff-output");
        invoker.Queue("child-agent", new AgentDecision("Completed"), output: "iter-1");
        invoker.Queue("child-agent", new AgentDecision("Failed"), output: "iter-2-failed");

        await using var harness = await SetupAsync(invoker);
        await SeedChildWorkflowAsync(harness.Services);
        var workflowKey = await SeedForEachParentWorkflowAsync(harness.Services);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
        saga.FailureReason.Should().NotBeNullOrWhiteSpace();
        saga.FailureReason!.Should().Contain("iteration 2/3");

        invoker.CallsFor("child-agent").Should().Be(2, "child runs for items 1 + 2 only; item 3 is skipped");
        invoker.CallsFor("sink").Should().Be(0, "sink must not fire when ForEach routes Failed");
    }

    private async Task<TestHarness> SetupAsync(IAgentInvoker invoker)
    {
        var configuration = BuildConfiguration();
        var endpointsReady = new EndpointReadinessTracker(["agent-invocations", "workflow-saga"]);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(
            builder.Configuration,
            bus => bus.AddConfigureEndpointsCallback((_, _, cfg) =>
                cfg.ConnectReceiveEndpointObserver(endpointsReady)));
        builder.Services.AddSingleton(invoker);

        var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(
            host.Services,
            "kickoff",
            "child-agent",
            "sink");
        return new TestHarness(host, endpointsReady);
    }

    private async Task PublishKickoffAsync(TestHarness harness, string workflowKey, Guid traceId)
    {
        var startNodeId = await GetNodeIdAsync(harness.Services, workflowKey, 1, WorkflowNodeKind.Start);
        var inputRef = await WriteInputArtifactAsync(harness.Services, "seed-input");

        var bus = harness.Services.GetRequiredService<IBus>();
        await bus.Publish(new AgentInvokeRequested(
            TraceId: traceId,
            RoundId: Guid.NewGuid(),
            WorkflowKey: workflowKey,
            WorkflowVersion: 1,
            NodeId: startNodeId,
            AgentKey: "kickoff",
            AgentVersion: 1,
            InputRef: inputRef,
            ContextInputs: new Dictionary<string, JsonElement>()));
    }

    private IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable] = mariaDbContainer.GetConnectionString(),
                ["RabbitMq:Host"] = rabbitMqContainer.Hostname,
                ["RabbitMq:Port"] = rabbitMqContainer.GetMappedPublicPort(5672).ToString(),
                ["RabbitMq:VirtualHost"] = "/",
                ["RabbitMq:Username"] = "codeflow",
                ["RabbitMq:Password"] = "codeflow_dev",
                ["Artifacts:RootDirectory"] = artifactRoot,
                ["RabbitMq:PrefetchCount"] = "1",
                ["RabbitMq:ConsumerConcurrencyLimit"] = "1",
                ["Secrets:MasterKey"] = TestSecrets.DeterministicMasterKeyBase64
            })
            .Build();
    }

    private static async Task SeedAgentsAsync(IServiceProvider services, params string[] agentKeys)
    {
        await using var scope = services.CreateAsyncScope();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentConfigRepository>();

        foreach (var agentKey in agentKeys)
        {
            var configJson = $$"""
            {
              "provider": "lmstudio",
              "model": "{{agentKey}}",
              "enableHostTools": false
            }
            """;

            (await agentRepo.CreateNewVersionAsync(agentKey, configJson, "test")).Should().Be(1);
        }
    }

    private static async Task<Guid> GetNodeIdAsync(
        IServiceProvider services,
        string workflowKey,
        int version,
        WorkflowNodeKind kind)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var workflow = await dbContext.Workflows
            .Include(w => w.Nodes)
            .AsNoTracking()
            .SingleAsync(w => w.Key == workflowKey && w.Version == version);

        return workflow.Nodes.Single(n => n.Kind == kind).NodeId;
    }

    private async Task<Uri> WriteInputArtifactAsync(IServiceProvider services, string content)
    {
        await using var scope = services.CreateAsyncScope();
        var artifactStore = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        return await artifactStore.WriteAsync(
            stream,
            new ArtifactMetadata(
                TraceId: Guid.NewGuid(),
                RoundId: Guid.NewGuid(),
                ArtifactId: Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: "input.txt"));
    }

    private static async Task<WorkflowSagaStateEntity> WaitForTerminalStateAsync(
        IServiceProvider services,
        Guid traceId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        WorkflowSagaStateEntity? saga = null;

        while (DateTime.UtcNow < deadline)
        {
            await using var scope = services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            saga = await dbContext.WorkflowSagas
                .Include(s => s.Decisions)
                .AsNoTracking()
                .SingleOrDefaultAsync(s => s.TraceId == traceId);

            if (saga is not null && saga.CurrentState is
                nameof(WorkflowSagaStateMachine.Completed)
                or nameof(WorkflowSagaStateMachine.Failed))
            {
                return saga;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Saga for trace {traceId} did not reach a terminal state within {timeout}. Last state: {saga?.CurrentState ?? "<missing>"}");
    }

    /// <summary>
    /// Seed-collection script for the happy-path parent: the kickoff's output script writes a
    /// fixed 3-item array onto workflow.items so the ForEach node sees something to iterate over.
    /// </summary>
    private const string SeedThreeItemsScript = """
        setWorkflow('items', ['alpha', 'bravo', 'charlie']);
        setNodePath('Completed');
        """;

    private const string SeedEmptyItemsScript = """
        setWorkflow('items', []);
        setNodePath('Completed');
        """;

    private static async Task<string> SeedForEachParentWorkflowAsync(
        IServiceProvider services,
        string? kickoffOutputScript = null)
    {
        var key = $"foreach-parent-{Guid.NewGuid():N}";

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var forEachNode = Guid.NewGuid();
        var sinkNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";
        const string sinkPortsJson = """["Completed","Failed"]""";
        const string forEachPortsJson = """["Continue","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = key,
            Version = 1,
            Name = $"ForEach test {key}",
            MaxStepsPerSaga = 50,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputScript = kickoffOutputScript ?? SeedThreeItemsScript,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = forEachNode,
                    Kind = WorkflowNodeKind.ForEach,
                    OutputPortsJson = forEachPortsJson,
                    SubflowKey = ChildWorkflowKey,
                    SubflowVersion = 1,
                    CollectionExpression = "workflow.items",
                    ItemVar = "item",
                },
                new WorkflowNodeEntity
                {
                    NodeId = sinkNode,
                    Kind = WorkflowNodeKind.Agent,
                    AgentKey = "sink",
                    AgentVersion = 1,
                    OutputPortsJson = sinkPortsJson,
                },
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = startNode,
                    FromPort = "Completed",
                    ToNodeId = forEachNode,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    SortOrder = 0,
                },
                new WorkflowEdgeEntity
                {
                    FromNodeId = forEachNode,
                    FromPort = "Continue",
                    ToNodeId = sinkNode,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    SortOrder = 1,
                },
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task SeedChildWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        if (await dbContext.Workflows.AnyAsync(w => w.Key == ChildWorkflowKey && w.Version == 1))
        {
            return;
        }

        var startNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";

        dbContext.Workflows.Add(new WorkflowEntity
        {
            Key = ChildWorkflowKey,
            Version = 1,
            Name = "ForEach child",
            MaxStepsPerSaga = 10,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "child-agent",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
            ],
            Edges = []
        });

        await dbContext.SaveChangesAsync();
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly Microsoft.Extensions.Hosting.IHost host;
        private readonly EndpointReadinessTracker tracker;

        public TestHarness(Microsoft.Extensions.Hosting.IHost host, EndpointReadinessTracker tracker)
        {
            this.host = host;
            this.tracker = tracker;
        }

        public IServiceProvider Services => host.Services;

        public async Task StartAsync()
        {
            await host.StartAsync();
            await tracker.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed class EndpointReadinessTracker : IReceiveEndpointObserver
    {
        private readonly HashSet<string> expected;
        private readonly HashSet<string> ready = [];
        private readonly TaskCompletionSource allReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();

        public EndpointReadinessTracker(IEnumerable<string> expectedEndpoints)
        {
            expected = new HashSet<string>(expectedEndpoints, StringComparer.OrdinalIgnoreCase);
        }

        public async Task WaitAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(allReady.Task, Task.Delay(timeout));
            if (completed != allReady.Task)
            {
                string missing;
                lock (gate)
                {
                    missing = string.Join(", ", expected.Except(ready, StringComparer.OrdinalIgnoreCase));
                }
                throw new TimeoutException(
                    $"Timed out waiting for receive endpoints to become ready. Missing: {missing}");
            }
        }

        public Task Ready(ReceiveEndpointReady ready)
        {
            ArgumentNullException.ThrowIfNull(ready);
            var name = ExtractQueueName(ready.InputAddress);
            lock (gate)
            {
                if (expected.Contains(name))
                {
                    this.ready.Add(name);
                    if (expected.IsSubsetOf(this.ready))
                    {
                        allReady.TrySetResult();
                    }
                }
            }
            return Task.CompletedTask;
        }

        public Task Stopping(ReceiveEndpointStopping stopping) => Task.CompletedTask;
        public Task Completed(ReceiveEndpointCompleted completed) => Task.CompletedTask;
        public Task Faulted(ReceiveEndpointFaulted faulted) => Task.CompletedTask;

        private static string ExtractQueueName(Uri? inputAddress)
        {
            if (inputAddress is null)
            {
                return string.Empty;
            }
            var path = inputAddress.AbsolutePath.TrimStart('/');
            var slash = path.IndexOf('/');
            return slash < 0 ? path : path[(slash + 1)..];
        }
    }

    /// <summary>
    /// Like <see cref="WorkflowSagaSwarmEndToEndTests"/>'s recorder, but captures the
    /// <c>loop.*</c> template variables per call so ForEach tests can assert on per-iteration
    /// binding. Items are extracted from the flat variable dictionary the renderer assembles —
    /// the saga publishes <see cref="AgentInvokeRequested.LoopContext"/>, the consumer flattens
    /// it via <see cref="AgentPromptScopeBuilder.BuildLoopVariables"/>, and we reverse it here.
    /// </summary>
    private sealed class RecordingScriptedAgentInvoker : IAgentInvoker
    {
        private readonly ConcurrentDictionary<string, Queue<ScriptedStep>> scripts = new();
        private readonly ConcurrentDictionary<string, int> callCounts = new();
        private readonly ConcurrentDictionary<string, List<RecordedInvocation>> invocations = new();

        public void Queue(string agentKey, AgentDecision decision, string output)
        {
            var queue = scripts.GetOrAdd(agentKey, _ => new Queue<ScriptedStep>());
            lock (queue)
            {
                queue.Enqueue(new ScriptedStep(decision, output));
            }
        }

        public int CallsFor(string agentKey) =>
            callCounts.TryGetValue(agentKey, out var count) ? count : 0;

        public IReadOnlyList<RecordedInvocation> AllInvocationsFor(string agentKey)
        {
            if (!invocations.TryGetValue(agentKey, out var list))
            {
                return Array.Empty<RecordedInvocation>();
            }
            lock (list)
            {
                return list.ToArray();
            }
        }

        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            CancellationToken cancellationToken = default,
            CodeFlow.Runtime.ToolExecutionContext? toolExecutionContext = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var agentKey = configuration.Model;

            if (!scripts.TryGetValue(agentKey, out var queue))
            {
                throw new InvalidOperationException(
                    $"No scripted steps registered for agent '{agentKey}'.");
            }

            ScriptedStep step;
            lock (queue)
            {
                if (queue.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Scripted queue for agent '{agentKey}' is empty.");
                }
                step = queue.Dequeue();
            }

            var loopItem = configuration.Variables?.TryGetValue("loop.item", out var item) == true ? item : null;
            int? loopIndex = configuration.Variables?.TryGetValue("loop.index", out var idx) == true
                && int.TryParse(idx, out var iParsed) ? iParsed : null;
            int? loopCount = configuration.Variables?.TryGetValue("loop.count", out var cnt) == true
                && int.TryParse(cnt, out var cParsed) ? cParsed : null;
            bool? loopIsLast = configuration.Variables?.TryGetValue("loop.isLast", out var last) == true
                ? string.Equals(last, "true", StringComparison.Ordinal)
                : null;

            var list = invocations.GetOrAdd(agentKey, _ => []);
            lock (list)
            {
                list.Add(new RecordedInvocation(input, loopItem, loopIndex, loopCount, loopIsLast));
            }
            callCounts.AddOrUpdate(agentKey, 1, (_, count) => count + 1);

            return Task.FromResult(new AgentInvocationResult(
                Output: step.Output,
                Decision: step.Decision,
                Transcript: [],
                TokenUsage: new Runtime.TokenUsage(1, 1, 2),
                ToolCallsExecuted: 0));
        }

        private sealed record ScriptedStep(AgentDecision Decision, string Output);
    }

    private sealed record RecordedInvocation(
        string? Input,
        string? LoopItem,
        int? LoopIndex,
        int? LoopCount,
        bool? LoopIsLast);
}
