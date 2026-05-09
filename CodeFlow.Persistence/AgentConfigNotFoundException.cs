namespace CodeFlow.Persistence;

public sealed class AgentConfigNotFoundException : Exception
{
    public AgentConfigNotFoundException(string key, int version)
        : base($"No agent config exists for key '{key}' and version {version}.")
    {
        Key = key;
        Version = version;
    }

    public AgentConfigNotFoundException(string key)
        : base($"No agent config exists for key '{key}'.")
    {
        Key = key;
        Version = null;
    }

    public string Key { get; }

    public int? Version { get; }
}

/// <summary>
/// sc-828 / AR-4: thrown when a bump-on-write request previewed against agent v{expected}
/// but the latest version moved on to v{actual} between preview and apply. The admin UI
/// surfaces a 409 + refresh affordance; the user can re-confirm by passing
/// <c>acknowledgeDrift: true</c> on the next request.
/// </summary>
public sealed class AgentConfigVersionDriftException(
    string key,
    int expectedVersion,
    int actualVersion)
    : Exception($"Agent '{key}' has moved from v{expectedVersion} to v{actualVersion} since this edit started.")
{
    public string Key { get; } = key;

    public int ExpectedVersion { get; } = expectedVersion;

    public int ActualVersion { get; } = actualVersion;
}
