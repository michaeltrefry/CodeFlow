namespace CodeFlow.Persistence;

public sealed class McpServerToolEntity
{
    public long Id { get; set; }

    public long ServerId { get; set; }

    public McpServerEntity Server { get; set; } = null!;

    public string ToolName { get; set; } = null!;

    public string? Description { get; set; }

    public string? ParametersJson { get; set; }

    public bool IsMutating { get; set; }

    public DateTime SyncedAtUtc { get; set; }
}
