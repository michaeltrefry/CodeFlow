using CodeFlow.Orchestration.DryRun;
using CodeFlow.Orchestration.Replay;
using CodeFlow.Persistence;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Replay;

public sealed class ReplayEditsApplicatorTests
{
    private static readonly Guid StartId = Guid.Parse("cccc1111-1111-1111-1111-cccccccccccc");
    private static readonly Guid ProducerId = Guid.Parse("cccc2222-2222-2222-2222-cccccccccccc");
    private static readonly Guid ReviewerId = Guid.Parse("cccc3333-3333-3333-3333-cccccccccccc");

    [Fact]
    public async Task Apply_NoEdits_PreservesQueues()
    {
        var workflow = BuildWorkflow();
        var ports = await BuildPortsAsync(workflow);
        var baseMocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["producer"] = new[] { new DryRunMockResponse("Completed", "v1", null) },
            ["reviewer"] = new[] { new DryRunMockResponse("Approved", "ship it", null) },
        };

        var result = ReplayEditsApplicator.Apply(baseMocks, edits: null, additionalMocks: null, ports, "'edits-test' v1");

        result.ValidationErrors.Should().BeEmpty();
        result.Mocks["producer"].Should().BeEquivalentTo(baseMocks["producer"]);
        result.Mocks["reviewer"].Should().BeEquivalentTo(baseMocks["reviewer"]);
    }

    [Fact]
    public async Task Apply_SingleEditOverridesDecisionAndOutput()
    {
        var workflow = BuildWorkflow();
        var ports = await BuildPortsAsync(workflow);
        var baseMocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["reviewer"] = new[]
            {
                new DryRunMockResponse("Rejected", "no", null),
                new DryRunMockResponse("Rejected", "still no", null),
                new DryRunMockResponse("Rejected", "no again", null),
            },
        };
        var edits = new[]
        {
            new ReplayEdit("reviewer", Ordinal: 3, Decision: "Approved", Output: "ok now", Payload: null),
        };

        var result = ReplayEditsApplicator.Apply(baseMocks, edits, additionalMocks: null, ports, "'edits-test' v1");

        result.ValidationErrors.Should().BeEmpty();
        result.Mocks["reviewer"][0].Decision.Should().Be("Rejected");
        result.Mocks["reviewer"][2].Decision.Should().Be("Approved");
        result.Mocks["reviewer"][2].Output.Should().Be("ok now");
    }

    [Fact]
    public async Task Apply_OrdinalOutOfRange_ReturnsValidationError()
    {
        var workflow = BuildWorkflow();
        var ports = await BuildPortsAsync(workflow);
        var baseMocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["reviewer"] = new[] { new DryRunMockResponse("Rejected", "no", null) },
        };
        var edits = new[]
        {
            new ReplayEdit("reviewer", Ordinal: 5, Decision: "Approved", Output: null, Payload: null),
        };

        var result = ReplayEditsApplicator.Apply(baseMocks, edits, additionalMocks: null, ports, "'edits-test' v1");

        result.ValidationErrors.Should().ContainSingle(e => e.Contains("ordinal 5"));
    }

    [Fact]
    public async Task Apply_PortNotDeclaredOnAgent_ReturnsValidationError()
    {
        var workflow = BuildWorkflow();
        var ports = await BuildPortsAsync(workflow);
        var baseMocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["reviewer"] = new[] { new DryRunMockResponse("Approved", "ok", null) },
        };
        var edits = new[]
        {
            new ReplayEdit("reviewer", Ordinal: 1, Decision: "NeverDeclared", Output: null, Payload: null),
        };

        var result = ReplayEditsApplicator.Apply(baseMocks, edits, additionalMocks: null, ports, "'edits-test' v1");

        result.ValidationErrors.Should().ContainSingle(e => e.Contains("NeverDeclared"));
    }

    [Fact]
    public async Task Apply_EditOnNonExistentAgentKey_ReturnsValidationError()
    {
        var workflow = BuildWorkflow();
        var ports = await BuildPortsAsync(workflow);
        var baseMocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["reviewer"] = new[] { new DryRunMockResponse("Approved", "ok", null) },
        };
        var edits = new[]
        {
            new ReplayEdit("ghost", Ordinal: 1, Decision: "Approved", Output: null, Payload: null),
        };

        var result = ReplayEditsApplicator.Apply(baseMocks, edits, additionalMocks: null, ports, "'edits-test' v1");

        result.ValidationErrors.Should().ContainSingle(e => e.Contains("ghost"));
    }

    [Fact]
    public async Task Apply_AdditionalMocks_AppendedToQueue()
    {
        var workflow = BuildWorkflow();
        var ports = await BuildPortsAsync(workflow);
        var baseMocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["reviewer"] = new[] { new DryRunMockResponse("Rejected", "no", null) },
        };
        var additional = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["reviewer"] = new[]
            {
                new DryRunMockResponse("Approved", "appended", null),
            },
        };

        var result = ReplayEditsApplicator.Apply(baseMocks, edits: null, additional, ports, "'edits-test' v1");

        result.ValidationErrors.Should().BeEmpty();
        result.Mocks["reviewer"].Should().HaveCount(2);
        result.Mocks["reviewer"][1].Decision.Should().Be("Approved");
    }

    [Fact]
    public async Task Apply_FailedPort_AlwaysAcceptedEvenIfNotExplicitlyDeclared()
    {
        var workflow = BuildWorkflow();
        var ports = await BuildPortsAsync(workflow);
        var baseMocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["reviewer"] = new[] { new DryRunMockResponse("Approved", "ok", null) },
        };
        var edits = new[]
        {
            new ReplayEdit("reviewer", Ordinal: 1, Decision: "Failed", Output: null, Payload: null),
        };

        var result = ReplayEditsApplicator.Apply(baseMocks, edits, additionalMocks: null, ports, "'edits-test' v1");

        result.ValidationErrors.Should().BeEmpty();
        result.Mocks["reviewer"][0].Decision.Should().Be("Failed");
    }

    [Fact]
    public async Task BuildPortIndex_WalksSubflowSubtree()
    {
        var (outer, inner) = BuildOuterInnerPair();
        var repo = new SimpleRepository(outer, inner);

        var ports = await ReplayEditsApplicator.BuildPortIndexAsync(outer, repo, CancellationToken.None);

        ports.Should().ContainKey("producer");
        ports.Should().ContainKey("reviewer");
        ports["reviewer"].Should().BeEquivalentTo(new[] { "Approved", "Rejected", "Failed" });
    }

    private static Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> BuildPortsAsync(Workflow workflow) =>
        ReplayEditsApplicator.BuildPortIndexAsync(workflow, new SimpleRepository(workflow), CancellationToken.None);

    private static Workflow BuildWorkflow() =>
        new(
            Key: "edits-test",
            Version: 1,
            Name: "edits",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: StartId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: ProducerId, Kind: WorkflowNodeKind.Agent, AgentKey: "producer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0),
                new WorkflowNode(
                    Id: ReviewerId, Kind: WorkflowNodeKind.Agent, AgentKey: "reviewer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Approved", "Rejected" }, LayoutX: 200, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(StartId, "Completed", ProducerId, "in", false, 0),
                new WorkflowEdge(ProducerId, "Completed", ReviewerId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

    private static (Workflow Outer, Workflow Inner) BuildOuterInnerPair()
    {
        var outerStartId = Guid.NewGuid();
        var loopId = Guid.NewGuid();
        var innerStartId = Guid.NewGuid();
        var innerProducer = Guid.NewGuid();
        var innerReviewer = Guid.NewGuid();

        var inner = new Workflow(
            Key: "inner",
            Version: 1,
            Name: "inner",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: innerStartId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: innerProducer, Kind: WorkflowNodeKind.Agent, AgentKey: "producer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0),
                new WorkflowNode(
                    Id: innerReviewer, Kind: WorkflowNodeKind.Agent, AgentKey: "reviewer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Approved", "Rejected" }, LayoutX: 200, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        var outer = new Workflow(
            Key: "outer",
            Version: 1,
            Name: "outer",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: outerStartId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: loopId, Kind: WorkflowNodeKind.ReviewLoop, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Approved", "Exhausted" },
                    LayoutX: 100, LayoutY: 0,
                    SubflowKey: "inner", SubflowVersion: 1,
                    ReviewMaxRounds: 3, LoopDecision: "Rejected"),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        return (outer, inner);
    }

    private sealed class SimpleRepository : IWorkflowRepository
    {
        private readonly Dictionary<string, Workflow> byKey;

        public SimpleRepository(params Workflow[] workflows)
        {
            byKey = workflows.ToDictionary(w => w.Key, StringComparer.Ordinal);
        }

        public Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default) =>
            byKey.TryGetValue(key, out var w)
                ? Task.FromResult(w)
                : throw new InvalidOperationException($"Unknown workflow '{key}' v{version}.");

        public Task<Workflow?> GetLatestAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(byKey.TryGetValue(key, out var w) ? w : null);

        public Task<IReadOnlyList<Workflow>> ListLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workflow>>(byKey.Values.ToArray());

        public Task<IReadOnlyList<Workflow>> ListVersionsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workflow>>(byKey.TryGetValue(key, out var w) ? new[] { w } : Array.Empty<Workflow>());

        public Task<WorkflowEdge?> FindNextAsync(string key, int version, Guid fromNodeId, string outputPortName, CancellationToken cancellationToken = default) =>
            Task.FromResult(byKey[key].FindNext(fromNodeId, outputPortName));

        public Task<IReadOnlyCollection<string>> GetTerminalPortsAsync(string key, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult(byKey[key].TerminalPorts);

        public Task<int> CreateNewVersionAsync(WorkflowDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
