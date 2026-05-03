using FluentAssertions;

namespace CodeFlow.Persistence.Tests;

public sealed class WorkflowJsonTests
{
    [Fact]
    public void DeserializePorts_RoundTripsUserDefinedPorts()
    {
        var json = WorkflowJson.SerializePorts(new[] { "Approved", "Rejected", "NeedsReview" });
        var ports = WorkflowJson.DeserializePorts(json);
        ports.Should().Equal("Approved", "Rejected", "NeedsReview");
    }

    [Fact]
    public void DeserializePorts_StripsImplicitFailedPort()
    {
        // Heal legacy workflows that landed in the DB with `Failed` redundantly persisted in
        // OutputPorts (saved before the importer's apply path started running WorkflowValidator
        // on 2026-04-30). Without this scrub the editor can't save any edit to such a workflow
        // because the validator's reserved-port rule rejects the unmodified node list the editor
        // re-posts.
        var json = WorkflowJson.SerializePorts(new[] { "Approved", "Failed", "Rejected" });
        var ports = WorkflowJson.DeserializePorts(json);
        ports.Should().Equal("Approved", "Rejected");
    }

    [Fact]
    public void DeserializePorts_StripsReviewLoopExhaustedPort()
    {
        // Same provenance as the Failed scrub: pre-fix ReviewLoop nodes commonly carried
        // `Exhausted` in their persisted OutputPorts. The runtime synthesizes this port
        // unconditionally (Workflow.ComputeTerminalPorts), so stripping it on read is
        // behavior-neutral and re-enables editing.
        var json = WorkflowJson.SerializePorts(new[] { "Approved", "Exhausted" });
        var ports = WorkflowJson.DeserializePorts(json);
        ports.Should().Equal("Approved");
    }

    [Fact]
    public void DeserializePorts_StripsBothReservedPortsAndIgnoresBlankEntries()
    {
        var json = WorkflowJson.SerializePorts(new[] { "Approved", "Failed", "", "Exhausted", " ", "Done" });
        var ports = WorkflowJson.DeserializePorts(json);
        ports.Should().Equal("Approved", "Done");
    }

    [Fact]
    public void DeserializePorts_NullOrEmpty_ReturnsEmptyArray()
    {
        WorkflowJson.DeserializePorts(null).Should().BeEmpty();
        WorkflowJson.DeserializePorts(string.Empty).Should().BeEmpty();
        WorkflowJson.DeserializePorts("   ").Should().BeEmpty();
    }
}
