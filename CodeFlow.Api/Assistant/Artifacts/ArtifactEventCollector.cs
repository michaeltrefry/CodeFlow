using CodeFlow.Persistence;

namespace CodeFlow.Api.Assistant.Artifacts;

/// <summary>
/// Default implementation. Backed by a plain <see cref="List{T}"/> because the recorder and
/// the assistant tool loop run on the same request thread (the recorder is invoked synchronously
/// from inside <c>dispatcher.InvokeAsync(...)</c>, and the assistant drains right after that
/// returns). No cross-thread access; no concurrent collection needed.
/// </summary>
public sealed class ArtifactEventCollector : IArtifactEventCollector
{
    private readonly List<CollectedArtifactEvent> buffer = new();

    public void Append(AssistantArtifactEvent evt, bool supersedesPriorByName)
    {
        ArgumentNullException.ThrowIfNull(evt);
        buffer.Add(new CollectedArtifactEvent(evt, supersedesPriorByName));
    }

    public IReadOnlyList<CollectedArtifactEvent> Drain()
    {
        if (buffer.Count == 0)
        {
            return Array.Empty<CollectedArtifactEvent>();
        }

        var copy = buffer.ToArray();
        buffer.Clear();
        return copy;
    }
}
