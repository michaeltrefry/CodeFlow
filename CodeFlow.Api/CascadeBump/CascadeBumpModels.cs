namespace CodeFlow.Api.CascadeBump;

/// <summary>
/// E4: surface area for the cascade-bump assistant. Given a "bump root" (an agent or workflow
/// that just got a new version), the planner walks the reverse-pin graph and produces a plan
/// that bumps every workflow whose latest version pins the previous version of the root or any
/// transitively-bumped workflow. The executor then materializes the plan as new workflow
/// versions.
/// </summary>
public enum CascadeBumpRootKind
{
    Agent,
    Workflow,
}

public enum CascadeBumpReferenceKind
{
    Agent,
    Subflow,
}

public sealed record CascadeBumpRoot(
    CascadeBumpRootKind Kind,
    string Key,
    int FromVersion,
    int ToVersion);

public sealed record CascadeBumpPinChange(
    Guid NodeId,
    CascadeBumpReferenceKind ReferenceKind,
    string Key,
    int FromVersion,
    int ToVersion);

/// <summary>
/// One workflow that needs a new version as part of the cascade. <see cref="FromVersion"/> is
/// the workflow's current latest at planning time; <see cref="ToVersion"/> is the predicted
/// next version (current+1). Apply may end up assigning a different version if a concurrent
/// edit lands between plan and apply — the actual created version is returned in the apply
/// response.
/// </summary>
public sealed record CascadeBumpStep(
    string WorkflowKey,
    int FromVersion,
    int ToVersion,
    IReadOnlyList<CascadeBumpPinChange> PinChanges);

public sealed record CascadeBumpFinding(
    string Severity,
    string Code,
    string Message);

public sealed record CascadeBumpPlan(
    CascadeBumpRoot Root,
    IReadOnlyList<CascadeBumpStep> Steps,
    IReadOnlyList<CascadeBumpFinding> Findings);

public sealed record CascadeBumpAppliedWorkflow(
    string WorkflowKey,
    int FromVersion,
    int CreatedVersion,
    IReadOnlyList<CascadeBumpPinChange> PinChanges);

public sealed record CascadeBumpApplyResult(
    CascadeBumpRoot Root,
    IReadOnlyList<CascadeBumpAppliedWorkflow> AppliedWorkflows,
    IReadOnlyList<CascadeBumpFinding> Findings);

public sealed record CascadeBumpRequest(
    CascadeBumpRootKind RootKind,
    string Key,
    int FromVersion,
    int ToVersion,
    IReadOnlyList<string>? ExcludeWorkflows = null);

public sealed class CascadeBumpRootNotFoundException : Exception
{
    public CascadeBumpRootNotFoundException(string message) : base(message) { }
}
