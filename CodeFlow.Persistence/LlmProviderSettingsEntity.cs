namespace CodeFlow.Persistence;

public sealed class LlmProviderSettingsEntity
{
    public string Provider { get; set; } = string.Empty;

    public byte[]? EncryptedApiKey { get; set; }

    public string? EndpointUrl { get; set; }

    public string? ApiVersion { get; set; }

    public string? ModelsJson { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
