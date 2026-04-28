namespace CodeFlow.Persistence;

public sealed class TokenUsageRecordEntity
{
    public Guid Id { get; set; }

    public Guid TraceId { get; set; }

    public Guid NodeId { get; set; }

    public Guid InvocationId { get; set; }

    /// <summary>
    /// Ordered scope chain from the root workflow down to the originating node, serialized as a
    /// JSON array of Guid strings. Empty array for top-level (non-nested) sagas. Used to roll up
    /// usage for nested subflows / ReviewLoops / Swarms without a separate join table.
    /// </summary>
    public string ScopeChainJson { get; set; } = "[]";

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public DateTime RecordedAtUtc { get; set; }

    /// <summary>
    /// Verbatim provider-reported usage fields (e.g., input_tokens, output_tokens,
    /// cache_creation_input_tokens, cache_read_input_tokens, reasoning_tokens). Schema-less so
    /// new provider fields land without a migration. Capture code MUST NOT drop unknown fields.
    /// </summary>
    public string UsageJson { get; set; } = "{}";
}
