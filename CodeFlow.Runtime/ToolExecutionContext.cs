namespace CodeFlow.Runtime;

public sealed record ToolExecutionContext(
    ToolWorkspaceContext? Workspace = null);

public sealed record ToolWorkspaceContext(
    Guid CorrelationId,
    string RootPath,
    string? RepoUrl = null,
    string? RepoIdentityKey = null,
    string? RepoSlug = null);
