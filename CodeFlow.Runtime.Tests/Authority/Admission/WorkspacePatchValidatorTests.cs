using CodeFlow.Runtime.Authority.Admission;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Authority.Admission;

/// <summary>
/// sc-272 PR1 — exercises <see cref="WorkspacePatchValidator"/> on its own. The
/// validator-then-execute flow inside <see cref="WorkspaceHostToolService"/> is covered
/// by the existing <c>WorkspaceHostToolServiceHardeningTests</c>; this file covers the
/// minted <see cref="AdmittedWorkspacePatch"/> contract and re-mint behaviour directly.
/// </summary>
public sealed class WorkspacePatchValidatorTests : IDisposable
{
    private readonly string workspaceRoot;

    public WorkspacePatchValidatorTests()
    {
        workspaceRoot = Path.Combine(Path.GetTempPath(), "codeflow-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Validate_AddFile_ProducesAdmittedPatchWithSourceText()
    {
        var validator = NewValidator();
        var patch = """
            *** Begin Patch
            *** Add File: src/new.txt
            +alpha
            *** End Patch
            """;

        var admission = validator.Validate(NewRequest(patch));

        admission.IsAccepted.Should().BeTrue();
        var admitted = admission.Should().BeOfType<Accepted<AdmittedWorkspacePatch>>().Subject.Value;
        admitted.WorkspaceRootPath.Should().Be(workspaceRoot);
        admitted.SourcePatchText.Should().Be(patch);
        admitted.CommandCount.Should().Be(1);
    }

    [Fact]
    public void Validate_MalformedPatch_RejectsWithPatchMalformedCode()
    {
        var validator = NewValidator();
        var admission = validator.Validate(NewRequest("not a patch at all"));

        var rejected = admission.Should().BeOfType<Rejected<AdmittedWorkspacePatch>>().Subject;
        rejected.Reason.Code.Should().Be("patch-malformed");
        rejected.Reason.Axis.Should().Be("workspace-mutation");
    }

    [Fact]
    public void Validate_PathEscapingWorkspaceRoot_RejectsWithPathConfinementCode()
    {
        var validator = NewValidator();
        var patch = """
            *** Begin Patch
            *** Add File: ../escape.txt
            +pwned
            *** End Patch
            """;

        var admission = validator.Validate(NewRequest(patch));

        var rejected = admission.Should().BeOfType<Rejected<AdmittedWorkspacePatch>>().Subject;
        rejected.Reason.Code.Should().Be("path-confinement");
        rejected.Reason.Axis.Should().Be("workspace-mutation");
        rejected.Reason.Path.Should().Be("../escape.txt");
    }

    [Fact]
    public void Validate_UpdateMoveTo_PathConfinedOnDestination()
    {
        WriteFile("src/from.txt", "line\n");
        var validator = NewValidator();
        var patch = """
            *** Begin Patch
            *** Update File: src/from.txt
            *** Move to: ../escape.txt
             line
            *** End Patch
            """;

        var admission = validator.Validate(NewRequest(patch));

        var rejected = admission.Should().BeOfType<Rejected<AdmittedWorkspacePatch>>().Subject;
        rejected.Reason.Code.Should().Be("path-confinement");
        rejected.Reason.Path.Should().Be("../escape.txt");
    }

    [Fact]
    public void Validate_PatchOverSymlinkPath_RejectsWhenPolicyIsRefuse()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        WriteFile("real/target.txt", "hello\n");
        File.CreateSymbolicLink(
            Path.Combine(workspaceRoot, "alias.txt"),
            Path.Combine(workspaceRoot, "real", "target.txt"));

        var validator = NewValidator();
        var patch = """
            *** Begin Patch
            *** Update File: alias.txt
             hello
            *** End Patch
            """;

        var admission = validator.Validate(NewRequest(patch));

        var rejected = admission.Should().BeOfType<Rejected<AdmittedWorkspacePatch>>().Subject;
        rejected.Reason.Code.Should().Be("symlink-refused");
        rejected.Reason.Path.Should().Be("alias.txt");
    }

    [Fact]
    public void Validate_ReMint_SecondCallWithSameInputProducesEquivalentAdmittedValue()
    {
        // Re-mint discipline: persist the source request, replay it through the validator on a
        // fresh process, get back an equivalent admitted value (modulo wall-clock fields).
        // Simulated here with a fixed clock so the AdmittedAt value matches across calls.
        var fixedNow = DateTimeOffset.Parse("2026-04-30T12:00:00Z");
        var validator = NewValidator(now: () => fixedNow);
        var patch = """
            *** Begin Patch
            *** Add File: src/foo.txt
            +bar
            *** End Patch
            """;
        var request = NewRequest(patch);

        var first = validator.Validate(request);
        var second = validator.Validate(request);

        first.Should().BeOfType<Accepted<AdmittedWorkspacePatch>>();
        second.Should().BeOfType<Accepted<AdmittedWorkspacePatch>>();
        var firstAdmitted = ((Accepted<AdmittedWorkspacePatch>)first).Value;
        var secondAdmitted = ((Accepted<AdmittedWorkspacePatch>)second).Value;
        secondAdmitted.SourcePatchText.Should().Be(firstAdmitted.SourcePatchText);
        secondAdmitted.WorkspaceRootPath.Should().Be(firstAdmitted.WorkspaceRootPath);
        secondAdmitted.WorkspaceCorrelationId.Should().Be(firstAdmitted.WorkspaceCorrelationId);
        secondAdmitted.AdmittedAt.Should().Be(firstAdmitted.AdmittedAt);
        secondAdmitted.CommandCount.Should().Be(firstAdmitted.CommandCount);
    }

    [Fact]
    public void Validate_BlankRoot_RejectsWithInvariantCode()
    {
        var validator = NewValidator();
        var admission = validator.Validate(new WorkspacePatchAdmissionRequest(
            WorkspaceCorrelationId: Guid.NewGuid(),
            WorkspaceRootPath: "",
            PatchText: "*** Begin Patch\n*** Add File: x\n+y\n*** End Patch"));

        admission.Should().BeOfType<Rejected<AdmittedWorkspacePatch>>()
            .Which.Reason.Code.Should().Be(RejectionCodes.InvariantViolated);
    }

    private WorkspacePatchValidator NewValidator(
        CodeFlow.Runtime.Workspace.WorkspaceSymlinkPolicy policy = CodeFlow.Runtime.Workspace.WorkspaceSymlinkPolicy.RefuseForMutation,
        Func<DateTimeOffset>? now = null) =>
        new(
            options: new WorkspaceOptions
            {
                Root = workspaceRoot,
                SymlinkPolicy = policy
            },
            nowProvider: now);

    private WorkspacePatchAdmissionRequest NewRequest(string patchText) =>
        new(
            WorkspaceCorrelationId: Guid.NewGuid(),
            WorkspaceRootPath: workspaceRoot,
            PatchText: patchText);

    private void WriteFile(string relative, string content)
    {
        var absolute = Path.Combine(workspaceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }
}
