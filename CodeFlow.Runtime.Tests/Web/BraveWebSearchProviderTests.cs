using System.Net;
using System.Text;
using CodeFlow.Runtime.Web;
using FluentAssertions;
using Xunit;

namespace CodeFlow.Runtime.Tests.Web;

public sealed class BraveWebSearchProviderTests
{
    [Fact]
    public async Task SearchAsync_RefusesWhenCredentialsAreMissing()
    {
        var provider = new BraveWebSearchProvider(_ => null, new ScriptedHttpHandler());

        var result = await provider.SearchAsync("anything", maxResults: 5, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.RefusalCode.Should().Be("brave-key-missing");
    }

    [Fact]
    public async Task SearchAsync_RefusesWhenEndpointIsNotHttp()
    {
        var provider = new BraveWebSearchProvider(
            _ => new BraveWebSearchProvider.BraveCredentials("k", "ftp://example.invalid/"),
            new ScriptedHttpHandler());

        var result = await provider.SearchAsync("q", 5, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.RefusalCode.Should().Be("brave-endpoint-invalid");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "brave-key-rejected")]
    [InlineData(HttpStatusCode.Forbidden, "brave-key-rejected")]
    [InlineData(HttpStatusCode.TooManyRequests, "brave-rate-limited")]
    [InlineData(HttpStatusCode.InternalServerError, "brave-http-error")]
    public async Task SearchAsync_MapsHttpFailuresToStructuredRefusals(
        HttpStatusCode status,
        string expectedCode)
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(new HttpResponseMessage(status));
        var provider = new BraveWebSearchProvider(
            _ => new BraveWebSearchProvider.BraveCredentials("token"),
            handler);

        var result = await provider.SearchAsync("q", 5, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.RefusalCode.Should().Be(expectedCode);
    }

    [Fact]
    public async Task SearchAsync_SendsSubscriptionTokenAndQueryParams()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(JsonResponse("""{"web":{"results":[]}}"""));
        var provider = new BraveWebSearchProvider(
            _ => new BraveWebSearchProvider.BraveCredentials("subscription-token"),
            handler);

        await provider.SearchAsync("how to use docker.io", maxResults: 30, CancellationToken.None);

        handler.LastRequest!.Headers.GetValues("X-Subscription-Token").Should().Equal("subscription-token");
        handler.LastRequest!.RequestUri!.Host.Should().Be("api.search.brave.com");
        // 30 should clamp to 20 — Brave's documented per-request ceiling.
        handler.LastRequest!.RequestUri!.Query
            .Should().Contain("count=20")
            .And.Contain("q=how%20to%20use%20docker.io");
    }

    [Fact]
    public async Task SearchAsync_MapsResultsAndCapsToMaxResults()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(JsonResponse("""
            {
              "web": {
                "results": [
                  { "title": "First",  "url": "https://docs.example.com/a", "description": "One" },
                  { "title": "Second", "url": "https://docs.example.com/b", "description": "Two" },
                  { "title": "Third",  "url": "https://docs.example.com/c", "description": "Three" }
                ]
              }
            }
            """));
        var provider = new BraveWebSearchProvider(
            _ => new BraveWebSearchProvider.BraveCredentials("token"),
            handler);

        var result = await provider.SearchAsync("docs", maxResults: 2, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Hits.Should().HaveCount(2);
        result.Hits[0].Title.Should().Be("First");
        result.Hits[0].Url.Should().Be("https://docs.example.com/a");
        result.Hits[0].Snippet.Should().Be("One");
        result.Hits[1].Title.Should().Be("Second");
    }

    [Fact]
    public async Task SearchAsync_SkipsHitsWithMissingUrl()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(JsonResponse("""
            {
              "web": {
                "results": [
                  { "title": "No url" },
                  { "title": "Has url", "url": "https://example.com" }
                ]
              }
            }
            """));
        var provider = new BraveWebSearchProvider(
            _ => new BraveWebSearchProvider.BraveCredentials("token"),
            handler);

        var result = await provider.SearchAsync("q", 10, CancellationToken.None);

        result.Hits.Should().HaveCount(1);
        result.Hits[0].Url.Should().Be("https://example.com");
    }

    [Fact]
    public async Task SearchAsync_RefusesOnUnparseableBody()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
        });
        var provider = new BraveWebSearchProvider(
            _ => new BraveWebSearchProvider.BraveCredentials("token"),
            handler);

        var result = await provider.SearchAsync("q", 5, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.RefusalCode.Should().Be("brave-parse-failed");
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new();

        public HttpRequestMessage? LastRequest { get; private set; }

        public void Enqueue(HttpResponseMessage response) => responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("ScriptedHttpHandler ran out of responses.");
            }

            return Task.FromResult(responses.Dequeue());
        }
    }
}
