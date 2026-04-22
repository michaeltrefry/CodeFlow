using System.Net;
using System.Text;

namespace CodeFlow.Runtime;

public sealed class ModelClientHttpException : HttpRequestException
{
    public ModelClientHttpException(
        string message,
        HttpStatusCode statusCode,
        string method,
        Uri? requestUri,
        IReadOnlyDictionary<string, string[]> requestHeaders,
        string? requestBody,
        string? providerErrorMessage,
        string? responseReasonPhrase,
        IReadOnlyDictionary<string, string[]> responseHeaders,
        string? responseBody)
        : base(message, inner: null, statusCode)
    {
        Method = method;
        RequestUri = requestUri;
        RequestHeaders = requestHeaders;
        RequestBody = requestBody;
        ProviderErrorMessage = providerErrorMessage;
        ResponseReasonPhrase = responseReasonPhrase;
        ResponseHeaders = responseHeaders;
        ResponseBody = responseBody;
    }

    public string Method { get; }

    public Uri? RequestUri { get; }

    public IReadOnlyDictionary<string, string[]> RequestHeaders { get; }

    public string? RequestBody { get; }

    public string? ProviderErrorMessage { get; }

    public string? ResponseReasonPhrase { get; }

    public IReadOnlyDictionary<string, string[]> ResponseHeaders { get; }

    public string? ResponseBody { get; }

    public string BuildDiagnosticsText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("HTTP diagnostics");
        builder.AppendLine();
        builder.Append("Request Method: ").AppendLine(Method);
        builder.Append("Request URL: ").AppendLine(RequestUri?.ToString() ?? "(unknown)");
        AppendHeaders(builder, "Request Headers", RequestHeaders);
        AppendBody(builder, "Request Body", RequestBody);

        builder.AppendLine();
        builder.Append("Response Status: ").Append((int?)StatusCode is int statusCode ? statusCode.ToString() : "(unknown)");
        if (!string.IsNullOrWhiteSpace(ResponseReasonPhrase))
        {
            builder.Append(' ').Append(ResponseReasonPhrase);
        }
        builder.AppendLine();
        AppendHeaders(builder, "Response Headers", ResponseHeaders);
        AppendBody(builder, "Response Body", ResponseBody);

        return builder.ToString().TrimEnd();
    }

    private static void AppendHeaders(
        StringBuilder builder,
        string title,
        IReadOnlyDictionary<string, string[]> headers)
    {
        builder.AppendLine(title + ":");
        if (headers.Count == 0)
        {
            builder.AppendLine("(none)");
            return;
        }

        foreach (var header in headers.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (header.Value.Length == 0)
            {
                builder.Append(header.Key).AppendLine(":");
                continue;
            }

            foreach (var value in header.Value)
            {
                builder.Append(header.Key).Append(": ").AppendLine(value);
            }
        }
    }

    private static void AppendBody(
        StringBuilder builder,
        string title,
        string? body)
    {
        builder.AppendLine(title + ":");
        builder.AppendLine(string.IsNullOrEmpty(body) ? "(empty)" : body);
    }
}
