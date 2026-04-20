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
    bool IsArchived);

public sealed record AgentRoleToolGrant(
    AgentRoleToolCategory Category,
    string ToolIdentifier);

public sealed record AgentRoleCreate(
    string Key,
    string DisplayName,
    string? Description,
    string? CreatedBy);

public sealed record AgentRoleUpdate(
    string DisplayName,
    string? Description,
    string? UpdatedBy);
