using System.Text;
using CodeFlow.Persistence;

namespace CodeFlow.Orchestration.Tests.Replay;

internal sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly Dictionary<Uri, byte[]> store = new();

    public void Seed(string uri, string content)
    {
        store[new Uri(uri)] = Encoding.UTF8.GetBytes(content);
    }

    public Task<Uri> WriteAsync(Stream content, ArtifactMetadata metadata, CancellationToken cancellationToken = default)
    {
        var uri = new Uri($"memory://{Guid.NewGuid():N}");
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        store[uri] = ms.ToArray();
        return Task.FromResult(uri);
    }

    public Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!store.TryGetValue(uri, out var bytes))
        {
            throw new FileNotFoundException($"Artifact not found: {uri}");
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task<ArtifactMetadata> GetMetadataAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
