namespace CodeFlow.Persistence;

internal readonly record struct WorkflowCacheKey(string Key, int Version)
{
    public static WorkflowCacheKey Create(string key, int version)
    {
        return new WorkflowCacheKey(key.Trim().ToUpperInvariant(), version);
    }
}
