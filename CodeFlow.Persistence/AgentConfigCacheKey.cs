namespace CodeFlow.Persistence;

internal readonly record struct AgentConfigCacheKey(string Key, int Version)
{
    public static AgentConfigCacheKey Create(string key, int version)
    {
        return new AgentConfigCacheKey(key.Trim().ToUpperInvariant(), version);
    }
}
