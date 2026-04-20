namespace CodeFlow.Persistence;

public interface IArtifactStore
{
    Task<Uri> WriteAsync(
        Stream content,
        ArtifactMetadata metadata,
        CancellationToken cancellationToken = default);

    Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default);

    Task<ArtifactMetadata> GetMetadataAsync(
        Uri uri,
        CancellationToken cancellationToken = default);
}
