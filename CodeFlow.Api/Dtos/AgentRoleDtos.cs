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

/// <summary>
/// sc-828 / AR-4: PUT /api/agents/{key}/roles request body. The default flow is
/// bump-on-write — the endpoint clones the latest agent body to a new version with the
/// supplied <see cref="RoleIds"/> assignment slot. <see cref="ExpectedFromVersion"/> (when
/// non-null) drives the drift gate: 409 if the current latest doesn't match, retried with
/// <see cref="AcknowledgeDrift"/> = <c>true</c> to override.
/// </summary>
public sealed record AgentAssignmentsRequest(
    IReadOnlyList<long>? RoleIds,
    int? ExpectedFromVersion = null,
    bool AcknowledgeDrift = false);

/// <summary>
/// Response for PUT /api/agents/{key}/roles after AR-4. Surfaces the new agent version the
/// admin UI should redirect to (and which workflows can rebind by republishing).
/// </summary>
public sealed record AgentAssignmentsResponse(
    string AgentKey,
    int AgentVersion,
    IReadOnlyList<AgentRoleResponse> AssignedRoles);

/// <summary>
/// 409 body returned when bump-on-write detects drift between the version the caller
/// previewed against and the current latest. Mirrors the in-place agent edit's
/// publish-back drift envelope.
/// </summary>
public sealed record AgentAssignmentsDriftResponse(
    string AgentKey,
    int ExpectedFromVersion,
    int ActualLatestVersion,
    string Message);

public sealed record BulkRetireRoleIdsRequest(IReadOnlyList<long>? Ids);

public sealed record BulkRetireRoleIdsResponse(
    IReadOnlyList<long> RetiredIds,
    IReadOnlyList<long> MissingIds);
