using CodeFlow.Persistence;

namespace CodeFlow.Api.Assistant.Artifacts;

/// <summary>
/// Per-turn collector the <see cref="ArtifactRecorder"/> appends to after a successful
/// repository write. The assistant's tool-dispatch loop drains this collector after each
/// tool call so artifact events are emitted as stream items in close lock-step with the
/// tool that produced them. Scoped — one instance per HTTP request, shared across the
/// recorder and the assistant via DI scope.
/// </summary>
/// <remarks>
/// AA-2 only: AA-1's persistence path already guarantees durability — the collector is the
/// surface that turns persisted events into a live UI signal. Reload-survival comes in AA-3
/// via conversation-load hydration; this collector is purely the in-turn live channel.
/// </remarks>
public interface IArtifactEventCollector
{
    /// <summary>
    /// Append a successfully-recorded artifact event. <paramref name="supersedesPriorByName"/>
    /// rides along so the chat panel can mark prior pills with the same name as superseded
    /// without a separate round-trip.
    /// </summary>
    void Append(AssistantArtifactEvent evt, bool supersedesPriorByName);

    /// <summary>
    /// Returns and clears all events appended since the last drain. Called by the assistant
    /// loop after every tool dispatch; ordering matches insertion order.
    /// </summary>
    IReadOnlyList<CollectedArtifactEvent> Drain();
}

/// <summary>Pairs the persisted event with the supersession flag for stream emission.</summary>
public sealed record CollectedArtifactEvent(AssistantArtifactEvent Event, bool SupersedesPriorByName);
