using FluentAssertions;
using System.Text;

namespace CodeFlow.Persistence.Tests;

public sealed class FileSystemArtifactStoreTests : IDisposable
{
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "codeflow-artifact-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteReadAndGetMetadataAsync_ShouldRoundTripArtifactContent()
    {
        var store = CreateStore();
        var metadata = new ArtifactMetadata(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            ArtifactId: Guid.NewGuid(),
            ContentType: "text/plain",
            FileName: "greeting.txt");
        var content = Encoding.UTF8.GetBytes("hello from CodeFlow");

        var artifactUri = await store.WriteAsync(new MemoryStream(content), metadata);

        artifactUri.Should().Be(new Uri(Path.Combine(
            rootDirectory,
            metadata.TraceId.ToString("N"),
            metadata.RoundId.ToString("N"),
            $"{metadata.ArtifactId:N}.bin")));

        await using var contentStream = await store.ReadAsync(artifactUri);
        using var reader = new StreamReader(contentStream, Encoding.UTF8);
        var roundTrippedContent = await reader.ReadToEndAsync();
        var persistedMetadata = await store.GetMetadataAsync(artifactUri);

        roundTrippedContent.Should().Be("hello from CodeFlow");
        persistedMetadata.TraceId.Should().Be(metadata.TraceId);
        persistedMetadata.RoundId.Should().Be(metadata.RoundId);
        persistedMetadata.ArtifactId.Should().Be(metadata.ArtifactId);
        persistedMetadata.ContentType.Should().Be("text/plain");
        persistedMetadata.FileName.Should().Be("greeting.txt");
        persistedMetadata.ContentLength.Should().Be(content.Length);
        persistedMetadata.ContentHash.Should().NotBeNullOrWhiteSpace();
        persistedMetadata.CreatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task WriteAsync_ShouldDedupeIdenticalContentAcrossDistinctArtifactUris()
    {
        var store = CreateStore();
        var content = Encoding.UTF8.GetBytes("same bytes");
        var metadata1 = new ArtifactMetadata(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var metadata2 = new ArtifactMetadata(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var artifactUri1 = await store.WriteAsync(new MemoryStream(content), metadata1);
        var artifactUri2 = await store.WriteAsync(new MemoryStream(content), metadata2);

        var metadataResult1 = await store.GetMetadataAsync(artifactUri1);
        var metadataResult2 = await store.GetMetadataAsync(artifactUri2);

        metadataResult1.ContentHash.Should().Be(metadataResult2.ContentHash);
        Directory.GetFiles(Path.Combine(rootDirectory, ".blobs"), "*.bin")
            .Should()
            .HaveCount(1);

        await using var stream1 = await store.ReadAsync(artifactUri1);
        await using var stream2 = await store.ReadAsync(artifactUri2);
        using var reader1 = new StreamReader(stream1, Encoding.UTF8);
        using var reader2 = new StreamReader(stream2, Encoding.UTF8);

        (await reader1.ReadToEndAsync()).Should().Be("same bytes");
        (await reader2.ReadToEndAsync()).Should().Be("same bytes");
    }

    [Fact]
    public async Task ReadAsync_Rejects_Sidecar_Pointing_Outside_Blob_Root()
    {
        var store = CreateStore();
        var metadata = new ArtifactMetadata(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            ArtifactId: Guid.NewGuid(),
            ContentType: "text/plain",
            FileName: "malicious.txt");

        var uri = await store.WriteAsync(new MemoryStream(Encoding.UTF8.GetBytes("safe payload")), metadata);

        // Tamper with the sidecar to point at a path-traversal target outside the .blobs directory.
        var sidecarPath = uri.LocalPath + ".json";
        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        var tamperedContent = sidecarContent.Replace(
            "\"blobRelativePath\":",
            "\"blobRelativePath\": \"../../../../../../etc/passwd\", \"__orig\":");
        await File.WriteAllTextAsync(sidecarPath, tamperedContent);

        var readAct = () => store.ReadAsync(uri);
        await readAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*outside the configured blob directory*");

        var metadataAct = () => store.GetMetadataAsync(uri);
        await metadataAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*outside the configured blob directory*");
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private IArtifactStore CreateStore()
    {
        return new FileSystemArtifactStore(new FileSystemArtifactStoreOptions(rootDirectory));
    }
}
