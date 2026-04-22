using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

internal static class ModelClientHttpErrorHelper
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SensitivePropertyNameFragments =
    [
        "api_key",
        "apikey",
        "authorization",
        "token",
        "secret",
        "password",
        "x-api-key"
    ];
    private static readonly string[] SensitiveHeaderNameFragments =
    [
        "authorization",
        "cookie",
        "api-key",
        "apikey",
        "token",
        "secret"
    ];

    private const int ErrorContentLimit = 128 * 1024;

    public static async Task EnsureSuccessStatusCodeAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append("Response status code does not indicate success: ")
            .Append((int)response.StatusCode)
            .Append(" (")
            .Append(response.ReasonPhrase ?? response.StatusCode.ToString())
            .Append(").");

        if (request.RequestUri is not null)
        {
            builder.AppendLine()
                .Append("Request URL: ")
                .Append(request.RequestUri);
        }

        var rawRequestContent = await ReadRawContentAsync(request.Content, cancellationToken);
        var rawResponseContent = await ReadRawContentAsync(response.Content, cancellationToken);
        var requestContent = string.IsNullOrWhiteSpace(rawRequestContent)
            ? null
            : TruncateForErrorMessage(SanitizeJsonContent(rawRequestContent));
        if (!string.IsNullOrWhiteSpace(requestContent))
        {
            builder.AppendLine()
                .Append("Request: ")
                .Append(requestContent);
        }

        var responseContent = string.IsNullOrWhiteSpace(rawResponseContent)
            ? null
            : TruncateForErrorMessage(SanitizeJsonContent(rawResponseContent));
        if (!string.IsNullOrWhiteSpace(responseContent))
        {
            builder.AppendLine()
                .Append("Response: ")
                .Append(responseContent);
        }

        throw new ModelClientHttpException(
            builder.ToString(),
            response.StatusCode,
            request.Method.Method,
            request.RequestUri,
            CaptureHeaders(request.Headers, request.Content?.Headers),
            rawRequestContent,
            ExtractProviderErrorMessage(rawResponseContent),
            response.ReasonPhrase,
            CaptureHeaders(response.Headers, response.Content?.Headers),
            rawResponseContent);
    }

    private static async Task<string?> ReadRawContentAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return null;
        }

        var raw = await content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    private static string SanitizeJsonContent(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            if (node is null)
            {
                return raw;
            }

            RedactSensitiveValues(node);
            return node.ToJsonString(SerializerOptions);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static string? ExtractProviderErrorMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (TryGetString(root, "message", out var directMessage))
            {
                return directMessage;
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && TryGetString(error, "message", out var nestedMessage))
            {
                return nestedMessage;
            }
        }
        catch (JsonException)
        {
            // Fall through to null when the provider returned non-JSON content.
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void RedactSensitiveValues(JsonNode node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var entry in jsonObject.ToList())
                {
                    if (entry.Value is null)
                    {
                        continue;
                    }

                    if (IsSensitivePropertyName(entry.Key))
                    {
                        jsonObject[entry.Key] = "[REDACTED]";
                        continue;
                    }

                    RedactSensitiveValues(entry.Value);
                }

                break;

            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    if (item is not null)
                    {
                        RedactSensitiveValues(item);
                    }
                }

                break;
        }
    }

    private static bool IsSensitivePropertyName(string propertyName)
    {
        return SensitivePropertyNameFragments.Any(fragment =>
            propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string TruncateForErrorMessage(string value)
    {
        if (value.Length <= ErrorContentLimit)
        {
            return value;
        }

        var omittedCharacters = value.Length - ErrorContentLimit;
        var headLength = ErrorContentLimit / 2;
        var tailLength = ErrorContentLimit - headLength;

        return string.Concat(
            value.AsSpan(0, headLength),
            $"...(truncated {omittedCharacters} chars)...",
            value.AsSpan(value.Length - tailLength));
    }

    private static IReadOnlyDictionary<string, string[]> CaptureHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? contentHeaders)
    {
        var captured = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        AddHeaders(captured, headers);
        if (contentHeaders is not null)
        {
            AddHeaders(captured, contentHeaders);
        }

        return captured;
    }

    private static void AddHeaders(
        IDictionary<string, string[]> captured,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        foreach (var header in headers)
        {
            var values = header.Value
                .Select(value => IsSensitiveHeaderName(header.Key) ? "[REDACTED]" : value)
                .ToArray();

            if (captured.TryGetValue(header.Key, out var existing))
            {
                captured[header.Key] = existing.Concat(values).ToArray();
            }
            else
            {
                captured[header.Key] = values;
            }
        }
    }

    private static bool IsSensitiveHeaderName(string headerName)
    {
        return SensitiveHeaderNameFragments.Any(fragment =>
            headerName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
