using CodeFlow.Persistence;
using FluentAssertions;

namespace CodeFlow.Persistence.Tests;

/// <summary>
/// sc-628 unit coverage of <see cref="BoundaryScriptPorts.GetDeclaredPorts"/> — the shared
/// computation the saga, the DryRunExecutor, and (slice 3) the editor's <c>/validate-script</c>
/// path all rely on. If this drifts, scripts pick ports the runtime later rejects.
/// </summary>
public sealed class BoundaryScriptPortsTests
{
    private static readonly Guid NodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Subflow_AlwaysIncludesImplicitFailed_OnTopOfDeclaredPorts()
    {
        var node = MakeNode(WorkflowNodeKind.Subflow, outputPorts: new[] { "Approved", "Rejected" });

        var ports = BoundaryScriptPorts.GetDeclaredPorts(node);

        ports.Should().BeEquivalentTo(new[] { "Approved", "Rejected", "Failed" });
    }

    [Fact]
    public void Subflow_FoldsChildTerminalsIntoSet_WhenSupplied()
    {
        // Author may not yet have propagated child terminal port names into OutputPorts; the
        // server-side validator path passes them explicitly so the script can target them.
        var node = MakeNode(WorkflowNodeKind.Subflow, outputPorts: new[] { "Approved" });

        var ports = BoundaryScriptPorts.GetDeclaredPorts(
            node,
            childTerminals: new[] { "Approved", "FromChild" });

        ports.Should().BeEquivalentTo(new[] { "Approved", "FromChild", "Failed" });
    }

    [Fact]
    public void ReviewLoop_AddsExhaustedAndDefaultLoopDecision_WhenLoopDecisionUnset()
    {
        var node = MakeNode(WorkflowNodeKind.ReviewLoop, outputPorts: new[] { "Approved" });

        var ports = BoundaryScriptPorts.GetDeclaredPorts(node);

        ports.Should().BeEquivalentTo(new[] { "Approved", "Failed", "Exhausted", "Rejected" });
    }

    [Fact]
    public void ReviewLoop_RespectsCustomLoopDecision_OverridingDefault()
    {
        // Authors override loopDecision to drive iteration off any port name (e.g. an interview
        // loop where "Answered" is the iterate signal). The boundary script may still route to
        // that port post-loop — it's a wirable choice.
        var node = MakeNode(
            WorkflowNodeKind.ReviewLoop,
            outputPorts: new[] { "Concluded" },
            loopDecision: "MoreQuestions");

        var ports = BoundaryScriptPorts.GetDeclaredPorts(node);

        ports.Should().BeEquivalentTo(new[] { "Concluded", "Failed", "Exhausted", "MoreQuestions" });
        ports.Should().NotContain("Rejected", because: "the custom loopDecision overrides the default 'Rejected'.");
    }

    [Fact]
    public void EmptyOutputPorts_StillIncludesImplicitFailed()
    {
        var node = MakeNode(WorkflowNodeKind.Subflow, outputPorts: Array.Empty<string>());

        var ports = BoundaryScriptPorts.GetDeclaredPorts(node);

        ports.Should().Equal(new[] { "Failed" });
    }

    [Fact]
    public void ReviewLoop_ChildTerminalsAndDeclaredOutputsMerge_WithoutDuplicates()
    {
        var node = MakeNode(
            WorkflowNodeKind.ReviewLoop,
            outputPorts: new[] { "Approved" },
            loopDecision: "Rejected");

        var ports = BoundaryScriptPorts.GetDeclaredPorts(
            node,
            childTerminals: new[] { "Approved", "Rejected", "Stalled" });

        // Approved declared + childTerminals union, plus implicit Failed and synthesized
        // Exhausted. loopDecision (=Rejected) is also there but only once even though the
        // child terminals list it.
        ports.Should().BeEquivalentTo(new[] { "Approved", "Rejected", "Stalled", "Failed", "Exhausted" });
    }

    [Fact]
    public void ThrowsWhenNodeIsNull()
    {
        var act = () => BoundaryScriptPorts.GetDeclaredPorts(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private static WorkflowNode MakeNode(
        WorkflowNodeKind kind,
        IReadOnlyList<string> outputPorts,
        string? loopDecision = null) =>
        new(
            Id: NodeId,
            Kind: kind,
            AgentKey: null,
            AgentVersion: null,
            OutputScript: null,
            OutputPorts: outputPorts,
            LayoutX: 0,
            LayoutY: 0,
            LoopDecision: loopDecision,
            ReviewMaxRounds: kind == WorkflowNodeKind.ReviewLoop ? 3 : null,
            SubflowKey: kind is WorkflowNodeKind.Subflow or WorkflowNodeKind.ReviewLoop ? "child" : null,
            SubflowVersion: kind is WorkflowNodeKind.Subflow or WorkflowNodeKind.ReviewLoop ? 1 : null);
}
