using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeFlow.Runtime.Web;

/// <summary>
/// Bounded host tools so agents can look up framework/install/Docker-Hub guidance without
/// receiving ambient internal-network access. Two operations: <c>web_fetch</c> downloads a
/// public HTTP/HTTPS URL into a bounded text body; <c>web_search</c> delegates to a pluggable
/// <see cref="IWebSearchProvider"/>. Both run through <see cref="UrlPolicy"/> for SSRF defense
/// (literal URL + resolved IPs + every redirect Location) and never send credentials, cookies,
/// or auth headers regardless of how the agent phrases the request.
/// </summary>
public sealed class WebHostToolService
{
    public const string WebFetchToolName = "web_fetch";
    public const string WebSearchToolName = "web_search";

    private static readonly Regex ScriptStyleStripper = new(
        @"<(script|style)\b[^<]*(?:(?!</\1>)<[^<]*)*</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagStripper = new(
        @"<[^>]+>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WhitespaceCollapser = new(
        @"[\r\n\t ]{2,}",
        RegexOptions.Compiled);

    private readonly WebToolOptions options;
    private readonly HttpMessageHandler messageHandler;
    private readonly IWebSearchProvider searchProvider;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>> hostResolver;

    public WebHostToolService(
        WebToolOptions? options = null,
        HttpMessageHandler? messageHandler = null,
        IWebSearchProvider? searchProvider = null,
        Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>>? hostResolver = null)
    {
        this.options = options ?? new WebToolOptions();
        var errors = this.options.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Web tool options are invalid: " + string.Join(" ", errors));
        }

        this.messageHandler = messageHandler ?? CreateDefaultHandler();
        this.searchProvider = searchProvider ?? new NullWebSearchProvider();
        this.hostResolver = hostResolver ?? DefaultHostResolverAsync;
    }

    public async Task<ToolResult> FetchAsync(
        ToolCall toolCall,
        ToolExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        _ = context;

        var arguments = toolCall.Arguments as JsonObject;
        var url = arguments?["url"] as JsonValue is { } urlValue && urlValue.TryGetValue<string>(out var urlText)
            ? urlText
            : null;

        var validation = UrlPolicy.ValidateLiteralUrl(options, url ?? string.Empty);
        if (!validation.Allowed)
        {
            return Refusal(toolCall.Id, validation.Code!, validation.Reason!);
        }

        try
        {
            var addresses = await hostResolver(validation.Uri!.Host, cancellationToken);
            var resolved = UrlPolicy.ValidateResolvedAddresses(options, validation.Uri, addresses);
            if (!resolved.Allowed)
            {
                return Refusal(toolCall.Id, resolved.Code!, resolved.Reason!);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refusal(toolCall.Id, "dns-failed", $"DNS resolution failed for '{validation.Uri!.Host}': {ex.Message}");
        }

        using var client = new HttpClient(messageHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(options.FetchTimeoutSeconds)
        };

        var currentUri = validation.Uri!;
        for (var hop = 0; hop <= options.MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            ApplyUserAgent(request);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Refusal(toolCall.Id, "fetch-timeout", $"Request to {currentUri} timed out after {options.FetchTimeoutSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                return Refusal(toolCall.Id, "fetch-failed", $"HTTP request to {currentUri} failed: {ex.Message}");
            }

            if (IsRedirect(response.StatusCode))
            {
                response.Dispose();
                if (hop >= options.MaxRedirects)
                {
                    return Refusal(toolCall.Id, "redirect-limit", $"Exceeded redirect limit of {options.MaxRedirects} hops.");
                }

                var location = response.Headers.Location;
                if (location is null)
                {
                    return Refusal(toolCall.Id, "redirect-invalid", "Redirect response had no Location header.");
                }

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                var nextValidation = UrlPolicy.ValidateLiteralUrl(options, nextUri.ToString());
                if (!nextValidation.Allowed)
                {
                    return Refusal(toolCall.Id, nextValidation.Code!, $"Redirect to '{nextUri}' refused: {nextValidation.Reason}");
                }

                try
                {
                    var redirectAddresses = await hostResolver(nextValidation.Uri!.Host, cancellationToken);
                    var redirectResolved = UrlPolicy.ValidateResolvedAddresses(options, nextValidation.Uri, redirectAddresses);
                    if (!redirectResolved.Allowed)
                    {
                        return Refusal(toolCall.Id, redirectResolved.Code!, $"Redirect to '{nextUri}' refused: {redirectResolved.Reason}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Refusal(toolCall.Id, "dns-failed", $"DNS resolution failed for redirect host '{nextValidation.Uri!.Host}': {ex.Message}");
                }

                currentUri = nextValidation.Uri!;
                continue;
            }

            using (response)
            {
                var body = await ReadBoundedAsync(response, cancellationToken);
                var extracted = ExtractText(body.Text, response.Content.Headers.ContentType?.MediaType);
                var truncatedText = extracted.Length > options.MaxExtractedTextBytes
                    ? extracted[..(int)Math.Min(int.MaxValue, options.MaxExtractedTextBytes)]
                    : extracted;
                var textTruncated = body.Truncated || extracted.Length > options.MaxExtractedTextBytes;

                var payload = new JsonObject
                {
                    ["ok"] = (int)response.StatusCode is >= 200 and < 300,
                    ["status"] = (int)response.StatusCode,
                    ["finalUrl"] = currentUri.ToString(),
                    ["contentType"] = response.Content.Headers.ContentType?.MediaType,
                    ["text"] = truncatedText,
                    ["textTruncated"] = textTruncated,
                    ["responseBytes"] = body.Bytes,
                    ["responseTruncated"] = body.Truncated
                };

                return new ToolResult(
                    toolCall.Id,
                    payload.ToJsonString(),
                    IsError: (int)response.StatusCode is < 200 or >= 300);
            }
        }

        return Refusal(toolCall.Id, "redirect-limit", $"Exceeded redirect limit of {options.MaxRedirects} hops.");
    }

    public async Task<ToolResult> SearchAsync(
        ToolCall toolCall,
        ToolExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        _ = context;

        var arguments = toolCall.Arguments as JsonObject;
        var query = arguments?["query"] as JsonValue is { } queryValue && queryValue.TryGetValue<string>(out var queryText)
            ? queryText
            : null;

        if (string.IsNullOrWhiteSpace(query))
        {
            return Refusal(toolCall.Id, "query-required", "web_search requires a non-empty 'query'.");
        }

        var maxResults = options.MaxSearchResults;
        if (arguments?["maxResults"] is JsonValue mrValue && mrValue.TryGetValue<int>(out var mr) && mr > 0)
        {
            maxResults = Math.Min(mr, options.MaxSearchResults);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.SearchTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        WebSearchProviderResult providerResult;
        try
        {
            providerResult = await searchProvider.SearchAsync(query.Trim(), maxResults, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return Refusal(toolCall.Id, "search-timeout", $"Search timed out after {options.SearchTimeoutSeconds}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refusal(toolCall.Id, "search-failed", $"Search backend failed: {ex.Message}");
        }

        if (!providerResult.Ok)
        {
            return Refusal(toolCall.Id, providerResult.RefusalCode ?? "search-refused", providerResult.RefusalReason ?? "Search refused.");
        }

        var hits = new JsonArray();
        foreach (var hit in providerResult.Hits.Take(maxResults))
        {
            // Defense-in-depth: even if the provider returned a private-network URL, refuse
            // to surface it to the agent.
            var hitValidation = UrlPolicy.ValidateLiteralUrl(options, hit.Url);
            if (!hitValidation.Allowed)
            {
                continue;
            }

            hits.Add(new JsonObject
            {
                ["title"] = hit.Title,
                ["url"] = hit.Url,
                ["snippet"] = hit.Snippet
            });
        }

        return new ToolResult(
            toolCall.Id,
            new JsonObject
            {
                ["ok"] = true,
                ["query"] = query.Trim(),
                ["hits"] = hits
            }.ToJsonString());
    }

    private void ApplyUserAgent(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeFlow", "1.0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/michaeltrefry/CodeFlow)"));

        // Defense-in-depth: never send credentials/cookies/auth headers regardless of
        // upstream config. The HttpMessageHandler already disables cookie storage; clearing
        // these headers here protects against an upstream typo / middleware injection.
        request.Headers.Authorization = null;
        request.Headers.Remove("Cookie");
        request.Headers.Remove("Authorization");
    }

    private async Task<BoundedBody> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var max = options.MaxResponseBytes;
        var buffer = new byte[8192];
        var collected = new MemoryStream();
        long total = 0;
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var remaining = max - total;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var toCopy = (int)Math.Min(read, remaining);
            collected.Write(buffer, 0, toCopy);
            total += toCopy;
            if (toCopy < read)
            {
                truncated = true;
                break;
            }
        }

        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        var text = encoding.GetString(collected.ToArray());
        return new BoundedBody(text, total, truncated);
    }

    private static string ExtractText(string body, string? contentType)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }

        if (contentType is not null
            && (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)))
        {
            var withoutScripts = ScriptStyleStripper.Replace(body, " ");
            var stripped = TagStripper.Replace(withoutScripts, " ");
            var collapsed = WhitespaceCollapser.Replace(stripped, " ").Trim();
            return WebUtility.HtmlDecode(collapsed);
        }

        return body;
    }

    private static bool IsRedirect(HttpStatusCode status) => status switch
    {
        HttpStatusCode.MovedPermanently => true,
        HttpStatusCode.Found => true,
        HttpStatusCode.SeeOther => true,
        HttpStatusCode.TemporaryRedirect => true,
        HttpStatusCode.PermanentRedirect => true,
        _ => false
    };

    private static ToolResult Refusal(string callId, string code, string reason)
    {
        var refusal = new JsonObject
        {
            ["code"] = code,
            ["reason"] = reason,
            ["axis"] = "web-policy"
        };

        return new ToolResult(
            callId,
            new JsonObject
            {
                ["ok"] = false,
                ["refusal"] = refusal
            }.ToJsonString(),
            IsError: true);
    }

    private static HttpMessageHandler CreateDefaultHandler() =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli | DecompressionMethods.Deflate
        };

    private static async Task<IReadOnlyList<IPAddress>> DefaultHostResolverAsync(string host, CancellationToken ct) =>
        await Dns.GetHostAddressesAsync(host, ct);

    private sealed record BoundedBody(string Text, long Bytes, bool Truncated);
}
