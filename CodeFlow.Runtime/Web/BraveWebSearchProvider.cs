using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Web;

/// <summary>
/// <see cref="IWebSearchProvider"/> backed by the Brave Web Search API. Subscription token is
/// passed via the <c>X-Subscription-Token</c> header per Brave's docs. The implementation maps
/// HTTP/auth/quota failures into structured <see cref="WebSearchProviderResult.Refused"/> shapes
/// so agents see a clear refusal code rather than an opaque exception.
/// </summary>
public sealed class BraveWebSearchProvider : IWebSearchProvider
{
    public const string DefaultEndpoint = "https://api.search.brave.com/res/v1/web/search";

    private readonly Func<string, BraveCredentials?> credentialsResolver;
    private readonly HttpMessageHandler messageHandler;
    private readonly bool disposeHandler;

    public BraveWebSearchProvider(
        Func<string, BraveCredentials?> credentialsResolver,
        HttpMessageHandler? messageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(credentialsResolver);
        this.credentialsResolver = credentialsResolver;
        this.messageHandler = messageHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli | DecompressionMethods.Deflate,
        };
        this.disposeHandler = messageHandler is null;
    }

    public async Task<WebSearchProviderResult> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var credentials = credentialsResolver(query);
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            return WebSearchProviderResult.Refused(
                "brave-key-missing",
                "Brave Web Search is selected but no subscription token is stored. "
                + "Set the API key on the Web Search admin page.");
        }

        if (!Uri.TryCreate(credentials.Endpoint ?? DefaultEndpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return WebSearchProviderResult.Refused(
                "brave-endpoint-invalid",
                "Brave Web Search endpoint is not a valid absolute http(s) URL.");
        }

        var clamped = Math.Clamp(maxResults, 1, 20);
        var requestUri = new UriBuilder(endpoint)
        {
            Query = $"q={Uri.EscapeDataString(query)}&count={clamped}",
        }.Uri;

        using var client = new HttpClient(messageHandler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Subscription-Token", credentials.ApiKey);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeFlow", "1.0"));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return WebSearchProviderResult.Refused(
                "brave-request-failed",
                $"Brave Web Search request failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebSearchProviderResult.Refused(
                "brave-timeout",
                "Brave Web Search request timed out before the response arrived.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return WebSearchProviderResult.Refused(
                    "brave-key-rejected",
                    $"Brave rejected the subscription token (HTTP {(int)response.StatusCode}). "
                    + "Verify the key on the Web Search admin page.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return WebSearchProviderResult.Refused(
                    "brave-rate-limited",
                    "Brave returned 429 Too Many Requests. Slow tool usage or upgrade the plan.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebSearchProviderResult.Refused(
                    "brave-http-error",
                    $"Brave Web Search returned HTTP {(int)response.StatusCode}.");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return WebSearchProviderResult.Refused(
                    "brave-read-failed",
                    $"Failed to read Brave Web Search response body: {ex.Message}");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(body);
            }
            catch (Exception ex)
            {
                return WebSearchProviderResult.Refused(
                    "brave-parse-failed",
                    $"Failed to parse Brave Web Search response: {ex.Message}");
            }

            var hits = ExtractHits(parsed, clamped);
            return WebSearchProviderResult.Success(hits);
        }
    }

    private static IReadOnlyList<WebSearchHit> ExtractHits(JsonNode? root, int max)
    {
        var results = root?["web"]?["results"] as JsonArray;
        if (results is null || results.Count == 0)
        {
            return Array.Empty<WebSearchHit>();
        }

        var hits = new List<WebSearchHit>(Math.Min(results.Count, max));
        foreach (var node in results)
        {
            if (hits.Count >= max)
            {
                break;
            }

            var url = StringValue(node, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var title = StringValue(node, "title") ?? url;
            var description = StringValue(node, "description");

            hits.Add(new WebSearchHit(title, url, description));
        }

        return hits;
    }

    private static string? StringValue(JsonNode? node, string property)
    {
        if (node is JsonObject obj
            && obj.TryGetPropertyValue(property, out var value)
            && value is JsonValue jv
            && jv.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }

    public sealed record BraveCredentials(string ApiKey, string? Endpoint = null);
}
