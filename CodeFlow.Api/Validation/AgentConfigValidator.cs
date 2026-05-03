using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeFlow.Api.Validation;

public static class AgentConfigValidator
{
    private static readonly Regex KeyPattern = new("^[a-z0-9]+(?:[-_][a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex DecisionOutputPortPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private const int MaxDecisionOutputTemplates = 32;
    private const int MaxDecisionOutputTemplateLength = 16 * 1024;
    private const int MaxHistoryEntries = 32;
    private const int MaxHistoryTotalLength = 32 * 1024;
    private static readonly HashSet<string> KnownProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
        "anthropic",
        "lmstudio"
    };
    private static readonly HashSet<string> AuthorableHistoryRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "user",
        "assistant"
    };
    private static readonly string[] LegacyToolFields = new[] { "enableHostTools", "mcpTools" };

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

        foreach (var legacy in LegacyToolFields)
        {
            if (HasProperty(config.Value, legacy))
            {
                return ValidationResult.Fail(
                    $"'{legacy}' has been removed from agent configuration. Grant tools by assigning agent roles instead.");
            }
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

        var decisionTemplatesResult = ValidateDecisionOutputTemplates(config.Value);
        if (!decisionTemplatesResult.IsValid)
        {
            return decisionTemplatesResult;
        }

        var historyResult = ValidateHistory(config.Value);
        if (!historyResult.IsValid)
        {
            return historyResult;
        }

        return ValidationResult.Ok();
    }

    private static ValidationResult ValidateHistory(JsonElement config)
    {
        if (!config.TryGetProperty("history", out var history))
        {
            return ValidationResult.Ok();
        }

        if (history.ValueKind == JsonValueKind.Null)
        {
            return ValidationResult.Ok();
        }

        if (history.ValueKind != JsonValueKind.Array)
        {
            return ValidationResult.Fail("'history' must be a JSON array of {role, content} entries.");
        }

        var index = 0;
        var totalLength = 0;
        foreach (var entry in history.EnumerateArray())
        {
            if (index >= MaxHistoryEntries)
            {
                return ValidationResult.Fail(
                    $"'history' may contain at most {MaxHistoryEntries} entries.");
            }

            if (entry.ValueKind != JsonValueKind.Object)
            {
                return ValidationResult.Fail(
                    $"'history[{index}]' must be a JSON object with 'role' and 'content'.");
            }

            var role = ReadStringProperty(entry, "role");
            if (string.IsNullOrWhiteSpace(role))
            {
                return ValidationResult.Fail(
                    $"'history[{index}].role' is required and must be a string.");
            }

            if (!AuthorableHistoryRoles.Contains(role))
            {
                return ValidationResult.Fail(
                    $"'history[{index}].role' must be 'user' or 'assistant'. "
                    + "Use 'systemPrompt' for system content; tool messages are not authorable.");
            }

            var content = ReadStringProperty(entry, "content");
            if (string.IsNullOrEmpty(content))
            {
                return ValidationResult.Fail(
                    $"'history[{index}].content' is required and must be a non-empty string.");
            }

            totalLength += content.Length;
            if (totalLength > MaxHistoryTotalLength)
            {
                return ValidationResult.Fail(
                    $"'history' total content length exceeds the {MaxHistoryTotalLength}-character limit.");
            }

            index++;
        }

        return ValidationResult.Ok();
    }

    private static ValidationResult ValidateDecisionOutputTemplates(JsonElement config)
    {
        if (!config.TryGetProperty("decisionOutputTemplates", out var templates))
        {
            return ValidationResult.Ok();
        }

        if (templates.ValueKind == JsonValueKind.Null)
        {
            return ValidationResult.Ok();
        }

        if (templates.ValueKind != JsonValueKind.Object)
        {
            return ValidationResult.Fail("'decisionOutputTemplates' must be a JSON object keyed by output port name.");
        }

        var count = 0;
        foreach (var entry in templates.EnumerateObject())
        {
            count++;
            if (count > MaxDecisionOutputTemplates)
            {
                return ValidationResult.Fail(
                    $"'decisionOutputTemplates' may define at most {MaxDecisionOutputTemplates} entries.");
            }

            if (!IsValidDecisionOutputPortName(entry.Name))
            {
                return ValidationResult.Fail(
                    $"'decisionOutputTemplates' key '{entry.Name}' must be '*' or match [A-Za-z0-9_-]{{1,64}}.");
            }

            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                return ValidationResult.Fail(
                    $"'decisionOutputTemplates.{entry.Name}' must be a string template.");
            }

            var template = entry.Value.GetString();
            if (string.IsNullOrEmpty(template))
            {
                return ValidationResult.Fail(
                    $"'decisionOutputTemplates.{entry.Name}' must not be empty.");
            }

            if (template.Length > MaxDecisionOutputTemplateLength)
            {
                return ValidationResult.Fail(
                    $"'decisionOutputTemplates.{entry.Name}' exceeds the {MaxDecisionOutputTemplateLength}-character limit.");
            }
        }

        return ValidationResult.Ok();
    }

    private static bool IsValidDecisionOutputPortName(string name)
    {
        return name == "*" || DecisionOutputPortPattern.IsMatch(name);
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool HasProperty(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok() => new(true, null);

    public static ValidationResult Fail(string error) => new(false, error);
}
