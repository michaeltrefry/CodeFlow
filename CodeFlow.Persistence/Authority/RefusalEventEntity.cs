namespace CodeFlow.Persistence.Authority;

/// <summary>
/// Append-only persistence row for a <see cref="CodeFlow.Runtime.Authority.RefusalEvent"/>.
/// Producers (workspace tools, envelope resolver, gates, preflight) write through
/// <see cref="EfRefusalEventSink"/>; queries go through <see cref="RefusalEventRepository"/>.
/// </summary>
public sealed class RefusalEventEntity
{
    public Guid Id { get; set; }

    public Guid? TraceId { get; set; }

    public Guid? AssistantConversationId { get; set; }

    public string Stage { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string? Axis { get; set; }

    public string? Path { get; set; }

    public string? DetailJson { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
