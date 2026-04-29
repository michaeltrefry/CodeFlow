namespace CodeFlow.Runtime;

public sealed record ToolExecutionContext(
    ToolWorkspaceContext? Workspace = null,
    IReadOnlyList<ToolRepositoryContext>? Repositories = null);

public sealed record ToolWorkspaceContext(
    Guid CorrelationId,
    string RootPath,
    string? RepoUrl = null,
    string? RepoIdentityKey = null,
    string? RepoSlug = null);

public sealed record ToolRepositoryContext(
    string Owner,
    string Name,
    string? Url = null,
    string? RepoIdentityKey = null,
    string? RepoSlug = null);
