namespace CodeFlow.Persistence;

public sealed record ArtifactMetadata(
    Guid TraceId,
    Guid RoundId,
    Guid ArtifactId,
    string? ContentType = null,
    string? FileName = null,
    string? ContentHash = null,
    long ContentLength = 0,
    DateTime? CreatedAtUtc = null);
