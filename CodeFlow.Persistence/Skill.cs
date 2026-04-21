namespace CodeFlow.Persistence;

public sealed record Skill(
    long Id,
    string Name,
    string Body,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime UpdatedAtUtc,
    string? UpdatedBy,
    bool IsArchived);

public sealed record SkillCreate(
    string Name,
    string Body,
    string? CreatedBy);

public sealed record SkillUpdate(
    string Name,
    string Body,
    string? UpdatedBy);
