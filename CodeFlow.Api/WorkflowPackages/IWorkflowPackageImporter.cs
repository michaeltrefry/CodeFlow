namespace CodeFlow.Api.WorkflowPackages;

public interface IWorkflowPackageImporter
{
    Task<WorkflowPackageImportPreview> PreviewAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default);

    Task<WorkflowPackageImportApplyResult> ApplyAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowPackageImportPreview(
    WorkflowPackageReference EntryPoint,
    IReadOnlyList<WorkflowPackageImportItem> Items,
    IReadOnlyList<string> Warnings)
{
    public int CreateCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Create);

    public int ReuseCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Reuse);

    public int ConflictCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Conflict);

    public int WarningCount => Warnings.Count;

    public bool CanApply => ConflictCount == 0;
}

public sealed record WorkflowPackageImportItem(
    WorkflowPackageImportResourceKind Kind,
    string Key,
    int? Version,
    WorkflowPackageImportAction Action,
    string Message);

public sealed record WorkflowPackageImportApplyResult(
    WorkflowPackageReference EntryPoint,
    IReadOnlyList<WorkflowPackageImportItem> Items,
    IReadOnlyList<string> Warnings)
{
    public int CreateCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Create);

    public int ReuseCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Reuse);

    public int ConflictCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Conflict);

    public int WarningCount => Warnings.Count;
}

public enum WorkflowPackageImportResourceKind
{
    Workflow,
    Agent,
    AgentRoleAssignment,
    Role,
    Skill,
    McpServer,
}

public enum WorkflowPackageImportAction
{
    Create,
    Reuse,
    Conflict,
}
