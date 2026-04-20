namespace CodeFlow.Persistence;

public sealed class AgentConfigEntity
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public int Version { get; set; }

    public string ConfigJson { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public bool IsActive { get; set; }
}
