namespace CodeFlow.Persistence;

public sealed record WorkflowInput(
    string Key,
    string DisplayName,
    WorkflowInputKind Kind,
    bool Required,
    string? DefaultValueJson,
    string? Description,
    int Ordinal);
