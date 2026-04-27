using CodeFlow.Persistence;

namespace CodeFlow.Api.Dtos;

public sealed record AgentRoleCreateRequest(
    string? Key,
    string? DisplayName,
    string? Description);

public sealed record AgentRoleUpdateRequest(
    string? DisplayName,
    string? Description);

public sealed record AgentRoleResponse(
    long Id,
    string Key,
    string DisplayName,
    string? Description,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime UpdatedAtUtc,
    string? UpdatedBy,
    bool IsArchived,
    bool IsSystemManaged = false);

public sealed record AgentRoleGrantRequest(
    AgentRoleToolCategory Category,
    string? ToolIdentifier);

public sealed record AgentRoleGrantResponse(
    AgentRoleToolCategory Category,
    string ToolIdentifier);

public sealed record AgentAssignmentsRequest(IReadOnlyList<long>? RoleIds);
