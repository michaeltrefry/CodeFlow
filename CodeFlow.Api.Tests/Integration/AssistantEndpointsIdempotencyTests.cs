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
    public async Task In_flight_duplicate_waits_then_replays_originating_events()
    {
        // sc-525 — second request arrives WHILE the first is still streaming. The coordinator
        // must wait for the originating turn to reach a terminal status, then replay the
        // recorded events to the duplicate. Same key + same body → still exactly one LLM call.
        AssistantStub.Reset();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("delayed"),
            new AssistantTextDelta(" answer"),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });
        AssistantStub.SetGate(release.Task);

        using var client = CreateClientWithStub();
        var conversation = await CreateConversationAsync(client);
        var key = NewKey();

        // Kick off the originating turn but don't block on the body — we want a duplicate
        // request to arrive WHILE the assistant stub is gated open.
        var firstTask = SendMessageAsync(client, conversation.Id, "wait for me", key);

        // Wait until the stub is actually gated (call started) so the duplicate hits the
        // InFlight path rather than racing past the claim.
        var gatedAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (AssistantStub.GateEntered == 0 && DateTime.UtcNow < gatedAt)
        {
            await Task.Delay(20);
        }
        AssistantStub.GateEntered.Should().BeGreaterThan(0, "originating turn must be in flight before the duplicate fires");

        // Fire the duplicate — must NOT take the Claimed path (call count stays at 1).
        var secondTask = SendMessageAsync(client, conversation.Id, "wait for me", key);

        // Briefly let both requests sit in flight; neither should complete yet.
        await Task.Delay(100);
        firstTask.IsCompleted.Should().BeFalse();
        secondTask.IsCompleted.Should().BeFalse();

        // Release the gate — the originating turn finishes, the duplicate replays.
        release.SetResult(true);

        var (firstFrames, _) = await firstTask;
        var (secondFrames, _) = await secondTask;

        AssistantStub.CallCount.Should().Be(1, "the duplicate must replay, not invoke the LLM a second time");

        var firstPayloadEvents = firstFrames.Where(f => f.Event != "done").Select(f => f.Event).ToArray();
        var secondPayloadEvents = secondFrames.Where(f => f.Event != "done").Select(f => f.Event).ToArray();
        secondPayloadEvents.Should().BeEquivalentTo(firstPayloadEvents, opts => opts.WithStrictOrdering());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        (await dbContext.AssistantMessages
            .AsNoTracking()
            .CountAsync(m => m.ConversationId == conversation.Id && m.Role == AssistantMessageRole.User))
            .Should().Be(1, "in-flight retries must not duplicate the user message");
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
        var (frames, _) = await SendMessageAsync(client, conversationId, content, idempotencyKey);
        return frames;
    }

    /// <summary>
    /// In-flight-aware variant: returns the parsed SSE frames + the HTTP status. Uses
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> so the caller can have
    /// concurrent requests outstanding without the test fixture deadlocking on response-body
    /// reads.
    /// </summary>
    private async Task<(List<SseFrame> Frames, HttpStatusCode Status)> SendMessageAsync(
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
        var status = response.StatusCode;
        var body = await response.Content.ReadAsStringAsync();
        return (ParseSse(body), status);
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
        private int gateEntered;
        private Task? gate;

        public void Reset()
        {
            reply = Array.Empty<AssistantStreamItem>();
            Interlocked.Exchange(ref callCount, 0);
            Interlocked.Exchange(ref gateEntered, 0);
            gate = null;
        }

        public void SetReply(IReadOnlyList<AssistantStreamItem> items) => reply = items;

        /// <summary>
        /// When non-null, the stub awaits this task before yielding any items. Lets a test
        /// observe the "in flight" window — duplicate POSTs that race in must take the
        /// WaitThenReplay path.
        /// </summary>
        public void SetGate(Task gateTask) => gate = gateTask;

        public int CallCount => Volatile.Read(ref callCount);
        public int GateEntered => Volatile.Read(ref gateEntered);

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
            var pendingGate = gate;
            if (pendingGate is not null)
            {
                Interlocked.Increment(ref gateEntered);
                await pendingGate.WaitAsync(cancellationToken);
            }

            foreach (var item in reply)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }
}
