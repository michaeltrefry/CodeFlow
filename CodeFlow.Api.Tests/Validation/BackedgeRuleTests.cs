using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.Validation.Pipeline.Rules;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Tests for V6's <see cref="BackedgeRule"/>: cycle detection in the authored edge graph, with
/// the <see cref="WorkflowEdgeDto.IntentionalBackedge"/> opt-out.
/// </summary>
public sealed class BackedgeRuleTests
{
    [Fact]
    public async Task LogicNodeRoutingBackToStart_FiresWarning()
    {
        // Acceptance: a Logic node routing back into the Start node fires the warning.
        var startId = Guid.NewGuid();
        var logicId = Guid.NewGuid();
        var nodes = new[]
        {
            Node(startId, WorkflowNodeKind.Start),
            Node(logicId, WorkflowNodeKind.Logic),
        };
        var edges = new[]
        {
            Edge(startId, "Done", logicId),
            Edge(logicId, "Retry", startId),
        };

        var findings = await RunAsync(nodes, edges);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Warning);
        findings[0].RuleId.Should().Be("backedge");
        findings[0].Location!.EdgeFrom.Should().Be(logicId);
        findings[0].Location.EdgePort.Should().Be("Retry");
    }

    [Fact]
    public async Task LinearGraph_NoFindings()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var nodes = new[]
        {
            Node(a, WorkflowNodeKind.Start),
            Node(b, WorkflowNodeKind.Agent),
            Node(c, WorkflowNodeKind.Agent),
        };
        var edges = new[]
        {
            Edge(a, "Done", b),
            Edge(b, "Done", c),
        };

        var findings = await RunAsync(nodes, edges);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewLoopNodeWithoutAuthoredCycle_NoFindings()
    {
        // ReviewLoop iteration is internal — not represented as an authored edge in the parent
        // graph, so a Start → ReviewLoop → terminal flow has no cycles.
        var start = Guid.NewGuid();
        var loop = Guid.NewGuid();
        var nodes = new[]
        {
            Node(start, WorkflowNodeKind.Start),
            Node(loop, WorkflowNodeKind.ReviewLoop, subflowKey: "child"),
        };
        var edges = new[] { Edge(start, "Done", loop) };

        var findings = await RunAsync(nodes, edges);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task SelfLoopOnSingleNode_FiresWarning()
    {
        var node = Guid.NewGuid();
        var edges = new[] { Edge(node, "Retry", node) };

        var findings = await RunAsync(new[] { Node(node, WorkflowNodeKind.Logic) }, edges);

        findings.Should().ContainSingle()
            .Which.Severity.Should().Be(WorkflowValidationSeverity.Warning);
    }

    [Fact]
    public async Task IntentionalBackedge_DoesNotFire()
    {
        // Acceptance: once dismissed as intentional, the same edge does not warn on subsequent saves.
        var start = Guid.NewGuid();
        var logic = Guid.NewGuid();
        var nodes = new[]
        {
            Node(start, WorkflowNodeKind.Start),
            Node(logic, WorkflowNodeKind.Logic),
        };
        var edges = new[]
        {
            Edge(start, "Done", logic),
            Edge(logic, "Retry", start, intentionalBackedge: true),
        };

        var findings = await RunAsync(nodes, edges);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ThreeNodeCycle_FiresOneWarningOnTheClosingEdge()
    {
        // a → b → c → a. DFS rooted at a: walks a→b→c, and c→a sees a as Gray (ancestor) → backedge.
        // The forward edges a→b and b→c are tree edges, not backedges.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var nodes = new[]
        {
            Node(a, WorkflowNodeKind.Start),
            Node(b, WorkflowNodeKind.Agent),
            Node(c, WorkflowNodeKind.Logic),
        };
        var edges = new[]
        {
            Edge(a, "Done", b),
            Edge(b, "Done", c),
            Edge(c, "Loop", a),
        };

        var findings = await RunAsync(nodes, edges);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Warning);
        findings[0].Location!.EdgePort.Should().Be("Loop");
    }

    [Fact]
    public async Task Diamond_NoCycle_NoFindings()
    {
        // a → b → d
        // a → c → d   (diamond, not a cycle)
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var nodes = new[]
        {
            Node(a, WorkflowNodeKind.Start),
            Node(b, WorkflowNodeKind.Agent),
            Node(c, WorkflowNodeKind.Agent),
            Node(d, WorkflowNodeKind.Agent),
        };
        var edges = new[]
        {
            Edge(a, "Branch1", b),
            Edge(a, "Branch2", c),
            Edge(b, "Done", d),
            Edge(c, "Done", d),
        };

        var findings = await RunAsync(nodes, edges);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyGraph_NoFindings()
    {
        var findings = await RunAsync(
            Array.Empty<WorkflowNodeDto>(),
            Array.Empty<WorkflowEdgeDto>());

        findings.Should().BeEmpty();
    }

    private static WorkflowNodeDto Node(
        Guid id,
        WorkflowNodeKind kind,
        string? subflowKey = null) =>
        new(
            Id: id,
            Kind: kind,
            AgentKey: kind == WorkflowNodeKind.Agent || kind == WorkflowNodeKind.Start ? "x" : null,
            AgentVersion: kind == WorkflowNodeKind.Agent || kind == WorkflowNodeKind.Start ? 1 : null,
            OutputScript: null,
            OutputPorts: new[] { "Done" },
            LayoutX: 0, LayoutY: 0,
            SubflowKey: subflowKey,
            SubflowVersion: subflowKey is null ? null : 1);

    private static WorkflowEdgeDto Edge(
        Guid from,
        string fromPort,
        Guid to,
        bool intentionalBackedge = false) =>
        new(
            FromNodeId: from,
            FromPort: fromPort,
            ToNodeId: to,
            ToPort: WorkflowEdge.DefaultInputPort,
            RotatesRound: false,
            SortOrder: 0,
            IntentionalBackedge: intentionalBackedge);

    private static async Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
        IReadOnlyList<WorkflowNodeDto> nodes,
        IReadOnlyList<WorkflowEdgeDto> edges)
    {
        var rule = new BackedgeRule();
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
            .UseInMemoryDatabase($"backedge-tests-{Guid.NewGuid():N}")
            .Options;
        await using var db = new CodeFlowDbContext(options);
        var context = new WorkflowValidationContext(
            Key: "test-flow",
            Name: "Test flow",
            MaxRoundsPerRound: 3,
            Nodes: nodes,
            Edges: edges,
            Inputs: null,
            DbContext: db,
            WorkflowRepository: new WorkflowRepository(db),
            AgentRepository: new AgentConfigRepository(db),
            AgentRoleRepository: new AgentRoleRepository(db));
        return await rule.RunAsync(context, CancellationToken.None);
    }
}
