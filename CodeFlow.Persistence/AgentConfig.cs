using CodeFlow.Runtime;

namespace CodeFlow.Persistence;

public sealed record AgentConfig(
    string Key,
    int Version,
    AgentKind Kind,
    AgentInvocationConfiguration Configuration,
    string ConfigJson,
    DateTime CreatedAtUtc,
    string? CreatedBy);
