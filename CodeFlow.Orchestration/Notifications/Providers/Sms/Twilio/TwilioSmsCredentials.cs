using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Orchestration.Notifications.Providers.Sms.Twilio;

/// <summary>
/// Twilio credential payload encoded in the encrypted_credential column. JSON shape:
/// <c>{ "account_sid": "AC...", "auth_token": "..." }</c>. Account SID and auth token are
/// paired: the SID identifies the Twilio account, the token authenticates against it. Both
/// are required to call the Messages API.
/// </summary>
public sealed record TwilioSmsCredentials(string AccountSid, string AuthToken)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static TwilioSmsCredentials? Parse(string? plaintextCredential)
    {
        if (string.IsNullOrWhiteSpace(plaintextCredential))
        {
            return null;
        }

        TwilioSmsCredentialsRaw raw;
        try
        {
            raw = JsonSerializer.Deserialize<TwilioSmsCredentialsRaw>(plaintextCredential, JsonOptions)
                ?? throw new TwilioSmsCredentialsException("Twilio credential JSON deserialised to null.");
        }
        catch (JsonException ex)
        {
            throw new TwilioSmsCredentialsException(
                $"Twilio credential is not valid JSON. Expected {{\"account_sid\":..,\"auth_token\":..}}: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(raw.AccountSid) || string.IsNullOrWhiteSpace(raw.AuthToken))
        {
            throw new TwilioSmsCredentialsException(
                "Twilio credential JSON is missing account_sid or auth_token.");
        }

        return new TwilioSmsCredentials(raw.AccountSid.Trim(), raw.AuthToken.Trim());
    }

    private sealed class TwilioSmsCredentialsRaw
    {
        [JsonPropertyName("account_sid")] public string? AccountSid { get; set; }
        [JsonPropertyName("auth_token")] public string? AuthToken { get; set; }
    }
}

public sealed class TwilioSmsCredentialsException : Exception
{
    public TwilioSmsCredentialsException(string message) : base(message) { }
    public TwilioSmsCredentialsException(string message, Exception inner) : base(message, inner) { }
}
