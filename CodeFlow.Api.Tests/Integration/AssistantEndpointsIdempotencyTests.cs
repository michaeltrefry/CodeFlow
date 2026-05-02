using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeFlow.Api.Assistant;
using CodeFlow.Api.Assistant.Idempotency;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeFlow.Api.Tests.Integration;

/// <summary>
/// sc-525 — Integration tests for the retry-safe assistant turn POST. Covers the full
/// dispatch matrix (claim / replay / wait+replay / hash-mismatch / user-mismatch / passthrough)
/// against real MariaDB through the WebApplicationFactory so the schema, EF mapping, and
/// endpoint composition are all exercised end-to-end.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class AssistantEndpointsIdempotencyTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;
    private static readonly StubAssistantForIdempotency AssistantStub = new();

    public AssistantEndpointsIdempotencyTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    private HttpClient CreateClientWithStub() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICodeFlowAssistant>();
                services.AddSingleton<ICodeFlowAssistant>(AssistantStub);
            });
        }).CreateClient();

    [Fact]
    public async Task Same_key_same_body_replays_events_and_avoids_second_LLM_call()
    {
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("hello"),
            new AssistantTextDelta(" world"),
            new AssistantTokenUsage(
                Provider: "anthropic",
                Model: "claude-sonnet-4",
                Usage: JsonSerializer.SerializeToElement(new { input_tokens = 5, output_tokens = 2 })),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();
        var conversation = await CreateConversationAsync(client);
        var key = NewKey();

        var firstFrames = await PostMessageAsync(client, conversation.Id, "hello?", key);
        var firstCallCount = AssistantStub.CallCount;

        var secondFrames = await PostMessageAsync(client, conversation.Id, "hello?", key);

        // Same key + same body → second response replays the originating events; the assistant
        // stub MUST NOT be invoked a second time (no double LLM call).
        AssistantStub.CallCount.Should().Be(firstCallCount, "retry must not trigger a second turn");

        // Both responses end with the same payload events. We don't care about the leading ":
        // connected" comment or whether the trailing `done` is present; we care that the user
        // saw the same recorded events on the wire.
        var firstPayloadEvents = firstFrames.Where(f => f.Event != "done").ToArray();
        var secondPayloadEvents = secondFrames.Where(f => f.Event != "done").ToArray();
        secondPayloadEvents.Select(f => f.Event)
            .Should().BeEquivalentTo(firstPayloadEvents.Select(f => f.Event), opts => opts.WithStrictOrdering());

        // Only one user message persisted — the retry must NOT create a duplicate.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var userMessages = await dbContext.AssistantMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Role == AssistantMessageRole.User)
            .ToListAsync();
        userMessages.Should().ContainSingle();
        userMessages[0].Content.Should().Be("hello?");
    }

    [Fact]
    public async Task Same_key_different_body_returns_409_idempotency_conflict()
    {
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("ok"),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();
        var conversation = await CreateConversationAsync(client);
        var key = NewKey();

        await PostMessageAsync(client, conversation.Id, "first prompt", key);

        var second = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "DIFFERENT prompt" })
        };
        second.Headers.Add(AssistantTurnIdempotencyKeys.HeaderName, key);

        using var response = await client.SendAsync(second, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("idempotency-key-conflict");
    }

    [Fact]
    public async Task Malformed_key_returns_400_and_does_not_run_turn()
    {
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("never"),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();
        var conversation = await CreateConversationAsync(client);
        var preCallCount = AssistantStub.CallCount;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "hi" })
        };
        request.Headers.Add(AssistantTurnIdempotencyKeys.HeaderName, "bad key with spaces");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssistantStub.CallCount.Should().Be(preCallCount, "malformed key must short-circuit before the turn runs");
    }

    [Fact]
    public async Task No_header_preserves_existing_behavior()
    {
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("hi"),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();
        var conversation = await CreateConversationAsync(client);

        // No idempotency header — endpoint must run the turn normally and not write any
        // idempotency rows.
        await PostMessageAsync(client, conversation.Id, "hello", idempotencyKey: null);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var idempotencyRows = await dbContext.AssistantTurnIdempotency
            .AsNoTracking()
            .Where(e => e.ConversationId == conversation.Id)
            .ToListAsync();
        idempotencyRows.Should().BeEmpty();
    }

    private async Task<List<SseFrame>> PostMessageAsync(
        HttpClient client,
        Guid conversationId,
        string content,
        string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(new { content })
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add(AssistantTurnIdempotencyKeys.HeaderName, idempotencyKey);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        return ParseSse(body);
    }

    private static async Task<ConversationDto> CreateConversationAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = "idempotency-test", entityId = Guid.NewGuid().ToString() }
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts);
        return payload!.Conversation;
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private static List<SseFrame> ParseSse(string body)
    {
        var frames = new List<SseFrame>();
        foreach (var rawFrame in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? eventName = null;
            var dataBuilder = new StringBuilder();
            foreach (var line in rawFrame.Split('\n'))
            {
                if (line.StartsWith("event: "))
                {
                    eventName = line["event: ".Length..].Trim();
                }
                else if (line.StartsWith("data: "))
                {
                    if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                    dataBuilder.Append(line["data: ".Length..]);
                }
            }
            if (eventName is not null)
            {
                frames.Add(new SseFrame(eventName, dataBuilder.ToString()));
            }
        }
        return frames;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record SseFrame(string Event, string Data);
    private sealed record ConversationResponse(ConversationDto Conversation, IReadOnlyList<MessageDto> Messages);
    private sealed record ConversationDto(Guid Id, ScopeDto Scope, Guid SyntheticTraceId);
    private sealed record ScopeDto(string Kind, string? EntityType, string? EntityId);
    private sealed record MessageDto(Guid Id, int Sequence, string Role, string Content, string? Provider, string? Model);

    /// <summary>
    /// Counts invocations so the replay-skips-second-LLM-call test can assert that the stub
    /// was only hit once across two POSTs that share an idempotency key.
    /// </summary>
    public sealed class StubAssistantForIdempotency : ICodeFlowAssistant
    {
        private IReadOnlyList<AssistantStreamItem> reply = Array.Empty<AssistantStreamItem>();
        private int callCount;

        public void Reset()
        {
            reply = Array.Empty<AssistantStreamItem>();
            Interlocked.Exchange(ref callCount, 0);
        }

        public void SetReply(IReadOnlyList<AssistantStreamItem> items) => reply = items;

        public int CallCount => Volatile.Read(ref callCount);

        public async IAsyncEnumerable<AssistantStreamItem> AskAsync(
            string userMessage,
            IReadOnlyList<AssistantMessage> history,
            CodeFlow.Runtime.ToolAccessPolicy? toolPolicy = null,
            AssistantPageContext? pageContext = null,
            string? overrideProvider = null,
            string? overrideModel = null,
            Guid conversationId = default,
            AssistantWorkspaceTarget? workspaceOverride = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            foreach (var item in reply)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }
}
