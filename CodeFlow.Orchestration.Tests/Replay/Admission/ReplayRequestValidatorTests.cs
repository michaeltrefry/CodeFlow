using System.Text.Json.Nodes;
using CodeFlow.Orchestration.DryRun;
using CodeFlow.Orchestration.Replay;
using CodeFlow.Orchestration.Replay.Admission;
using CodeFlow.Runtime.Authority.Admission;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Replay.Admission;

/// <summary>
/// sc-272 PR3 — exercises <see cref="ReplayRequestValidator"/>. The validator-then-execute
/// flow inside <see cref="CodeFlow.Api.Endpoints.TracesReplayEndpoints"/> is covered by the
/// existing integration tests (one already asserts that bad-ordinal edits return 400);
/// this fixture covers the closed-form validator surface directly.
/// </summary>
public sealed class ReplayRequestValidatorTests
{
    [Fact]
    public void Validate_HappyPath_NoDrift_NoEdits_AdmitsWithMocksAndPinnedVersions()
    {
        var validator = new ReplayRequestValidator();
        var input = NewInput();

        var admission = validator.Validate(input);

        var admitted = admission.Should().BeOfType<Accepted<AdmittedReplayRequest>>().Subject.Value;
        admitted.ParentTraceId.Should().Be(input.ParentTraceId);
        admitted.WorkflowKey.Should().Be(input.WorkflowKey);
        admitted.OriginalWorkflowVersion.Should().Be(input.OriginalWorkflowVersion);
        admitted.TargetWorkflowVersion.Should().Be(input.TargetWorkflowVersion);
        admitted.PinnedAgentVersions.Should().BeEquivalentTo(input.PinnedAgentVersions);
        admitted.Decisions.Should().BeEquivalentTo(input.MockBundle.Decisions);
        admitted.Force.Should().BeFalse();
        admitted.Drift.Level.Should().Be(DriftLevel.None);
        admitted.Mocks.Should().ContainKey("agent-a");
    }

    [Fact]
    public void Validate_HardDriftWithoutForce_RejectsWithDriftRefusedCode()
    {
        var validator = new ReplayRequestValidator();
        var input = NewInput(drift: new DriftReport(
            DriftLevel.Hard,
            new[] { "node 'foo' kind changed from Agent to Logic" }));

        var admission = validator.Validate(input);

        var rejected = admission.Should().BeOfType<Rejected<AdmittedReplayRequest>>().Subject;
        rejected.Reason.Code.Should().Be("replay-drift-hard-refused");
        rejected.Reason.Axis.Should().Be("replay");
        rejected.Reason.Detail!["driftLevel"]!.GetValue<string>().Should().Be("Hard");
        rejected.Reason.Detail!["warnings"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void Validate_HardDriftWithForce_AdmitsWithForceFlagSet()
    {
        var validator = new ReplayRequestValidator();
        var input = NewInput(
            drift: new DriftReport(DriftLevel.Hard, new[] { "node kind changed" }),
            force: true);

        var admission = validator.Validate(input);

        var admitted = admission.Should().BeOfType<Accepted<AdmittedReplayRequest>>().Subject.Value;
        admitted.Force.Should().BeTrue();
        admitted.Drift.Level.Should().Be(DriftLevel.Hard);
    }

    [Fact]
    public void Validate_SoftDrift_AdmitsRegardlessOfForce()
    {
        // Soft drift is informational — surfaces warnings inside the admitted value but never
        // refuses. Authors learn the workflow shifted without losing the ability to replay.
        var validator = new ReplayRequestValidator();
        var input = NewInput(drift: new DriftReport(
            DriftLevel.Soft,
            new[] { "node added downstream of last decision" }));

        var admission = validator.Validate(input);

        admission.Should().BeOfType<Accepted<AdmittedReplayRequest>>()
            .Which.Value.Drift.Level.Should().Be(DriftLevel.Soft);
    }

    [Fact]
    public void Validate_EditOrdinalOutOfRange_RejectsWithEditValidationCode()
    {
        var validator = new ReplayRequestValidator();
        var input = NewInput(edits: new[]
        {
            new ReplayEdit("agent-a", Ordinal: 99, Decision: "Completed", Output: null, Payload: null),
        });

        var admission = validator.Validate(input);

        var rejected = admission.Should().BeOfType<Rejected<AdmittedReplayRequest>>().Subject;
        rejected.Reason.Code.Should().Be("replay-edit-validation");
        rejected.Reason.Detail!["errors"]!.AsArray()
            .Should().ContainSingle(node => node!.GetValue<string>().Contains("ordinal 99"));
    }

    [Fact]
    public void Validate_EditDecisionNotDeclared_RejectsWithEditValidationCode()
    {
        var validator = new ReplayRequestValidator();
        var input = NewInput(edits: new[]
        {
            new ReplayEdit("agent-a", Ordinal: 1, Decision: "NotAPort", Output: null, Payload: null),
        });

        var admission = validator.Validate(input);

        admission.Should().BeOfType<Rejected<AdmittedReplayRequest>>()
            .Which.Reason.Code.Should().Be("replay-edit-validation");
    }

    [Fact]
    public void Validate_EditValidEdit_AppliesToAdmittedMocks()
    {
        var validator = new ReplayRequestValidator();
        var input = NewInput(edits: new[]
        {
            new ReplayEdit("agent-a", Ordinal: 1, Decision: "Approved", Output: "ok", Payload: null),
        });

        var admission = validator.Validate(input);

        var admitted = admission.Should().BeOfType<Accepted<AdmittedReplayRequest>>().Subject.Value;
        admitted.Mocks["agent-a"][0].Decision.Should().Be("Approved");
        admitted.Mocks["agent-a"][0].Output.Should().Be("ok");
    }

    [Fact]
    public void Validate_ReMint_SecondCallWithSameInputProducesEquivalentAdmittedValue()
    {
        // Re-mint discipline: replaying the same admission inputs produces an equivalent value
        // (modulo wall-clock fields). Demonstrated here with a fixed clock; the validator is a
        // pure function on its inputs so the assertion is exact.
        var fixedNow = DateTimeOffset.Parse("2026-04-30T16:00:00Z");
        var validator = new ReplayRequestValidator(nowProvider: () => fixedNow);
        var input = NewInput();

        var first = validator.Validate(input);
        var second = validator.Validate(input);

        var firstAdmitted = first.Should().BeOfType<Accepted<AdmittedReplayRequest>>().Subject.Value;
        var secondAdmitted = second.Should().BeOfType<Accepted<AdmittedReplayRequest>>().Subject.Value;
        secondAdmitted.AdmittedAt.Should().Be(firstAdmitted.AdmittedAt);
        secondAdmitted.ParentTraceId.Should().Be(firstAdmitted.ParentTraceId);
        secondAdmitted.WorkflowKey.Should().Be(firstAdmitted.WorkflowKey);
        secondAdmitted.TargetWorkflowVersion.Should().Be(firstAdmitted.TargetWorkflowVersion);
    }

    private static ReplayAdmissionRequest NewInput(
        DriftReport? drift = null,
        IReadOnlyList<ReplayEdit>? edits = null,
        bool force = false)
    {
        var bundle = new ReplayMockBundle(
            Mocks: new Dictionary<string, IReadOnlyList<DryRunMockResponse>>(StringComparer.Ordinal)
            {
                ["agent-a"] = new[]
                {
                    new DryRunMockResponse("Rejected", "no", null),
                },
            },
            Decisions: new[]
            {
                new RecordedDecisionRef(
                    AgentKey: "agent-a",
                    OrdinalPerAgent: 1,
                    SagaCorrelationId: Guid.NewGuid(),
                    SagaOrdinal: 1,
                    NodeId: Guid.NewGuid(),
                    RoundId: Guid.NewGuid(),
                    OriginalDecision: "Rejected"),
            });

        var ports = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["agent-a"] = new HashSet<string>(StringComparer.Ordinal) { "Approved", "Rejected", "Completed" },
        };

        return new ReplayAdmissionRequest(
            ParentTraceId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            WorkflowKey: "demo",
            OriginalWorkflowVersion: 1,
            TargetWorkflowVersion: 1,
            TargetWorkflowDisplayLabel: "'demo' v1",
            PinnedAgentVersions: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["agent-a"] = 3,
            },
            MockBundle: bundle,
            DeclaredPortsByAgent: ports,
            Edits: edits,
            AdditionalMocks: null,
            Force: force,
            Drift: drift ?? new DriftReport(DriftLevel.None, Array.Empty<string>()));
    }
}
