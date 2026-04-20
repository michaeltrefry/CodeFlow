using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeFlow.Api.Validation;

public static class AgentConfigValidator
{
    private static readonly Regex KeyPattern = new("^[a-z0-9]+(?:[-_][a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly HashSet<string> KnownProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
        "anthropic",
        "lmstudio"
    };

    public static ValidationResult ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return ValidationResult.Fail("Agent key must not be empty.");
        }

        if (key.Length > 64)
        {
            return ValidationResult.Fail("Agent key must be 64 characters or fewer.");
        }

        if (!KeyPattern.IsMatch(key))
        {
            return ValidationResult.Fail("Agent key must be lowercase alphanumeric with optional dashes or underscores.");
        }

        return ValidationResult.Ok();
    }

    public static ValidationResult ValidateConfig(JsonElement? config)
    {
        if (config is null || config.Value.ValueKind == JsonValueKind.Undefined || config.Value.ValueKind == JsonValueKind.Null)
        {
            return ValidationResult.Fail("Agent config must be supplied.");
        }

        if (config.Value.ValueKind != JsonValueKind.Object)
        {
            return ValidationResult.Fail("Agent config must be a JSON object.");
        }

        var type = ReadStringProperty(config.Value, "type") ?? "agent";
        var isHitl = string.Equals(type, "hitl", StringComparison.OrdinalIgnoreCase);

        if (!isHitl)
        {
            var provider = ReadStringProperty(config.Value, "provider");
            if (string.IsNullOrWhiteSpace(provider))
            {
                return ValidationResult.Fail("Agent config must include a non-empty 'provider' unless type is 'hitl'.");
            }

            if (!KnownProviders.Contains(provider))
            {
                return ValidationResult.Fail($"Unknown provider '{provider}'. Known: {string.Join(", ", KnownProviders)}.");
            }

            var model = ReadStringProperty(config.Value, "model");
            if (string.IsNullOrWhiteSpace(model))
            {
                return ValidationResult.Fail("Agent config must include a non-empty 'model' unless type is 'hitl'.");
            }
        }

        return ValidationResult.Ok();
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }
}

public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok() => new(true, null);

    public static ValidationResult Fail(string error) => new(false, error);
}
