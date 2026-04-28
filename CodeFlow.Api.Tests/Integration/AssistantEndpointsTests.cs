using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeFlow.Api.Assistant;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeFlow.Api.Tests.Integration;

/// <summary>
/// HAA-1 — Assistant backend service. Verifies the chat-loop service stands up end-to-end:
/// - get-or-create conversation by user-scoped key (homepage / entity)
/// - per-user ownership enforcement on read
/// - SSE streaming of a chat turn with deltas + token usage + persistence
/// - history reload on subsequent reads
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class AssistantEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;
    private static readonly StubAssistant AssistantStub = new();

    public AssistantEndpointsTests(CodeFlowApiFactory factory)
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
    public async Task PostConversation_HomepageScope_ReturnsStableConversationOnRepeatedCalls()
    {
        using var client = CreateClientWithStub();

        var first = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "homepage" }
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstPayload = await first.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts);
        firstPayload.Should().NotBeNull();
        firstPayload!.Conversation.Id.Should().NotBeEmpty();
        firstPayload.Conversation.SyntheticTraceId.Should().NotBeEmpty();
        firstPayload.Conversation.Scope.Kind.Should().Be("homepage");

        var second = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "homepage" }
        });
        var secondPayload = await second.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts);
        secondPayload!.Conversation.Id.Should().Be(firstPayload.Conversation.Id);
    }

    [Fact]
    public async Task PostConversation_EntityScope_DistinctFromHomepageAndOtherEntities()
    {
        using var client = CreateClientWithStub();

        var homepage = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "homepage" }
        });
        var entityA = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = "trace", entityId = "trace-A" }
        });
        var entityB = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = "trace", entityId = "trace-B" }
        });

        var homepageId = (await homepage.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation.Id;
        var entityAId = (await entityA.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation.Id;
        var entityBId = (await entityB.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation.Id;

        new[] { homepageId, entityAId, entityBId }.Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task PostMessage_StreamsDeltas_PersistsBothMessages_RecordsTokenUsage()
    {
        AssistantStub.Reset();
        AssistantStub.SetReply(
            new[]
            {
                (AssistantStreamItem)new AssistantTextDelta("Hello, "),
                new AssistantTextDelta("world!"),
                new AssistantTokenUsage(
                    Provider: "anthropic",
                    Model: "claude-sonnet-4",
                    Usage: JsonSerializer.SerializeToElement(new
                    {
                        input_tokens = 12,
                        output_tokens = 7
                    })),
                new AssistantTurnDone("anthropic", "claude-sonnet-4")
            });

        using var client = CreateClientWithStub();
        var conversationResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "homepage" }
        });
        var conversation = (await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var streamRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "What's up?" })
        };
        using var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        streamResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        streamResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        var body = await streamResponse.Content.ReadAsStringAsync();
        var events = ParseSse(body);
        events.Select(e => e.Event).Should().BeEquivalentTo(new[]
        {
            "user-message-persisted",
            "text-delta",
            "text-delta",
            "token-usage",
            "assistant-message-persisted",
            "done"
        }, opts => opts.WithStrictOrdering());

        var assistantPersisted = events.Single(e => e.Event == "assistant-message-persisted");
        var assistantMsgPayload = JsonSerializer.Deserialize<JsonElement>(assistantPersisted.Data);
        assistantMsgPayload.GetProperty("content").GetString().Should().Be("Hello, world!");
        assistantMsgPayload.GetProperty("provider").GetString().Should().Be("anthropic");
        assistantMsgPayload.GetProperty("model").GetString().Should().Be("claude-sonnet-4");

        // History reload: a fresh GET surfaces both turns in sequence order.
        var fetched = await client.GetAsync($"/api/assistant/conversations/{conversation.Id}");
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetchedPayload = await fetched.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts);
        fetchedPayload!.Messages.Should().HaveCount(2);
        fetchedPayload.Messages[0].Role.Should().Be("user");
        fetchedPayload.Messages[0].Content.Should().Be("What's up?");
        fetchedPayload.Messages[1].Role.Should().Be("assistant");
        fetchedPayload.Messages[1].Content.Should().Be("Hello, world!");

        // Token usage record landed against the conversation's synthetic trace.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var records = await dbContext.TokenUsageRecords
            .AsNoTracking()
            .Where(r => r.TraceId == conversation.SyntheticTraceId)
            .ToListAsync();
        records.Should().ContainSingle();
        var record = records.Single();
        record.NodeId.Should().Be(conversation.Id);
        record.Provider.Should().Be("anthropic");
        record.Model.Should().Be("claude-sonnet-4");
    }

    [Fact]
    public async Task GetConversation_NonExistentId_Returns404()
    {
        using var client = CreateClientWithStub();
        var response = await client.GetAsync($"/api/assistant/conversations/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostMessage_ToolLoop_StreamsToolCallAndResultEvents()
    {
        AssistantStub.Reset();
        var args = JsonSerializer.SerializeToElement(new { key = "alpha" });
        AssistantStub.SetReply(
            new[]
            {
                (AssistantStreamItem)new AssistantTextDelta("Looking it up. "),
                new AssistantToolCallStarted("call_1", "get_workflow", args),
                new AssistantToolCallCompleted("call_1", "get_workflow", "{\"key\":\"alpha\",\"version\":1}", IsError: false),
                new AssistantTextDelta("Found alpha v1."),
                new AssistantTokenUsage(
                    Provider: "anthropic",
                    Model: "claude-sonnet-4",
                    Usage: JsonSerializer.SerializeToElement(new { input_tokens = 50, output_tokens = 12 })),
                new AssistantTurnDone("anthropic", "claude-sonnet-4")
            });

        using var client = CreateClientWithStub();
        // Use a unique entity-scoped conversation so we don't share state with the homepage
        // conversation other tests in this fixture create.
        var conversationResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = "tool-loop-test", entityId = Guid.NewGuid().ToString() }
        });
        var conversation = (await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var streamRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "Show me alpha." })
        };
        using var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        streamResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await streamResponse.Content.ReadAsStringAsync();
        var events = ParseSse(body);
        events.Select(e => e.Event).Should().BeEquivalentTo(new[]
        {
            "user-message-persisted",
            "text-delta",
            "tool-call",
            "tool-result",
            "text-delta",
            "token-usage",
            "assistant-message-persisted",
            "done"
        }, opts => opts.WithStrictOrdering());

        var toolCall = events.Single(e => e.Event == "tool-call");
        var toolCallPayload = JsonSerializer.Deserialize<JsonElement>(toolCall.Data);
        toolCallPayload.GetProperty("id").GetString().Should().Be("call_1");
        toolCallPayload.GetProperty("name").GetString().Should().Be("get_workflow");
        toolCallPayload.GetProperty("arguments").GetProperty("key").GetString().Should().Be("alpha");

        var toolResult = events.Single(e => e.Event == "tool-result");
        var toolResultPayload = JsonSerializer.Deserialize<JsonElement>(toolResult.Data);
        toolResultPayload.GetProperty("id").GetString().Should().Be("call_1");
        toolResultPayload.GetProperty("isError").GetBoolean().Should().BeFalse();
        toolResultPayload.GetProperty("result").GetString().Should().Contain("alpha");

        // Final assistant message persists only the text content (tool call/result events are
        // transient; only AssistantTextDelta items contribute to contentBuffer).
        var persisted = events.Single(e => e.Event == "assistant-message-persisted");
        var persistedPayload = JsonSerializer.Deserialize<JsonElement>(persisted.Data);
        persistedPayload.GetProperty("content").GetString().Should().Be("Looking it up. Found alpha v1.");
    }

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

    public sealed class StubAssistant : ICodeFlowAssistant
    {
        private IReadOnlyList<AssistantStreamItem> reply = Array.Empty<AssistantStreamItem>();

        public void Reset() => reply = Array.Empty<AssistantStreamItem>();

        public void SetReply(IReadOnlyList<AssistantStreamItem> items) => reply = items;

        public async IAsyncEnumerable<AssistantStreamItem> AskAsync(
            string userMessage,
            IReadOnlyList<AssistantMessage> history,
            CancellationToken cancellationToken = default)
        {
            foreach (var item in reply)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }
}
