namespace CodeFlow.Persistence;

public interface IAssistantSettingsRepository
{
    Task<AssistantSettings?> GetAsync(CancellationToken cancellationToken = default);

    Task<AssistantSettings> SetAsync(AssistantSettingsWrite write, CancellationToken cancellationToken = default);
}

public sealed record AssistantSettings(
    string? Provider,
    string? Model,
    long? MaxTokensPerConversation,
    long? AssignedAgentRoleId,
    string? Instructions,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record AssistantSettingsWrite(
    string? Provider,
    string? Model,
    long? MaxTokensPerConversation,
    long? AssignedAgentRoleId,
    string? Instructions,
    string? UpdatedBy);
