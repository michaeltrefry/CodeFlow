using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class AssistantSettingsRepository(CodeFlowDbContext dbContext) : IAssistantSettingsRepository
{
    public async Task<AssistantSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AssistantSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Key == AssistantSettingsEntity.SingletonKey, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<AssistantSettings> SetAsync(AssistantSettingsWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        var entity = await dbContext.AssistantSettings
            .SingleOrDefaultAsync(e => e.Key == AssistantSettingsEntity.SingletonKey, cancellationToken);

        var now = DateTime.UtcNow;
        var provider = NormalizeProvider(write.Provider);
        var model = NormalizeString(write.Model);
        var cap = write.MaxTokensPerConversation is { } v && v > 0 ? v : (long?)null;
        var roleId = write.AssignedAgentRoleId is { } r && r > 0 ? r : (long?)null;

        if (entity is null)
        {
            entity = new AssistantSettingsEntity
            {
                Key = AssistantSettingsEntity.SingletonKey,
                Provider = provider,
                Model = model,
                MaxTokensPerConversation = cap,
                AssignedAgentRoleId = roleId,
                UpdatedBy = NormalizeString(write.UpdatedBy),
                UpdatedAtUtc = now,
            };
            dbContext.AssistantSettings.Add(entity);
        }
        else
        {
            entity.Provider = provider;
            entity.Model = model;
            entity.MaxTokensPerConversation = cap;
            entity.AssignedAgentRoleId = roleId;
            entity.UpdatedBy = NormalizeString(write.UpdatedBy);
            entity.UpdatedAtUtc = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    private static AssistantSettings Map(AssistantSettingsEntity entity) => new(
        Provider: entity.Provider,
        Model: entity.Model,
        MaxTokensPerConversation: entity.MaxTokensPerConversation,
        AssignedAgentRoleId: entity.AssignedAgentRoleId,
        UpdatedBy: entity.UpdatedBy,
        UpdatedAtUtc: entity.UpdatedAtUtc == default
            ? null
            : DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc));

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeProvider(string? value)
    {
        var trimmed = NormalizeString(value);
        if (trimmed is null) return null;
        return LlmProviderKeys.IsKnown(trimmed) ? LlmProviderKeys.Canonicalize(trimmed) : trimmed;
    }
}
