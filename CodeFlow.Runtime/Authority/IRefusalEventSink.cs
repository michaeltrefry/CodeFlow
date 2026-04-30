namespace CodeFlow.Runtime.Authority;

/// <summary>
/// Append-only sink for <see cref="RefusalEvent"/> records. Producers (tools, envelope
/// resolver, gates, preflight) call <see cref="RecordAsync"/> on every denial path so the
/// stream of refused work is durable and queryable.
///
/// Implementations MUST be append-only — refusals are evidence, not state. Updates and
/// deletes are reserved for retention sweeps and are not part of this interface.
///
/// Implementations MUST NOT throw on transient failures unless the caller asked for
/// strict mode; refusal recording should never break the calling tool's primary failure
/// flow. Callers already have the structured payload in their hand; if the sink is down,
/// the payload is still surfaced to the LLM and operator.
/// </summary>
public interface IRefusalEventSink
{
    Task RecordAsync(RefusalEvent refusal, CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op sink used when refusal persistence is not configured (tests, minimal hosts). Lets
/// producers always have a sink to call without null checks.
/// </summary>
public sealed class NullRefusalEventSink : IRefusalEventSink
{
    public static readonly NullRefusalEventSink Instance = new();

    public Task RecordAsync(RefusalEvent refusal, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
