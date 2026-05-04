namespace CodeFlow.Persistence;

public sealed class AgentConfigEntity
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public int Version { get; set; }

    public string ConfigJson { get; set; } = null!;

    public string TagsJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public bool IsActive { get; set; }

    public bool IsRetired { get; set; }

    public string? OwningWorkflowKey { get; set; }

    public string? ForkedFromKey { get; set; }

    public int? ForkedFromVersion { get; set; }
}
