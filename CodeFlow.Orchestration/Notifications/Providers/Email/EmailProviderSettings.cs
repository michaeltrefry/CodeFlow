using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Orchestration.Notifications.Providers.Email;

/// <summary>
/// Parsed view of a notification-provider config row's <c>AdditionalConfigJson</c> for
/// channel <c>Email</c>. The JSON shape:
/// <code>
/// { "engine": "ses",  "region": "us-east-1" }
/// { "engine": "smtp", "host": "smtp.relay.example.com", "port": 587, "username": "app@example.com", "useStartTls": true }
/// </code>
/// </summary>
public sealed record EmailProviderSettings(
    EmailEngine Engine,
    SesEmailSettings? Ses,
    SmtpEmailSettings? Smtp)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Parses the engine + engine-specific fields from <paramref name="additionalConfigJson"/>.
    /// Throws <see cref="EmailProviderSettingsException"/> on malformed JSON or an unknown
    /// engine — the factory turns that into a Failed audit row at dispatch time.
    /// </summary>
    public static EmailProviderSettings Parse(string? additionalConfigJson)
    {
        if (string.IsNullOrWhiteSpace(additionalConfigJson))
        {
            throw new EmailProviderSettingsException(
                "additional_config_json is empty; email providers require an engine selector and engine-specific settings.");
        }

        EmailProviderSettingsRaw raw;
        try
        {
            raw = JsonSerializer.Deserialize<EmailProviderSettingsRaw>(additionalConfigJson, JsonOptions)
                ?? throw new EmailProviderSettingsException("additional_config_json deserialised to null.");
        }
        catch (JsonException ex)
        {
            throw new EmailProviderSettingsException($"additional_config_json is not valid JSON: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(raw.Engine))
        {
            throw new EmailProviderSettingsException(
                "additional_config_json is missing the 'engine' selector. Expected 'ses' or 'smtp'.");
        }

        var engine = raw.Engine.Trim().ToLowerInvariant() switch
        {
            "ses" => EmailEngine.Ses,
            "smtp" => EmailEngine.Smtp,
            var unknown => throw new EmailProviderSettingsException(
                $"Unknown email engine '{unknown}'. Expected 'ses' or 'smtp'."),
        };

        return engine switch
        {
            EmailEngine.Ses => new EmailProviderSettings(
                EmailEngine.Ses,
                Ses: ParseSes(raw),
                Smtp: null),
            EmailEngine.Smtp => new EmailProviderSettings(
                EmailEngine.Smtp,
                Ses: null,
                Smtp: ParseSmtp(raw)),
            _ => throw new EmailProviderSettingsException($"Unhandled engine {engine}."),
        };
    }

    private static SesEmailSettings ParseSes(EmailProviderSettingsRaw raw)
    {
        if (string.IsNullOrWhiteSpace(raw.Region))
        {
            throw new EmailProviderSettingsException(
                "SES engine requires 'region' (e.g. 'us-east-1') in additional_config_json.");
        }

        return new SesEmailSettings(Region: raw.Region.Trim());
    }

    private static SmtpEmailSettings ParseSmtp(EmailProviderSettingsRaw raw)
    {
        if (string.IsNullOrWhiteSpace(raw.Host))
        {
            throw new EmailProviderSettingsException(
                "SMTP engine requires 'host' in additional_config_json.");
        }

        var port = raw.Port ?? 587;
        if (port is <= 0 or > 65535)
        {
            throw new EmailProviderSettingsException(
                $"SMTP engine 'port' must be 1..65535; got {port}.");
        }

        return new SmtpEmailSettings(
            Host: raw.Host.Trim(),
            Port: port,
            Username: string.IsNullOrWhiteSpace(raw.Username) ? null : raw.Username.Trim(),
            UseStartTls: raw.UseStartTls ?? true);
    }

    private sealed class EmailProviderSettingsRaw
    {
        [JsonPropertyName("engine")] public string? Engine { get; set; }

        // SES
        [JsonPropertyName("region")] public string? Region { get; set; }

        // SMTP
        [JsonPropertyName("host")] public string? Host { get; set; }
        [JsonPropertyName("port")] public int? Port { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("use_start_tls")] public bool? UseStartTls { get; set; }
    }
}

/// <summary>SES-specific settings; the access-key/secret-key live in the encrypted credential JSON.</summary>
public sealed record SesEmailSettings(string Region);

/// <summary>SMTP-specific settings; the password lives in the encrypted credential plaintext.</summary>
public sealed record SmtpEmailSettings(
    string Host,
    int Port,
    string? Username,
    bool UseStartTls);

public sealed class EmailProviderSettingsException : Exception
{
    public EmailProviderSettingsException(string message) : base(message) { }
    public EmailProviderSettingsException(string message, Exception inner) : base(message, inner) { }
}
