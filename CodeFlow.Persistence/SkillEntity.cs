namespace CodeFlow.Persistence;

public sealed class SkillEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = null!;

    public string Body { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    public bool IsArchived { get; set; }
}
