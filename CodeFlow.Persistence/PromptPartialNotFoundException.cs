namespace CodeFlow.Persistence;

public sealed class PromptPartialNotFoundException(string key, int version)
    : Exception($"Prompt partial '{key}' v{version} does not exist.")
{
    public string Key { get; } = key;
    public int Version { get; } = version;
}
