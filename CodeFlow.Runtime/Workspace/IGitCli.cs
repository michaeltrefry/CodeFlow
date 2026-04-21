namespace CodeFlow.Runtime.Workspace;

public interface IGitCli
{
    Task CloneMirrorAsync(string originUrl, string destinationMirrorPath, CancellationToken cancellationToken = default);

    Task FetchAsync(string mirrorPath, CancellationToken cancellationToken = default);

    Task WorktreeAddAsync(
        string mirrorPath,
        string worktreePath,
        string branchName,
        string? startPoint = null,
        CancellationToken cancellationToken = default);

    Task WorktreeRemoveAsync(
        string mirrorPath,
        string worktreePath,
        bool force = false,
        CancellationToken cancellationToken = default);

    Task CreateBranchAsync(
        string worktreePath,
        string branchName,
        string? startPoint = null,
        CancellationToken cancellationToken = default);

    Task CheckoutAsync(string worktreePath, string branchOrRef, CancellationToken cancellationToken = default);

    Task AddAsync(
        string worktreePath,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default);

    Task<bool> CommitAsync(string worktreePath, string message, CancellationToken cancellationToken = default);

    Task PushAsync(
        string worktreePath,
        string? remote = null,
        string? branch = null,
        CancellationToken cancellationToken = default);

    Task<string> RevParseAsync(string worktreePath, string rev, CancellationToken cancellationToken = default);

    Task<string> GetSymbolicHeadAsync(string gitDirectory, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> LsFilesAsync(string worktreePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitStatusEntry>> StatusAsync(string worktreePath, CancellationToken cancellationToken = default);
}

public sealed record GitStatusEntry(string StatusCode, string Path);
