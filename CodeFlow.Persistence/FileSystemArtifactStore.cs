using System.Security.Cryptography;
using System.Text.Json;

namespace CodeFlow.Persistence;

public sealed class FileSystemArtifactStore : IArtifactStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string rootDirectory;
    private readonly string blobDirectory;
    private readonly string temporaryDirectory;
    private readonly long? maxArtifactBytes;

    public FileSystemArtifactStore(FileSystemArtifactStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.RootDirectory))
        {
            throw new ArgumentException("A root directory is required.", nameof(options));
        }

        if (options.MaxArtifactBytes is { } configuredMax && configuredMax <= 0)
        {
            throw new ArgumentException(
                "MaxArtifactBytes must be positive when set.",
                nameof(options));
        }

        rootDirectory = Path.GetFullPath(options.RootDirectory);
        blobDirectory = Path.Combine(rootDirectory, ".blobs");
        temporaryDirectory = Path.Combine(rootDirectory, ".tmp");
        maxArtifactBytes = options.MaxArtifactBytes;
    }

    public async Task<Uri> WriteAsync(
        Stream content,
        ArtifactMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        EnsureValidMetadata(metadata);
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(blobDirectory);
        Directory.CreateDirectory(temporaryDirectory);

        var artifactPath = GetArtifactPath(metadata);
        var artifactDirectory = Path.GetDirectoryName(artifactPath)!;
        var sidecarPath = GetSidecarPath(artifactPath);

        Directory.CreateDirectory(artifactDirectory);

        var temporaryContentPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.tmp");

        try
        {
            var (contentHash, contentLength) = await CopyToTemporaryFileAndHashAsync(
                content,
                temporaryContentPath,
                maxArtifactBytes,
                cancellationToken);

            var blobPath = Path.Combine(blobDirectory, $"{contentHash}.bin");

            if (!File.Exists(blobPath))
            {
                File.Move(temporaryContentPath, blobPath);
            }
            else
            {
                File.Delete(temporaryContentPath);
            }

            if (File.Exists(sidecarPath))
            {
                var existingMetadata = await GetMetadataAsync(
                    new Uri(artifactPath),
                    cancellationToken);

                if (!string.Equals(existingMetadata.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Artifact '{artifactPath}' already exists with different content.");
                }

                return new Uri(artifactPath);
            }

            var persistedMetadata = metadata with
            {
                ContentHash = contentHash,
                ContentLength = contentLength,
                CreatedAtUtc = metadata.CreatedAtUtc ?? DateTime.UtcNow
            };

            await WriteSidecarAsync(
                sidecarPath,
                persistedMetadata,
                Path.GetRelativePath(artifactDirectory, blobPath),
                cancellationToken);

            TryCreateSymbolicLink(artifactPath, blobPath);

            return new Uri(artifactPath);
        }
        finally
        {
            if (File.Exists(temporaryContentPath))
            {
                File.Delete(temporaryContentPath);
            }
        }
    }

    public async Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        var (artifactPath, sidecar) = await ResolveSidecarAsync(uri, cancellationToken);
        var artifactDirectory = Path.GetDirectoryName(artifactPath)!;
        var blobPath = Path.GetFullPath(sidecar.BlobRelativePath, artifactDirectory);

        EnsureBlobWithinBlobRoot(blobPath, uri);

        if (!File.Exists(blobPath))
        {
            throw new FileNotFoundException($"Artifact blob was not found for '{uri}'.", blobPath);
        }

        // An operator can tighten the cap after data has been written; reject stale oversized
        // blobs before handing back a stream so a downstream materialize can't OOM.
        if (maxArtifactBytes is { } limit && sidecar.ContentLength > limit)
        {
            throw new ArtifactTooLargeException(sidecar.ContentLength, limit);
        }

        return new FileStream(
            blobPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async Task<ArtifactMetadata> GetMetadataAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        var (artifactPath, sidecar) = await ResolveSidecarAsync(uri, cancellationToken);

        // Defence-in-depth: a sidecar whose BlobRelativePath escapes the .blobs directory could
        // point anywhere on disk. Write paths never produce such sidecars today, but any future
        // write change, test seed, or manual restore could — reject before we hand out metadata
        // that references an out-of-root path.
        var artifactDirectory = Path.GetDirectoryName(artifactPath)!;
        var blobPath = Path.GetFullPath(sidecar.BlobRelativePath, artifactDirectory);
        EnsureBlobWithinBlobRoot(blobPath, uri);

        return new ArtifactMetadata(
            sidecar.TraceId,
            sidecar.RoundId,
            sidecar.ArtifactId,
            sidecar.ContentType,
            sidecar.FileName,
            sidecar.ContentHash,
            sidecar.ContentLength,
            DateTime.SpecifyKind(sidecar.CreatedAtUtc, DateTimeKind.Utc));
    }

    private void EnsureBlobWithinBlobRoot(string resolvedBlobPath, Uri uri)
    {
        var normalizedBlobRoot = blobDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(resolvedBlobPath);

        if (!normalizedPath.StartsWith(normalizedBlobRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Artifact sidecar for '{uri}' points outside the configured blob directory.");
        }
    }

    private static void EnsureValidMetadata(ArtifactMetadata metadata)
    {
        if (metadata.TraceId == Guid.Empty)
        {
            throw new ArgumentException("Artifact metadata must include a non-empty trace ID.", nameof(metadata));
        }

        if (metadata.RoundId == Guid.Empty)
        {
            throw new ArgumentException("Artifact metadata must include a non-empty round ID.", nameof(metadata));
        }

        if (metadata.ArtifactId == Guid.Empty)
        {
            throw new ArgumentException("Artifact metadata must include a non-empty artifact ID.", nameof(metadata));
        }
    }

    private string GetArtifactPath(ArtifactMetadata metadata)
    {
        return Path.Combine(
            rootDirectory,
            metadata.TraceId.ToString("N"),
            metadata.RoundId.ToString("N"),
            $"{metadata.ArtifactId:N}.bin");
    }

    private static string GetSidecarPath(string artifactPath) => $"{artifactPath}.json";

    private static async Task<(string ContentHash, long ContentLength)> CopyToTemporaryFileAndHashAsync(
        Stream content,
        string temporaryContentPath,
        long? maxBytes,
        CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var temporaryStream = new FileStream(
            temporaryContentPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[81920];
        long totalBytes = 0;

        while (true)
        {
            var bytesRead = await content.ReadAsync(buffer, cancellationToken);

            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;

            // Fail fast before writing to disk so an oversized producer can't exhaust the temp
            // directory. The outer `finally` deletes the partial temp file on throw.
            if (maxBytes is { } limit && totalBytes > limit)
            {
                throw new ArtifactTooLargeException(totalBytes, limit);
            }

            hasher.AppendData(buffer, 0, bytesRead);
            await temporaryStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        await temporaryStream.FlushAsync(cancellationToken);

        return (Convert.ToHexStringLower(hasher.GetHashAndReset()), totalBytes);
    }

    private async Task WriteSidecarAsync(
        string sidecarPath,
        ArtifactMetadata metadata,
        string blobRelativePath,
        CancellationToken cancellationToken)
    {
        var sidecar = new ArtifactSidecar(
            metadata.TraceId,
            metadata.RoundId,
            metadata.ArtifactId,
            metadata.ContentType,
            metadata.FileName,
            metadata.ContentHash!,
            metadata.ContentLength,
            DateTime.SpecifyKind((metadata.CreatedAtUtc ?? DateTime.UtcNow), DateTimeKind.Utc),
            blobRelativePath);

        await using var sidecarStream = new FileStream(
            sidecarPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        await JsonSerializer.SerializeAsync(
            sidecarStream,
            sidecar,
            SerializerOptions,
            cancellationToken);
    }

    private async Task<(string ArtifactPath, ArtifactSidecar Sidecar)> ResolveSidecarAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var normalizedUri = NormalizeArtifactUri(uri);

        if (!normalizedUri.IsAbsoluteUri || !normalizedUri.IsFile)
        {
            throw new ArgumentException("Artifact URIs must be absolute file URIs.", nameof(uri));
        }

        var artifactPath = Path.GetFullPath(normalizedUri.LocalPath);

        if (!IsWithinRoot(artifactPath))
        {
            throw new ArgumentException(
                $"Artifact URI '{uri}' is outside the configured root '{rootDirectory}'.",
                nameof(uri));
        }

        var sidecarPath = GetSidecarPath(artifactPath);

        if (!File.Exists(sidecarPath))
        {
            throw new FileNotFoundException($"Artifact metadata was not found for '{uri}'.", sidecarPath);
        }

        await using var sidecarStream = new FileStream(
            sidecarPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var sidecar = await JsonSerializer.DeserializeAsync<ArtifactSidecar>(
            sidecarStream,
            SerializerOptions,
            cancellationToken);

        if (sidecar is null)
        {
            throw new InvalidOperationException($"Artifact metadata sidecar '{sidecarPath}' was empty.");
        }

        return (artifactPath, sidecar);
    }

    private static Uri NormalizeArtifactUri(Uri uri)
    {
        if (uri.IsAbsoluteUri)
        {
            return uri;
        }

        var originalValue = Uri.UnescapeDataString(uri.OriginalString);

        if (Path.IsPathRooted(originalValue))
        {
            return new Uri(Path.GetFullPath(originalValue));
        }

        return uri;
    }

    private static void TryCreateSymbolicLink(string artifactPath, string blobPath)
    {
        if (File.Exists(artifactPath))
        {
            return;
        }

        try
        {
            var artifactDirectory = Path.GetDirectoryName(artifactPath)!;
            var relativeBlobPath = Path.GetRelativePath(artifactDirectory, blobPath);
            File.CreateSymbolicLink(artifactPath, relativeBlobPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The store resolves reads via sidecars, so a best-effort symlink is helpful but optional.
        }
    }

    private bool IsWithinRoot(string path)
    {
        var normalizedRoot = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.Ordinal)
            || string.Equals(
                normalizedPath,
                rootDirectory,
                StringComparison.Ordinal);
    }

    private sealed record ArtifactSidecar(
        Guid TraceId,
        Guid RoundId,
        Guid ArtifactId,
        string? ContentType,
        string? FileName,
        string ContentHash,
        long ContentLength,
        DateTime CreatedAtUtc,
        string BlobRelativePath);
}
