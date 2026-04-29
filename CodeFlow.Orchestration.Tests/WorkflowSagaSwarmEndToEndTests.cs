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
/// End-to-end coverage for sc-43 (Sequential) and sc-46 (Coordinator) protocols on
/// <see cref="WorkflowNodeKind.Swarm"/>. Each test seeds a workflow Start → Swarm → Sink, scripts
/// the configured swarm agents through a fake invoker, and asserts the saga's terminal state +
/// decision ledger reflect the expected per-step rows.
/// </summary>
[Collection("Bus integration")]
[Trait("Category", "EndToEnd")]
public sealed class WorkflowSagaSwarmEndToEndTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_swarm_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly RabbitMqContainer rabbitMqContainer = new RabbitMqBuilder("rabbitmq:4.0-management")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly string artifactRoot = Path.Combine(
        Path.GetTempPath(),
        "codeflow-saga-swarm-tests",
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
    public async Task SwarmNode_Sequential_DispatchesAllContributorsAndSynthesizer_HappyPath()
    {
        // Workflow: Start (kickoff) → Swarm (n=2, Sequential) → Sink.
        // Two contributors run, each sees the prior contributions on workflow.swarmContributions,
        // then the synthesizer assembles a final artifact. The sink agent receives the
        // synthesizer's output verbatim.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "the mission text");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: analyst\nFirst contributor's take.");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: critic\nSecond contributor's take.");
        invoker.Queue("swarm-synthesizer", new AgentDecision("Synthesized"), output: "synthesis-output");
        invoker.Queue("sink", new AgentDecision("Completed"), output: "sink-done");

        await using var harness = await SetupAsync(invoker);
        var workflowKey = await SeedSwarmHappyPathWorkflowAsync(harness.Services);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(
            nameof(WorkflowSagaStateMachine.Completed),
            because: $"saga should reach Completed; FailureReason: {saga.FailureReason ?? "<none>"}");

        // Decision ledger: kickoff, contributor #1, contributor #2, synthesizer, sink.
        var decisions = saga.Decisions.OrderBy(d => d.Ordinal).ToArray();
        var decisionSummary = string.Join(
            "; ",
            decisions.Select(d => $"{d.Ordinal}:{d.AgentKey}={d.Decision}"));
        decisions.Select(d => d.AgentKey).Should().ContainInOrder(
            ["kickoff", "swarm-contributor", "swarm-contributor", "swarm-synthesizer", "sink"],
            because: $"actual decisions: [{decisionSummary}]; kickoff calls: {invoker.CallsFor("kickoff")}, "
                + $"contributor calls: {invoker.CallsFor("swarm-contributor")}, "
                + $"synthesizer calls: {invoker.CallsFor("swarm-synthesizer")}, "
                + $"sink calls: {invoker.CallsFor("sink")}");

        // The two contributor rows + synthesizer all share the swarm node id.
        var swarmNodeId = await GetNodeIdAsync(harness.Services, workflowKey, 1, WorkflowNodeKind.Swarm);
        decisions.Where(d => d.AgentKey is "swarm-contributor" or "swarm-synthesizer")
            .Should().OnlyContain(d => d.NodeId == swarmNodeId);

        // The synthesizer saw the assembled contributions array on its workflow context.
        var synthesizerInput = invoker.LastInvocationFor("swarm-synthesizer");
        synthesizerInput.Should().NotBeNull();
        synthesizerInput!.WorkflowContext.Should().ContainKey("swarmContributions");
        synthesizerInput.WorkflowContext["swarmContributions"].ValueKind.Should().Be(JsonValueKind.Array);
        synthesizerInput.WorkflowContext["swarmContributions"].GetArrayLength().Should().Be(2);

        // The synthesizer prompt receives swarmEarlyTerminated = false (no budget set).
        synthesizerInput.SwarmContext?.EarlyTerminated.Should().Be(false);

        // Contributor #2 sees position 2 + max-N 2 + the prior contribution.
        var c2 = invoker.AllInvocationsFor("swarm-contributor").Last();
        c2.SwarmContext?.Position.Should().Be(2);
        c2.SwarmContext?.MaxN.Should().Be(2);
        c2.WorkflowContext.Should().ContainKey("swarmContributions");
        c2.WorkflowContext["swarmContributions"].GetArrayLength().Should().Be(1);

        // After the swarm exits, swarm-* keys are scrubbed from the workflow bag the sink sees.
        var sinkInvocation = invoker.LastInvocationFor("sink");
        sinkInvocation!.WorkflowContext.Should().NotContainKey("swarmMission");
        sinkInvocation.WorkflowContext.Should().NotContainKey("swarmContributions");
    }

    [Fact]
    public async Task SwarmNode_Sequential_AbstentionFlagSurfacesOnContributionEntry()
    {
        // Same shape as happy path — but contributor #1 abstains. The contribution still lands
        // on workflow.swarmContributions with `abstained: true`, and the synthesizer can read
        // the role of each contributor.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "the mission text");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: abstain\nNo competence to contribute.");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: analyst\nMy take.");
        invoker.Queue("swarm-synthesizer", new AgentDecision("Synthesized"), output: "synthesis-output");
        invoker.Queue("sink", new AgentDecision("Completed"), output: "sink-done");

        await using var harness = await SetupAsync(invoker);
        var workflowKey = await SeedSwarmHappyPathWorkflowAsync(harness.Services);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));

        var synthesizerInvocation = invoker.LastInvocationFor("swarm-synthesizer")!;
        var contributions = synthesizerInvocation.WorkflowContext["swarmContributions"];
        contributions.GetArrayLength().Should().Be(2);

        var c1 = contributions[0];
        c1.GetProperty("abstained").GetBoolean().Should().Be(true);
        c1.GetProperty("role").GetString().Should().Be("abstain");

        var c2 = contributions[1];
        c2.GetProperty("abstained").GetBoolean().Should().Be(false);
        c2.GetProperty("role").GetString().Should().Be("analyst");
    }

    [Fact]
    public async Task SwarmNode_Sequential_TokenBudgetExceeded_RunsSynthesizerEarly()
    {
        // n=4 contributors, but the budget is so small that the first contributor's tokens
        // already blow it. The saga skips contributors 2..4 and dispatches the synthesizer with
        // swarmEarlyTerminated=true after contributor #1.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "the mission text");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: analyst\nThe lone contribution.");
        invoker.Queue("swarm-synthesizer", new AgentDecision("Synthesized"),
            output: "early-synthesis-output");
        invoker.Queue("sink", new AgentDecision("Completed"), output: "sink-done");

        await using var harness = await SetupAsync(invoker);
        var workflowKey = await SeedSwarmTinyBudgetWorkflowAsync(harness.Services);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));

        // Only contributor #1 ran; contributor #2..#4 were skipped.
        invoker.CallsFor("swarm-contributor").Should().Be(1);
        invoker.CallsFor("swarm-synthesizer").Should().Be(1);

        // Synthesizer dispatch carried the early-termination flag on the SwarmContext.
        var synthesizerInvocation = invoker.LastInvocationFor("swarm-synthesizer")!;
        synthesizerInvocation.SwarmContext?.EarlyTerminated.Should().Be(true);
    }

    [Fact]
    public async Task SwarmNode_Coordinator_DispatchesParallelWorkersAndSynthesizes_HappyPath()
    {
        // Workflow: Start (kickoff) → Swarm (n=3, Coordinator) → Sink.
        // The coordinator emits a JSON array of three assignments. The runtime fans out three
        // parallel workers, each receiving its own assignment via the SwarmContext.Assignment
        // template variable. Each worker contributes; the synthesizer sees all three.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "the mission text");
        invoker.Queue("swarm-coordinator", new AgentDecision("Completed"),
            output: """["analyst: structural critique","critic: assumption check","historian: precedent search"]""");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: analyst\nFirst worker contribution.");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: critic\nSecond worker contribution.");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: historian\nThird worker contribution.");
        invoker.Queue("swarm-synthesizer", new AgentDecision("Synthesized"), output: "synthesis-output");
        invoker.Queue("sink", new AgentDecision("Completed"), output: "sink-done");

        await using var harness = await SetupAsync(invoker);
        var workflowKey = await SeedCoordinatorHappyPathWorkflowAsync(harness.Services, n: 3);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(
            nameof(WorkflowSagaStateMachine.Completed),
            because: $"saga should reach Completed; FailureReason: {saga.FailureReason ?? "<none>"}");

        // Decision ledger: kickoff, coordinator, three contributors, synthesizer, sink.
        var decisions = saga.Decisions.OrderBy(d => d.Ordinal).ToArray();
        var decisionSummary = string.Join(
            "; ",
            decisions.Select(d => $"{d.Ordinal}:{d.AgentKey}={d.Decision}"));
        decisions.Select(d => d.AgentKey).Should().ContainInOrder(
            new[] { "kickoff", "swarm-coordinator", "swarm-contributor", "swarm-contributor", "swarm-contributor", "swarm-synthesizer", "sink" },
            because: $"actual: [{decisionSummary}]; coordinator calls: {invoker.CallsFor("swarm-coordinator")}, "
                + $"worker calls: {invoker.CallsFor("swarm-contributor")}, "
                + $"synthesizer calls: {invoker.CallsFor("swarm-synthesizer")}");

        // Each worker received its 1-indexed Position + the configured MaxN of 3 + its assignment.
        var workerInvocations = invoker.AllInvocationsFor("swarm-contributor");
        workerInvocations.Should().HaveCount(3);
        workerInvocations.Select(i => i.SwarmContext?.Position).Should().BeEquivalentTo(new int?[] { 1, 2, 3 });
        workerInvocations.Should().OnlyContain(i => i.SwarmContext != null && i.SwarmContext.MaxN == 3);

        var assignmentsByPosition = workerInvocations
            .ToDictionary(i => i.SwarmContext!.Position!.Value, i => i.SwarmContext!.Assignment);
        assignmentsByPosition[1].Should().Be("analyst: structural critique");
        assignmentsByPosition[2].Should().Be("critic: assumption check");
        assignmentsByPosition[3].Should().Be("historian: precedent search");

        // The coordinator's own dispatch carried MaxN but no Position / Assignment.
        var coordinatorInvocation = invoker.LastInvocationFor("swarm-coordinator")!;
        coordinatorInvocation.SwarmContext?.Position.Should().BeNull();
        coordinatorInvocation.SwarmContext?.MaxN.Should().Be(3);
        coordinatorInvocation.SwarmContext?.Assignment.Should().BeNull();

        // The synthesizer saw three contributions on workflow.swarmContributions.
        var synthesizerInput = invoker.LastInvocationFor("swarm-synthesizer")!;
        synthesizerInput.WorkflowContext.Should().ContainKey("swarmContributions");
        synthesizerInput.WorkflowContext["swarmContributions"].GetArrayLength().Should().Be(3);
        synthesizerInput.SwarmContext?.EarlyTerminated.Should().Be(false);

        // After the swarm exits, swarm-* keys are scrubbed from the workflow bag the sink sees.
        var sinkInvocation = invoker.LastInvocationFor("sink");
        sinkInvocation!.WorkflowContext.Should().NotContainKey("swarmMission");
        sinkInvocation.WorkflowContext.Should().NotContainKey("swarmContributions");
    }

    [Fact]
    public async Task SwarmNode_Coordinator_ReturnsFewerThanN_DispatchesOnlyAssigned()
    {
        // n=4 max, but the coordinator chooses to use only 2 workers. The runtime caps to the
        // coordinator's actual count and dispatches 2 — not 4.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "the mission text");
        invoker.Queue("swarm-coordinator", new AgentDecision("Completed"),
            output: """["only-needed-1","only-needed-2"]""");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: analyst\nFirst.");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: critic\nSecond.");
        invoker.Queue("swarm-synthesizer", new AgentDecision("Synthesized"), output: "synthesis-output");
        invoker.Queue("sink", new AgentDecision("Completed"), output: "sink-done");

        await using var harness = await SetupAsync(invoker);
        var workflowKey = await SeedCoordinatorHappyPathWorkflowAsync(harness.Services, n: 4);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
        invoker.CallsFor("swarm-contributor").Should().Be(2);
        invoker.CallsFor("swarm-synthesizer").Should().Be(1);

        // Workers carried MaxN = 4 (the configured cap), not 2 (the coordinator's actual count).
        var workerInvocations = invoker.AllInvocationsFor("swarm-contributor");
        workerInvocations.Should().OnlyContain(i => i.SwarmContext != null && i.SwarmContext.MaxN == 4);

        // Synthesizer saw 2 contributions, not 4.
        var synthesizerInput = invoker.LastInvocationFor("swarm-synthesizer")!;
        synthesizerInput.WorkflowContext["swarmContributions"].GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task SwarmNode_Coordinator_TokenBudgetOver10Pct_FailsBeforeSynthesizer()
    {
        // Budget = 4 tokens. Coordinator + 3 workers each spend 2 tokens (token usage is per
        // ScriptedStep: input=1, output=1 → 2 per call). Cumulative after coordinator + 3 workers
        // = 8 tokens — exactly 100% over. Hard cap is budget * 1.10 = 4.4; 8 > 4.4 so synthesizer
        // is skipped and the saga terminates Failed with a budget-exceeded reason.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "the mission text");
        invoker.Queue("swarm-coordinator", new AgentDecision("Completed"),
            output: """["a","b","c"]""");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: a\none.");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: b\ntwo.");
        invoker.Queue("swarm-contributor", new AgentDecision("Contributed"),
            output: "ROLE: c\nthree.");

        await using var harness = await SetupAsync(invoker);
        var workflowKey = await SeedCoordinatorBudgetWorkflowAsync(harness.Services, n: 3, tokenBudget: 4);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
        saga.FailureReason.Should().Contain("budget exceeded");
        invoker.CallsFor("swarm-synthesizer").Should().Be(
            0,
            because: "all three workers ran but the synthesizer was skipped because budget > 10% over.");
    }

    [Fact]
    public async Task SwarmNode_Coordinator_ReturnsMalformedAssignments_TerminatesFailed()
    {
        // Coordinator returns invalid JSON. Saga records the coordinator's decision row, then the
        // assignment-parser flags the malformed payload and the saga terminates Failed without
        // dispatching any workers.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff", new AgentDecision("Completed"), output: "the mission text");
        invoker.Queue("swarm-coordinator", new AgentDecision("Completed"), output: "not even close to JSON");

        await using var harness = await SetupAsync(invoker);
        var workflowKey = await SeedCoordinatorHappyPathWorkflowAsync(harness.Services, n: 2);
        await harness.StartAsync();

        var traceId = Guid.NewGuid();
        await PublishKickoffAsync(harness, workflowKey, traceId);
        var saga = await WaitForTerminalStateAsync(harness.Services, traceId, TimeSpan.FromSeconds(120));

        saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
        saga.FailureReason.Should().Contain("malformed assignments");
        invoker.CallsFor("swarm-contributor").Should().Be(0);
        invoker.CallsFor("swarm-synthesizer").Should().Be(0);
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
            "swarm-contributor",
            "swarm-synthesizer",
            "swarm-coordinator",
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

    private static async Task<string> SeedSwarmHappyPathWorkflowAsync(IServiceProvider services)
        => await SeedSwarmWorkflowAsync(services, key: $"swarm-happy-{Guid.NewGuid():N}", n: 2, tokenBudget: null);

    private static async Task<string> SeedSwarmTinyBudgetWorkflowAsync(IServiceProvider services)
        => await SeedSwarmWorkflowAsync(services, key: $"swarm-tiny-budget-{Guid.NewGuid():N}", n: 4, tokenBudget: 1);

    private static async Task<string> SeedCoordinatorHappyPathWorkflowAsync(IServiceProvider services, int n)
        => await SeedSwarmWorkflowAsync(
            services,
            key: $"swarm-coord-happy-{Guid.NewGuid():N}",
            n: n,
            tokenBudget: null,
            protocol: "Coordinator");

    private static async Task<string> SeedCoordinatorBudgetWorkflowAsync(
        IServiceProvider services,
        int n,
        int tokenBudget)
        => await SeedSwarmWorkflowAsync(
            services,
            key: $"swarm-coord-budget-{Guid.NewGuid():N}",
            n: n,
            tokenBudget: tokenBudget,
            protocol: "Coordinator");

    private static async Task<string> SeedSwarmWorkflowAsync(
        IServiceProvider services,
        string key,
        int n,
        int? tokenBudget,
        string protocol = "Sequential")
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var swarmNode = Guid.NewGuid();
        var sinkNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";
        const string sinkPortsJson = """["Completed","Failed"]""";
        const string swarmPortsJson = """["Synthesized","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = key,
            Version = 1,
            Name = $"Swarm test {key}",
            MaxRoundsPerRound = 50,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = swarmNode,
                    Kind = WorkflowNodeKind.Swarm,
                    OutputPortsJson = swarmPortsJson,
                    SwarmProtocol = protocol,
                    SwarmN = n,
                    ContributorAgentKey = "swarm-contributor",
                    ContributorAgentVersion = 1,
                    SynthesizerAgentKey = "swarm-synthesizer",
                    SynthesizerAgentVersion = 1,
                    CoordinatorAgentKey = protocol == "Coordinator" ? "swarm-coordinator" : null,
                    CoordinatorAgentVersion = protocol == "Coordinator" ? 1 : null,
                    SwarmTokenBudget = tokenBudget,
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
                    ToNodeId = swarmNode,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    SortOrder = 0,
                },
                new WorkflowEdgeEntity
                {
                    FromNodeId = swarmNode,
                    FromPort = "Synthesized",
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
    /// Like <see cref="WorkflowSagaTransformEndToEndTests"/>'s recorder but captures the entire
    /// workflow context + swarm context per call so swarm tests can assert on per-position
    /// template variables and accumulated contributions.
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

        public RecordedInvocation? LastInvocationFor(string agentKey) =>
            AllInvocationsFor(agentKey).LastOrDefault();

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

            // Reverse-engineer the workflow + swarm context from the agent's flat variable scope.
            // The saga publishes the message; AgentInvocationConsumer flattens it into
            // configuration.Variables. We pull the workflow.* keys and swarm-prefixed top-levels
            // back out so tests can assert on the structured payload the saga assembled.
            var workflowContext = ExtractWorkflowContext(configuration);
            var swarmContext = ExtractSwarmContext(configuration);

            var list = invocations.GetOrAdd(agentKey, _ => []);
            lock (list)
            {
                list.Add(new RecordedInvocation(input, workflowContext, swarmContext));
            }
            callCounts.AddOrUpdate(agentKey, 1, (_, count) => count + 1);

            return Task.FromResult(new AgentInvocationResult(
                Output: step.Output,
                Decision: step.Decision,
                Transcript: [],
                TokenUsage: new Runtime.TokenUsage(1, 1, 2),
                ToolCallsExecuted: 0));
        }

        private static IReadOnlyDictionary<string, JsonElement> ExtractWorkflowContext(
            AgentInvocationConfiguration configuration)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (configuration.Variables is null)
            {
                return result;
            }

            foreach (var (key, value) in configuration.Variables)
            {
                const string prefix = "workflow.";
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                // Skip nested flattened keys like workflow.swarmContributions.0 — only keep the
                // root entries which carry the full JSON payload.
                var tail = key[prefix.Length..];
                if (tail.Contains('.'))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                JsonElement element;
                try
                {
                    using var doc = JsonDocument.Parse(value);
                    element = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    element = JsonSerializer.SerializeToElement(value);
                }

                result[tail] = element;
            }

            return result;
        }

        private static SwarmInvocationContext? ExtractSwarmContext(
            AgentInvocationConfiguration configuration)
        {
            if (configuration.Variables is null)
            {
                return null;
            }

            int? position = null;
            int? maxN = null;
            string? assignment = null;
            bool? earlyTerminated = null;

            if (configuration.Variables.TryGetValue("swarmPosition", out var p)
                && int.TryParse(p, out var positionValue))
            {
                position = positionValue;
            }

            if (configuration.Variables.TryGetValue("swarmMaxN", out var m)
                && int.TryParse(m, out var maxNValue))
            {
                maxN = maxNValue;
            }

            if (configuration.Variables.TryGetValue("swarmAssignment", out var a))
            {
                assignment = a;
            }

            if (configuration.Variables.TryGetValue("swarmEarlyTerminated", out var e))
            {
                earlyTerminated = string.Equals(e, "true", StringComparison.Ordinal);
            }

            if (position is null && maxN is null && assignment is null && earlyTerminated is null)
            {
                return null;
            }

            return new SwarmInvocationContext(position, maxN, assignment, earlyTerminated);
        }

        private sealed record ScriptedStep(AgentDecision Decision, string Output);
    }

    private sealed record RecordedInvocation(
        string? Input,
        IReadOnlyDictionary<string, JsonElement> WorkflowContext,
        SwarmInvocationContext? SwarmContext);
}
