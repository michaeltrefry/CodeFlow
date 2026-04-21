namespace CodeFlow.Api.Dtos;

public sealed record SkillCreateRequest(
    string? Name,
    string? Body);

public sealed record SkillUpdateRequest(
    string? Name,
    string? Body);

public sealed record SkillResponse(
    long Id,
    string Name,
    string Body,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime UpdatedAtUtc,
    string? UpdatedBy,
    bool IsArchived);

public sealed record AgentRoleSkillGrantsRequest(IReadOnlyList<long>? SkillIds);

public sealed record AgentRoleSkillGrantsResponse(IReadOnlyList<long> SkillIds);
