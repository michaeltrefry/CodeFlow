namespace CodeFlow.Persistence;

public sealed class WorkflowNotFoundException(string key, int version)
    : Exception($"No workflow exists for key '{key}' and version {version}.")
{
    public string Key { get; } = key;

    public int Version { get; } = version;
}
