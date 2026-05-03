namespace CodeFlow.Persistence;

/// <summary>
/// Single-row admin config for the active web-search adapter. Only one provider is active
/// at a time, so the table is keyed by a fixed sentinel and the <see cref="Provider"/> column
/// carries the chosen backend (see <see cref="WebSearchProviderKeys"/>).
/// </summary>
public sealed class WebSearchProviderSettingsEntity
{
    /// <summary>Fixed sentinel so the table holds at most one row.</summary>
    public const string SingletonId = "active";

    public string Id { get; set; } = SingletonId;

    public string Provider { get; set; } = WebSearchProviderKeys.None;

    public byte[]? EncryptedApiKey { get; set; }

    public string? EndpointUrl { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
