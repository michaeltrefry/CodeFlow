namespace CodeFlow.Persistence;

public sealed class AgentConfigNotFoundException(string key, int version)
    : Exception($"No agent config exists for key '{key}' and version {version}.")
{
    public string Key { get; } = key;

    public int Version { get; } = version;
}
