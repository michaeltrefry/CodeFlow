using System.Net;
using System.Text;

namespace CodeFlow.Runtime;

/// <summary>
/// Shared retry / back-off policy for chat-model HTTP clients (Anthropic, OpenAI-compatible,
/// LM Studio). F-003 in the 2026-04-28 backend review — both <c>SendWithRetryAsync</c>,
/// <c>ShouldRetry</c>, and <c>GetRetryDelay</c> were ~95% identical between
/// <c>AnthropicModelClient</c> and <c>OpenAiCompatibleResponsesModelClientBase</c>; new providers
/// (Bedrock, Azure-OpenAI, Vertex) would have copied the same skeleton.
/// </summary>
internal static class ChatModelHttpRetry
{
    /// <summary>
    /// Send a request with bounded retries on retryable HTTP status codes and transient
    /// <see cref="HttpRequestException"/>s. Honors the standard <c>Retry-After</c> header
    /// (delta or absolute date) and exponential back-off otherwise. The caller must clone the
    /// request body upfront — we re-clone per attempt so the body stream stays readable.
    /// </summary>
    /// <param name="extraRetryStatusCheck">Provider-specific extra retryable status codes
    /// (e.g. Anthropic's 529 "Overloaded"). Returns true when the status is retryable beyond
    /// the standard set.</param>
    /// <param name="extraRetryAfterExtractor">Provider-specific Retry-After parsing for headers
    /// not exposed via <see cref="System.Net.Http.Headers.HttpResponseHeaders.RetryAfter"/>
    /// (e.g. Anthropic's lower-cased <c>retry-after</c>). Returns the parsed delay or null.</param>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        int maxRetryAttempts,
        TimeSpan initialRetryDelay,
        Func<HttpStatusCode, bool>? extraRetryStatusCheck,
        Func<HttpResponseMessage, TimeSpan?>? extraRetryAfterExtractor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(request);

        var attempt = 0;

        while (true)
        {
            attempt++;

            var requestClone = await CloneRequestAsync(request, cancellationToken);

            try
            {
                var response = await httpClient.SendAsync(requestClone, cancellationToken);

                var retryable = ShouldRetry(response.StatusCode)
                    || (extraRetryStatusCheck?.Invoke(response.StatusCode) ?? false);

                if (!retryable || attempt >= maxRetryAttempts)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, initialRetryDelay, attempt, extraRetryAfterExtractor);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxRetryAttempts)
            {
                await Task.Delay(
                    GetRetryDelay(response: null, initialRetryDelay, attempt, extraRetryAfterExtractor),
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Standard retryable status set: 408, 429, 500-class. Provider-specific extras (Anthropic's
    /// 529) layer on top via the <c>extraRetryStatusCheck</c> hook.
    /// </summary>
    public static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests
        || statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.BadGateway
        || statusCode == HttpStatusCode.ServiceUnavailable
        || statusCode == HttpStatusCode.GatewayTimeout
        || (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(
        HttpResponseMessage? response,
        TimeSpan initialRetryDelay,
        int attempt,
        Func<HttpResponseMessage, TimeSpan?>? extraRetryAfterExtractor)
    {
        if (response is not null)
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta)
            {
                return retryAfterDelta;
            }

            if (response.Headers.RetryAfter?.Date is DateTimeOffset retryAfterDate)
            {
                var delta = retryAfterDate - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            if (extraRetryAfterExtractor?.Invoke(response) is TimeSpan extra)
            {
                return extra > TimeSpan.Zero ? extra : TimeSpan.Zero;
            }
        }

        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var calculatedDelay = TimeSpan.FromMilliseconds(initialRetryDelay.TotalMilliseconds * multiplier);

        return calculatedDelay > TimeSpan.Zero ? calculatedDelay : TimeSpan.Zero;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var content = await request.Content.ReadAsStringAsync(cancellationToken);
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType);

            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
