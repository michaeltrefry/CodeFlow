using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Orchestration.Notifications.Providers.Email.Ses;

/// <summary>
/// SES credential payload encoded in the encrypted_credential column. JSON shape:
/// <c>{ "access_key": "AKIA...", "secret_key": "..." }</c>.
/// When the credential is null/empty, the SDK uses its default credential chain (IAM role,
/// env vars, shared profile, …) — preferred for production deployments.
/// </summary>
public sealed record SesEmailCredentials(string AccessKey, string SecretKey)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static SesEmailCredentials? Parse(string? plaintextCredential)
    {
        if (string.IsNullOrWhiteSpace(plaintextCredential))
        {
            return null;
        }

        SesEmailCredentialsRaw raw;
        try
        {
            raw = JsonSerializer.Deserialize<SesEmailCredentialsRaw>(plaintextCredential, JsonOptions)
                ?? throw new EmailProviderSettingsException("SES credential JSON deserialised to null.");
        }
        catch (JsonException ex)
        {
            throw new EmailProviderSettingsException(
                $"SES credential is not valid JSON. Expected {{\"access_key\":..,\"secret_key\":..}}: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(raw.AccessKey) || string.IsNullOrWhiteSpace(raw.SecretKey))
        {
            throw new EmailProviderSettingsException(
                "SES credential JSON is missing access_key or secret_key.");
        }

        return new SesEmailCredentials(raw.AccessKey.Trim(), raw.SecretKey.Trim());
    }

    private sealed class SesEmailCredentialsRaw
    {
        [JsonPropertyName("access_key")] public string? AccessKey { get; set; }
        [JsonPropertyName("secret_key")] public string? SecretKey { get; set; }
    }
}
