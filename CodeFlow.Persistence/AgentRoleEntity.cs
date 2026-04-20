namespace CodeFlow.Persistence;

public sealed class AgentRoleEntity
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    public bool IsArchived { get; set; }
}
