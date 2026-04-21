namespace CodeFlow.Api.Dtos;

public sealed record WorkspaceUsageResponse(
    long RootBytes,
    long CacheBytes,
    int WorktreeCount,
    long WarnThresholdBytes,
    bool AboveWarn);
