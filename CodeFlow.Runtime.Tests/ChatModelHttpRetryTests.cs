using System.Net;
using System.Text;
using CodeFlow.Runtime.LMStudio;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

public sealed class ChatModelHttpRetryTests
{
    [Fact]
    public async Task SendWithRetryAsync_ShouldNotDuplicateContentTypeWhenCloningRequest()
    {
        // Regression for an LM Studio interop bug: the previous CloneRequestAsync re-copied
        // Content-Type from the original request.Content onto the clone after StringContent's
        // constructor had already set it. .NET serializes the duplicate as a single
        // `Content-Type: application/json; charset=utf-8, application/json; charset=utf-8`
        // header, which Express's body parser does not match as JSON. The receiver saw
        // `req.body = {}` and rejected the call with "Missing required parameter: 'model'"
        // even though every CodeFlow-side diagnostic showed a fully-populated payload.
        var handler = new ContentTypeCapturingHandler();
        using var httpClient = new HttpClient(handler);

        var client = new LMStudioModelClient(
            httpClient,
            new LMStudioModelClientOptions
            {
                ResponsesEndpoint = new Uri("http://localhost/v1/responses"),
                InitialRetryDelay = TimeSpan.Zero
            });

        await client.InvokeAsync(
            new InvocationRequest(
                Messages: [new ChatMessage(ChatMessageRole.User, "ping")],
                Tools: null,
                Model: "test-model"));

        handler.CapturedContentTypes.Should().ContainSingle("Content-Type must be sent exactly once");
        handler.CapturedContentTypes[0].Should().Be("application/json; charset=utf-8");
    }

    private sealed class ContentTypeCapturingHandler : HttpMessageHandler
    {
        public IList<string> CapturedContentTypes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                foreach (var value in request.Content.Headers.GetValues("Content-Type"))
                {
                    CapturedContentTypes.Add(value);
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"completed","output":[],"usage":{"input_tokens":0,"output_tokens":0,"total_tokens":0}}""",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
