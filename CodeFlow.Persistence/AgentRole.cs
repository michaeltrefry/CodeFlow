namespace CodeFlow.Persistence;

public sealed record AgentRole(
    long Id,
    string Key,
    string DisplayName,
    string? Description,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime UpdatedAtUtc,
    string? UpdatedBy,
    bool IsArchived,
    bool IsRetired = false,
    bool IsSystemManaged = false,
    IReadOnlyList<string>? Tags = null)
{
    public IReadOnlyList<string> TagsOrEmpty => Tags ?? Array.Empty<string>();
}

public sealed record AgentRoleToolGrant(
    AgentRoleToolCategory Category,
    string ToolIdentifier);

public sealed record AgentRoleCreate(
    string Key,
    string DisplayName,
    string? Description,
    string? CreatedBy,
    IReadOnlyList<string>? Tags = null);

public sealed record AgentRoleUpdate(
    string DisplayName,
    string? Description,
    string? UpdatedBy,
    IReadOnlyList<string>? Tags = null);
