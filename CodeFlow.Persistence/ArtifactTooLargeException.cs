namespace CodeFlow.Persistence;

public sealed class ArtifactTooLargeException : Exception
{
    public ArtifactTooLargeException(long observedBytes, long maxBytes)
        : base($"Artifact payload of {observedBytes} bytes exceeds the configured limit of {maxBytes} bytes.")
    {
        ObservedBytes = observedBytes;
        MaxBytes = maxBytes;
    }

    public long ObservedBytes { get; }

    public long MaxBytes { get; }
}
