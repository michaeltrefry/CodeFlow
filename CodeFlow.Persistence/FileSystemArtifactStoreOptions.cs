namespace CodeFlow.Persistence;

public sealed record FileSystemArtifactStoreOptions(
    string RootDirectory,
    long? MaxArtifactBytes = null);
