using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Web;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Web;

public sealed class WebHostToolServiceTests
{
    [Fact]
    public async Task FetchAsync_returns_bounded_text_for_public_url()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("hello docs", Encoding.UTF8, "text/plain")
        });
        var service = NewService(handler);

        var result = await service.FetchAsync(NewCall(WebHostToolService.WebFetchToolName, new JsonObject
        {
            ["url"] = "https://docs.example.com/install"
        }), context: null);

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!;
        payload["status"]!.GetValue<int>().Should().Be(200);
        payload["text"]!.GetValue<string>().Should().Contain("hello docs");
        payload["finalUrl"]!.GetValue<string>().Should().Be("https://docs.example.com/install");
        payload["responseTruncated"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_strips_html_to_readable_text()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<html><head><script>evil</script><style>x{}</style></head><body><h1>Title</h1><p>Body</p></body></html>",
                Encoding.UTF8,
                "text/html")
        });
        var service = NewService(handler);

        var result = await service.FetchAsync(NewCall(WebHostToolService.WebFetchToolName, new JsonObject
        {
            ["url"] = "https://docs.example.com/page"
        }), context: null);

        var text = JsonNode.Parse(result.Content)!["text"]!.GetValue<string>();
        text.Should().Contain("Title");
        text.Should().Contain("Body");
        text.Should().NotContain("evil");
        text.Should().NotContain("<script>");
    }

    [Fact]
    public async Task FetchAsync_truncates_response_body_at_max_bytes()
    {
        var big = new string('x', 4096);
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(big, Encoding.UTF8, "text/plain")
        });
        var options = new WebToolOptions { MaxResponseBytes = 256, MaxExtractedTextBytes = 1024 };
        var service = NewService(handler, options);

        var result = await service.FetchAsync(NewCall(WebHostToolService.WebFetchToolName, new JsonObject
        {
            ["url"] = "https://example.com/"
        }), context: null);

        var payload = JsonNode.Parse(result.Content)!;
        payload["responseTruncated"]!.GetValue<bool>().Should().BeTrue();
        payload["responseBytes"]!.GetValue<int>().Should().BeLessThanOrEqualTo(256);
    }

    [Fact]
    public async Task FetchAsync_blocks_loopback_url_before_any_http_call()
    {
        var handler = new ScriptedHttpHandler();
        var service = NewService(handler);

        var result = await service.FetchAsync(NewCall(WebHostToolService.WebFetchToolName, new JsonObject
        {
            ["url"] = "http://localhost/admin"
        }), context: null);

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!;
        refusal["code"]!.GetValue<string>().Should().Be("url-private-host");
        refusal["axis"]!.GetValue<string>().Should().Be("web-policy");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task FetchAsync_blocks_when_resolved_ip_is_private()
    {
        var handler = new ScriptedHttpHandler();
        var service = NewService(
            handler,
            hostResolver: (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>(new[]
            {
                IPAddress.Parse("203.0.113.1"),
                IPAddress.Parse("10.0.0.5")
            }));

        var result = await service.FetchAsync(NewCall(WebHostToolService.WebFetchToolName, new JsonObject
        {
            ["url"] = "https://internal-disguise.example.com/"
        }), context: null);

        var refusal = JsonNode.Parse(result.Content)!["refusal"]!;
        refusal["code"]!.GetValue<string>().Should().Be("url-private-host");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task FetchAsync_blocks_redirect_into_private_network()
    {
        var handler = new ScriptedHttpHandler();
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data");
        handler.Enqueue(redirect);
        var service = NewService(handler);

        var result = await service.FetchAsync(NewCall(WebHostToolService.WebFetchToolName, new JsonObject
        {
            ["url"] = "https://docs.example.com/install"
        }), context: null);

        var refusal = JsonNode.Parse(result.Content)!["refusal"]!;
        refusal["code"]!.GetValue<string>().Should().Be("url-private-host");
        refusal["reason"]!.GetValue<string>().Should().Contain("Redirect");
    }

    [Fact]
    public async Task FetchAsync_refuses_when_redirect_chain_exceeds_max()
    {
        var handler = new ScriptedHttpHandler();
        for (var i = 0; i < 5; i++)
        {
            var hop = new HttpResponseMessage(HttpStatusCode.Found);
            hop.Headers.Location = new Uri($"https://docs.example.com/hop{i + 1}");
            handler.Enqueue(hop);
        }
        var options = new WebToolOptions { MaxRedirects = 2 };
        var service = NewService(handler, options);

        var result = await service.FetchAsync(NewCall(WebHostToolService.WebFetchToolName, new JsonObject
        {
            ["url"] = "https://docs.example.com/start"
        }), context: null);

        var refusal = JsonNode.Parse(result.Content)!["refusal"]!;
        refusal["code"]!.GetValue<string>().Should().Be("redirect-limit");
    }

    [Fact]
    public async Task FetchAsync_does_not_send_authorization_or_cookie_headers()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok", Encoding.UTF8, "text/plain")
        });
        var service = NewService(handler);

        await service.FetchAsync(NewCall(WebHostToolService.WebFetchToolName, new JsonObject
        {
            ["url"] = "https://docs.example.com/"
        }), context: null);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Authorization.Should().BeNull();
        handler.LastRequest.Headers.Contains("Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_returns_refusal_when_no_provider_configured()
    {
        var service = NewService(new ScriptedHttpHandler());

        var result = await service.SearchAsync(NewCall(WebHostToolService.WebSearchToolName, new JsonObject
        {
            ["query"] = "node 22 docker hub"
        }), context: null);

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("search-not-configured");
    }

    [Fact]
    public async Task SearchAsync_filters_provider_hits_through_url_policy()
    {
        var provider = new StubSearchProvider(new[]
        {
            new WebSearchHit("Public", "https://docs.example.com/install", "ok"),
            new WebSearchHit("Internal leak", "http://10.0.0.5/admin", "blocked")
        });
        var service = NewService(new ScriptedHttpHandler(), searchProvider: provider);

        var result = await service.SearchAsync(NewCall(WebHostToolService.WebSearchToolName, new JsonObject
        {
            ["query"] = "anything"
        }), context: null);

        result.IsError.Should().BeFalse();
        var hits = JsonNode.Parse(result.Content)!["hits"]!.AsArray();
        hits.Count.Should().Be(1);
        hits[0]!["url"]!.GetValue<string>().Should().Be("https://docs.example.com/install");
    }

    [Fact]
    public async Task SearchAsync_caps_results_to_max_search_results()
    {
        var hits = Enumerable.Range(1, 20)
            .Select(i => new WebSearchHit($"hit{i}", $"https://docs.example.com/{i}", null))
            .ToArray();
        var provider = new StubSearchProvider(hits);
        var options = new WebToolOptions { MaxSearchResults = 3 };
        var service = NewService(new ScriptedHttpHandler(), options, searchProvider: provider);

        var result = await service.SearchAsync(NewCall(WebHostToolService.WebSearchToolName, new JsonObject
        {
            ["query"] = "node",
            ["maxResults"] = 100
        }), context: null);

        JsonNode.Parse(result.Content)!["hits"]!.AsArray().Count.Should().Be(3);
    }

    [Fact]
    public async Task SearchAsync_refuses_blank_query()
    {
        var service = NewService(new ScriptedHttpHandler());

        var result = await service.SearchAsync(NewCall(WebHostToolService.WebSearchToolName, new JsonObject
        {
            ["query"] = "   "
        }), context: null);

        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("query-required");
    }

    private static WebHostToolService NewService(
        HttpMessageHandler handler,
        WebToolOptions? options = null,
        IWebSearchProvider? searchProvider = null,
        Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>>? hostResolver = null)
    {
        return new WebHostToolService(
            options ?? new WebToolOptions(),
            handler,
            searchProvider,
            hostResolver ?? ((_, _) => Task.FromResult<IReadOnlyList<IPAddress>>(new[] { IPAddress.Parse("203.0.113.1") })));
    }

    private static ToolCall NewCall(string name, JsonNode arguments) =>
        new("call_1", name, arguments);

    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new();

        public int RequestCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public void Enqueue(HttpResponseMessage response) => responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("ScriptedHttpHandler ran out of responses.");
            }

            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class StubSearchProvider : IWebSearchProvider
    {
        private readonly IReadOnlyList<WebSearchHit> hits;

        public StubSearchProvider(IReadOnlyList<WebSearchHit> hits) => this.hits = hits;

        public Task<WebSearchProviderResult> SearchAsync(string query, int maxResults, CancellationToken ct) =>
            Task.FromResult(WebSearchProviderResult.Success(hits));
    }
}
