using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Assistant.Idempotency;

/// <summary>
/// sc-803 — Per-process map of active assistant turn publishers (i.e. multicast recorders
/// that are still recording in-flight). The dispatcher consults this registry when an
/// idempotent retry hits an <c>InFlight</c> record so a same-instance retry can attach to
/// the live frame stream instead of waiting on the terminal-state poller. Cross-instance
/// retries find no entry here and fall back to <c>WaitThenReplay</c> until phase 3.
/// </summary>
public sealed class AssistantTurnSubscriptionRegistry
{
    private readonly ConcurrentDictionary<Guid, IAssistantTurnPublisher> publishers = new();
    private readonly IOptions<AssistantTurnIdempotencyOptions> options;

    public AssistantTurnSubscriptionRegistry(IOptions<AssistantTurnIdempotencyOptions> options)
    {
        this.options = options;
    }

    /// <summary>
    /// Producer-side: announce an active recorder. Called by the recorder constructor so a
    /// retry can find it any time after the originating turn has been claimed.
    /// </summary>
    internal void Register(Guid recordId, IAssistantTurnPublisher publisher)
    {
        publishers[recordId] = publisher;
    }

    /// <summary>
    /// Producer-side: drop the active recorder. Called by the recorder during
    /// <c>FlushAsync</c>. Subscribers attached *before* unregister are kept alive by the
    /// recorder's own sink list and complete through the channel; subscribers attaching
    /// *after* unregister get <c>null</c> and the dispatcher falls back to terminal replay.
    /// </summary>
    internal void Unregister(Guid recordId)
    {
        publishers.TryRemove(recordId, out _);
    }

    /// <summary>
    /// Consumer-side: attach to the live producer for <paramref name="recordId"/>. Returns
    /// null if no producer is active locally — caller should fall back to terminal
    /// <c>WaitThenReplay</c>. The returned subscription owns its sink lifetime and must be
    /// disposed (typically <c>await using</c>).
    /// </summary>
    public IAssistantTurnSubscription? TrySubscribe(Guid recordId)
    {
        if (!publishers.TryGetValue(recordId, out var publisher))
        {
            return null;
        }

        var opts = options.Value;
        var lifetime = opts.LiveTailSubscriberLifetime > TimeSpan.Zero
            ? opts.LiveTailSubscriberLifetime
            : opts.WaitTimeout;
        return publisher.Subscribe(opts.LiveTailSubscriberCapacity, lifetime);
    }
}
