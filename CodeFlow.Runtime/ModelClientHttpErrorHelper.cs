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

        var requestContent = await ReadSanitizedContentAsync(request.Content, cancellationToken);
        if (!string.IsNullOrWhiteSpace(requestContent))
        {
            builder.AppendLine()
                .Append("Request: ")
                .Append(requestContent);
        }

        var responseContent = await ReadSanitizedContentAsync(response.Content, cancellationToken);
        if (!string.IsNullOrWhiteSpace(responseContent))
        {
            builder.AppendLine()
                .Append("Response: ")
                .Append(responseContent);
        }

        throw new HttpRequestException(builder.ToString(), inner: null, response.StatusCode);
    }

    private static async Task<string?> ReadSanitizedContentAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return null;
        }

        var raw = await content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return TruncateForErrorMessage(SanitizeJsonContent(raw));
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
}
