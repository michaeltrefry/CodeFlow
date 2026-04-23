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
using Testcontainers.MariaDb;
using Testcontainers.RabbitMq;
using RuntimeDecisionKind = CodeFlow.Runtime.AgentDecisionKind;

namespace CodeFlow.Orchestration.Tests;

[Collection("Bus integration")]
[Trait("Category", "EndToEnd")]
public sealed class WorkflowSagaEndToEndTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_saga_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly RabbitMqContainer rabbitMqContainer = new RabbitMqBuilder("rabbitmq:4.0-management")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly string artifactRoot = Path.Combine(
        Path.GetTempPath(),
        "codeflow-saga-tests",
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
    public async Task ThreeAgentWorkflow_ShouldCompleteAfterRejectionRevise()
    {
        var scriptedInvoker = new ScriptedAgentInvoker();
        scriptedInvoker.Queue("evaluator",
            new CompletedDecision(), output: "evaluator-pass-1");
        scriptedInvoker.Queue("reviewer",
            new RejectedDecision(["needs stronger citations"]), output: "reviewer-rejection");
        scriptedInvoker.Queue("evaluator",
            new CompletedDecision(), output: "evaluator-pass-2");
        scriptedInvoker.Queue("reviewer",
            new ApprovedDecision(), output: "reviewer-approval");
        scriptedInvoker.Queue("publisher",
            new CompletedDecision(), output: "publisher-done");

        var configuration = new ConfigurationBuilder()
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

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(builder.Configuration);
        builder.Services.AddSingleton<IAgentInvoker>(scriptedInvoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(host.Services);
        var workflowKey = await SeedWorkflowAsync(host.Services);
        await host.StartAsync();

        try
        {
            var traceId = Guid.NewGuid();
            var initialRoundId = Guid.NewGuid();
            var inputRef = await WriteInputArtifactAsync(host.Services, "seed-input");

            var bus = host.Services.GetRequiredService<IBus>();
            var startNodeId = await GetStartNodeIdAsync(host.Services, workflowKey, 1);
            await bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: initialRoundId,
                WorkflowKey: workflowKey,
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "evaluator",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, System.Text.Json.JsonElement>()));

            var saga = await WaitForTerminalStateAsync(
                host.Services,
                traceId,
                timeout: TimeSpan.FromSeconds(120));

            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
            saga.WorkflowKey.Should().Be(workflowKey);
            saga.WorkflowVersion.Should().Be(1);

            var pinned = saga.GetPinnedAgentVersions();
            pinned.Should().ContainKeys("evaluator", "reviewer", "publisher");
            pinned["evaluator"].Should().Be(1);
            pinned["reviewer"].Should().Be(1);
            pinned["publisher"].Should().Be(1);

            var history = saga.GetDecisionHistory();
            history.Select(record => (record.AgentKey, record.Decision))
                .Should()
                .ContainInOrder(
                    ("evaluator", RuntimeDecisionKind.Completed),
                    ("reviewer", RuntimeDecisionKind.Rejected),
                    ("evaluator", RuntimeDecisionKind.Completed),
                    ("reviewer", RuntimeDecisionKind.Approved),
                    ("publisher", RuntimeDecisionKind.Completed));

            scriptedInvoker.CallsFor("evaluator").Should().Be(2);
            scriptedInvoker.CallsFor("reviewer").Should().Be(2);
            scriptedInvoker.CallsFor("publisher").Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task SeedAgentsAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentConfigRepository>();

        foreach (var agentKey in new[] { "evaluator", "reviewer", "publisher" })
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

    private static async Task<string> SeedWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var evaluator = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var publisher = Guid.NewGuid();
        const string portsJson = """["Completed","Approved","Rejected","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = "article-flow",
            Version = 1,
            Name = "Evaluator-Reviewer-Publisher",
            MaxRoundsPerRound = 10,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = evaluator,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "evaluator",
                    AgentVersion = 1,
                    OutputPortsJson = portsJson
                },
                new WorkflowNodeEntity
                {
                    NodeId = reviewer,
                    Kind = WorkflowNodeKind.Agent,
                    AgentKey = "reviewer",
                    AgentVersion = 1,
                    OutputPortsJson = portsJson
                },
                new WorkflowNodeEntity
                {
                    NodeId = publisher,
                    Kind = WorkflowNodeKind.Agent,
                    AgentKey = "publisher",
                    AgentVersion = 1,
                    OutputPortsJson = portsJson
                }
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = evaluator,
                    FromPort = "Completed",
                    ToNodeId = reviewer,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    RotatesRound = false,
                    SortOrder = 0
                },
                new WorkflowEdgeEntity
                {
                    FromNodeId = reviewer,
                    FromPort = "Approved",
                    ToNodeId = publisher,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    RotatesRound = false,
                    SortOrder = 1
                },
                new WorkflowEdgeEntity
                {
                    FromNodeId = reviewer,
                    FromPort = "Rejected",
                    ToNodeId = evaluator,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    RotatesRound = false,
                    SortOrder = 2
                }
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task<Guid> GetStartNodeIdAsync(IServiceProvider services, string workflowKey, int version)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var workflow = await dbContext.Workflows
            .Include(w => w.Nodes)
            .AsNoTracking()
            .SingleAsync(w => w.Key == workflowKey && w.Version == version);

        return workflow.Nodes.Single(n => n.Kind == WorkflowNodeKind.Start).NodeId;
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
                .AsNoTracking()
                .SingleOrDefaultAsync(s => s.TraceId == traceId);

            if (saga is not null && saga.CurrentState is
                nameof(WorkflowSagaStateMachine.Completed)
                or nameof(WorkflowSagaStateMachine.Failed)
                or nameof(WorkflowSagaStateMachine.Escalated))
            {
                return saga;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Saga for trace {traceId} did not reach a terminal state within {timeout}. Last state: {saga?.CurrentState ?? "<missing>"}");
    }

    private sealed class ScriptedAgentInvoker : IAgentInvoker
    {
        private readonly ConcurrentDictionary<string, Queue<ScriptedStep>> scripts = new();
        private readonly ConcurrentDictionary<string, int> callCounts = new();

        public void Queue(string agentKey, AgentDecision decision, string output)
        {
            var queue = scripts.GetOrAdd(agentKey, _ => new Queue<ScriptedStep>());
            lock (queue)
            {
                queue.Enqueue(new ScriptedStep(decision, output));
            }
        }

        public int CallsFor(string agentKey)
        {
            return callCounts.TryGetValue(agentKey, out var count) ? count : 0;
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
}
