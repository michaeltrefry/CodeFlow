using CodeFlow.Persistence;

namespace CodeFlow.Api.Dtos;

public sealed record AgentRoleCreateRequest(
    string? Key,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string>? Tags);

public sealed record AgentRoleUpdateRequest(
    string? DisplayName,
    string? Description,
    IReadOnlyList<string>? Tags);

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
    bool IsRetired,
    bool IsSystemManaged,
    IReadOnlyList<string> Tags);

public sealed record AgentRoleGrantRequest(
    AgentRoleToolCategory Category,
    string? ToolIdentifier);

public sealed record AgentRoleGrantResponse(
    AgentRoleToolCategory Category,
    string ToolIdentifier);

public sealed record AgentAssignmentsRequest(IReadOnlyList<long>? RoleIds);

public sealed record BulkRetireRoleIdsRequest(IReadOnlyList<long>? Ids);

public sealed record BulkRetireRoleIdsResponse(
    IReadOnlyList<long> RetiredIds,
    IReadOnlyList<long> MissingIds);
