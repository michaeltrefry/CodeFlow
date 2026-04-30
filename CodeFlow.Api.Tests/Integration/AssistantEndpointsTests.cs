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
    public async Task ListConversations_ReturnsCallerOwnConversationsNewestFirst_WithMessagePreviews()
    {
        // HAA-14: the homepage rail's resume-conversation list calls GET /api/assistant/conversations
        // and expects (a) only the caller's threads, (b) ordered by UpdatedAtUtc DESC, and (c) a
        // server-truncated preview of the first user message.
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("ack"),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();

        // Both conversations are entity-scoped to UNIQUE types so they never collide with the
        // shared homepage thread that other tests in this fixture (and the dev-bypass user)
        // accumulate messages on. Order: A first, B second, so B becomes most-recently-updated.
        var entityTypeA = $"haa14-list-a-{Guid.NewGuid():N}";
        var entityTypeB = $"haa14-list-b-{Guid.NewGuid():N}";

        var convAResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = entityTypeA, entityId = "1" }
        });
        var convA = (await convAResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var aMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{convA.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "How does the swarm node work?" })
        };
        using (var aStream = await client.SendAsync(aMessage, HttpCompletionOption.ResponseHeadersRead))
        {
            await aStream.Content.ReadAsStringAsync();
        }

        var convBResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = entityTypeB, entityId = "1" }
        });
        var convB = (await convBResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var bMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{convB.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "Why did this trace fail?" })
        };
        using (var bStream = await client.SendAsync(bMessage, HttpCompletionOption.ResponseHeadersRead))
        {
            await bStream.Content.ReadAsStringAsync();
        }

        // Widen the list call so both seeded conversations land in the response even if the
        // shared user has accumulated many threads from other tests in this fixture.
        var listResponse = await client.GetAsync("/api/assistant/conversations?limit=100");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listPayload = await listResponse.Content.ReadFromJsonAsync<ConversationListResponse>(JsonOpts);
        listPayload!.Conversations.Should().NotBeEmpty();

        var byId = listPayload.Conversations.ToDictionary(c => c.Id);
        byId.Should().ContainKey(convA.Id);
        byId.Should().ContainKey(convB.Id);

        // Filter to just our two so other tests' contributions don't perturb relative-order
        // assertions. Order should be B (newer) then A.
        var ourConversations = listPayload.Conversations
            .Where(c => c.Id == convA.Id || c.Id == convB.Id)
            .ToList();
        ourConversations.Select(c => c.Id).Should().BeEquivalentTo(
            new[] { convB.Id, convA.Id }, opts => opts.WithStrictOrdering());

        byId[convA.Id].FirstUserMessagePreview.Should().Contain("swarm node");
        byId[convA.Id].MessageCount.Should().BeGreaterThanOrEqualTo(2); // user + assistant turns
        byId[convA.Id].Scope.Kind.Should().Be("entity");
        byId[convA.Id].Scope.EntityType.Should().Be(entityTypeA);

        byId[convB.Id].FirstUserMessagePreview.Should().Contain("Why did this trace fail");
        byId[convB.Id].Scope.Kind.Should().Be("entity");
        byId[convB.Id].Scope.EntityType.Should().Be(entityTypeB);
    }

    [Fact]
    public async Task GetTokenUsageSummary_AggregatesAcrossUserConversations_ScopesTodayAndAllTime()
    {
        // HAA-14: the homepage rail's assistant-token chip calls GET /api/assistant/token-usage/summary
        // and expects today + all-time rollups across every synthetic-trace the user owns. Smoke-
        // test by sending one chat turn (which captures a token-usage record) and verifying the
        // summary endpoint reports the same numbers.
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("ok"),
            new AssistantTokenUsage(
                Provider: "anthropic",
                Model: "claude-sonnet-4",
                Usage: JsonSerializer.SerializeToElement(new
                {
                    input_tokens = 17,
                    output_tokens = 9
                })),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();

        var convResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = "token-summary-test", entityId = Guid.NewGuid().ToString() }
        });
        var conversation = (await convResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "ping" })
        };
        using (var stream = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead))
        {
            await stream.Content.ReadAsStringAsync();
        }

        var summaryResponse = await client.GetAsync("/api/assistant/token-usage/summary");
        summaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await summaryResponse.Content.ReadFromJsonAsync<TokenUsageSummaryResponse>(JsonOpts);
        summary!.AllTime.CallCount.Should().BeGreaterThanOrEqualTo(1);
        summary.AllTime.Totals["input_tokens"].Should().BeGreaterThanOrEqualTo(17);
        summary.AllTime.Totals["output_tokens"].Should().BeGreaterThanOrEqualTo(9);

        // The just-recorded turn happened "today" (calendar UTC). Other tests in the fixture may
        // also contribute to today's bucket — we assert >= rather than ==.
        summary.Today.CallCount.Should().BeGreaterThanOrEqualTo(1);

        // The seeded conversation must appear in the per-conversation breakdown with at least
        // its own token totals. Conversations with zero records are filtered server-side.
        var seeded = summary.PerConversation.SingleOrDefault(p => p.ConversationId == conversation.Id);
        seeded.Should().NotBeNull(because: "conversation has at least one captured token-usage record");
        seeded!.SyntheticTraceId.Should().Be(conversation.SyntheticTraceId);
        seeded.Rollup.CallCount.Should().BeGreaterThanOrEqualTo(1);
        seeded.Scope.Kind.Should().Be("entity");
        seeded.Scope.EntityType.Should().Be("token-summary-test");
    }

    [Fact]
    public async Task GetConversation_NonExistentId_Returns404()
    {
        using var client = CreateClientWithStub();
        var response = await client.GetAsync($"/api/assistant/conversations/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostNewConversation_PreservesOldThread_AndMakesSameScopeLoadNewest()
    {
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("ok"),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();
        var conversationResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = "new-chat-test", entityId = Guid.NewGuid().ToString() }
        });
        var conversation = (await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var streamRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "hello" })
        };
        using (var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead))
        {
            await streamResponse.Content.ReadAsStringAsync();
        }

        var replacementResponse = await client.PostAsJsonAsync("/api/assistant/conversations/new", new
        {
            scope = new
            {
                kind = "entity",
                entityType = conversation.Scope.EntityType,
                entityId = conversation.Scope.EntityId
            }
        });
        replacementResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var replacement = await replacementResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts);
        replacement!.Conversation.Id.Should().NotBe(conversation.Id);
        replacement.Messages.Should().BeEmpty();
        replacement.Conversation.Scope.EntityType.Should().Be(conversation.Scope.EntityType);
        replacement.Conversation.Scope.EntityId.Should().Be(conversation.Scope.EntityId);

        var oldFetch = await client.GetAsync($"/api/assistant/conversations/{conversation.Id}");
        oldFetch.StatusCode.Should().Be(HttpStatusCode.OK);
        var oldPayload = await oldFetch.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts);
        oldPayload!.Messages.Should().NotBeEmpty();

        var latestResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new
            {
                kind = "entity",
                entityType = conversation.Scope.EntityType,
                entityId = conversation.Scope.EntityId
            }
        });
        var latest = await latestResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts);
        latest!.Conversation.Id.Should().Be(replacement.Conversation.Id);
    }

    [Fact]
    public async Task PostMessage_WithPageContext_FlowsPageContextToAssistant()
    {
        // HAA-8: clients send a per-turn PageContext snapshot describing what the user is
        // currently looking at. The chat pipeline must hand it down to ICodeFlowAssistant so the
        // model can see a structured context block alongside the system prompt.
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("ok"),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();
        var conversationResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = "page-context-test", entityId = Guid.NewGuid().ToString() }
        });
        var conversation = (await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var streamRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new
            {
                content = "why did this fail?",
                pageContext = new
                {
                    kind = "trace",
                    route = "/traces/abc",
                    entityType = "trace",
                    entityId = "abc",
                    selectedNodeId = "node-7"
                }
            })
        };
        using var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        await streamResponse.Content.ReadAsStringAsync();

        AssistantStub.LastPageContext.Should().NotBeNull();
        AssistantStub.LastPageContext!.Kind.Should().Be("trace");
        AssistantStub.LastPageContext.EntityId.Should().Be("abc");
        AssistantStub.LastPageContext.SelectedNodeId.Should().Be("node-7");

        var formatted = AssistantPageContextFormatter.FormatAsSystemMessage(AssistantStub.LastPageContext);
        formatted.Should().NotBeNull();
        formatted.Should().Contain("Kind: trace")
            .And.Contain("Entity: trace=abc")
            .And.Contain("Selected node: node-7");
    }

    [Fact]
    public async Task PostMessage_WithoutPageContext_NullsOutAtAssistant()
    {
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("ok"),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();
        var conversationResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = "no-context-test", entityId = Guid.NewGuid().ToString() }
        });
        var conversation = (await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var streamRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "hi" })
        };
        using var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        await streamResponse.Content.ReadAsStringAsync();

        AssistantStub.LastPageContext.Should().BeNull(
            because: "omitting pageContext on the request must reach the assistant as null, not an empty record");
    }

    [Fact]
    public async Task PostMessage_AuthenticatedUser_PassesNullPolicyToAssistant()
    {
        // HAA-6: authenticated callers run with full tool access. The chat service should pass
        // null (= AllowAll) to the assistant, NOT NoTools. Demo mode is exclusively the anon path
        // and is exercised in CodeFlow.Api.Tests.Assistant.AssistantUserResolverTests + the
        // AssistantChatService unit tests; here we just guard the wiring on the auth path.
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("ok"),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        using var client = CreateClientWithStub();
        // Unique entity-scoped conversation so we don't share state with the homepage conversation
        // other tests in this fixture create.
        var conversationResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType = "policy-test", entityId = Guid.NewGuid().ToString() }
        });
        var conversation = (await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var streamRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "hello" })
        };
        using var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        await streamResponse.Content.ReadAsStringAsync();

        AssistantStub.LastToolPolicy.Should().BeNull(
            because: "authenticated callers must run with full tool access (null policy = AllowAll)");
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

    /// <summary>
    /// sc-274 phase 2 — assistant ambiguity preflight short-circuits the SSE stream and
    /// returns a 422 with structured clarification questions when the user message looks
    /// like a vague action request. The refusal is also persisted to the append-only
    /// <c>refusal_events</c> stream against the conversation id (not a trace id) so
    /// governance + bundle export both see assistant preflight refusals as first-class
    /// evidence.
    /// </summary>
    [Fact]
    public async Task PostMessage_VagueActionVerb_PreflightRefusesWith422_AndPersistsRefusal()
    {
        AssistantStub.Reset();

        // Unique entity-scoped conversation so the assertion that no message was persisted is
        // not perturbed by other tests in this fixture sharing the dev-bypass user's homepage
        // conversation.
        var entityType = $"sc274-preflight-refuse-{Guid.NewGuid():N}";
        using var client = CreateClientWithStub();
        var conversationResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType, entityId = "1" }
        });
        var conversation = (await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var streamRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            // Action verb + pronoun + ≤2 words + no scope noun → vague-action heuristic fires;
            // pronoun-without-context heuristic piggy-backs (no page context provided).
            Content = JsonContent.Create(new { content = "fix it" })
        };
        using var response = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        // SSE never opened — must be application/json so the client can parse the body.
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var payload = await response.Content.ReadFromJsonAsync<AssistantPreflightRefusalPayload>(JsonOpts);
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("assistant-preflight-ambiguous");
        payload.Mode.Should().Be("AssistantChat");
        payload.ConversationId.Should().Be(conversation.Id);
        payload.OverallScore.Should().BeLessThan(payload.Threshold);
        payload.MissingFields.Should().Contain("content.scope");
        payload.ClarificationQuestions.Should().NotBeEmpty()
            .And.Contain(q => q.StartsWith("What specifically should I"));

        // Preflight refused before the chat service ran — no user message persisted, no
        // assistant message persisted, no token usage rows for this conversation.
        var fetched = await client.GetAsync($"/api/assistant/conversations/{conversation.Id}");
        var fetchedPayload = await fetched.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts);
        fetchedPayload!.Messages.Should().BeEmpty(
            "preflight refused before AssistantChatService was invoked, so no message was persisted");

        // Refusal must land in the append-only stream against the conversation id (not a trace).
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var refusal = await db.RefusalEvents.SingleAsync(r => r.AssistantConversationId == conversation.Id);
        refusal.Stage.Should().Be("preflight");
        refusal.Code.Should().Be("assistant-preflight-ambiguous");
        refusal.TraceId.Should().BeNull();
        refusal.DetailJson.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// sc-274 phase 2 — a well-scoped prompt passes preflight cleanly; the SSE stream opens
    /// and the chat service runs as normal. Guards against the assessor over-refusing
    /// realistic action requests with concrete targets.
    /// </summary>
    [Fact]
    public async Task PostMessage_WellScopedActionPrompt_PassesPreflight_AndStreams()
    {
        AssistantStub.Reset();
        AssistantStub.SetReply(new[]
        {
            (AssistantStreamItem)new AssistantTextDelta("Working on it."),
            new AssistantTurnDone("anthropic", "claude-sonnet-4")
        });

        // Unique entity-scoped conversation so the refusal-count assertion is not perturbed
        // by other tests in this fixture sharing a conversation.
        var entityType = $"sc274-preflight-pass-{Guid.NewGuid():N}";
        using var client = CreateClientWithStub();
        var conversationResponse = await client.PostAsJsonAsync("/api/assistant/conversations", new
        {
            scope = new { kind = "entity", entityType, entityId = "1" }
        });
        var conversation = (await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOpts))!.Conversation;

        var streamRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversation.Id}/messages")
        {
            // Concrete file + concept noun ("AssistantChatService") — vague-action heuristic
            // never fires because ContainsScopeNoun matches.
            Content = JsonContent.Create(new { content = "update the AssistantChatService to log token usage" })
        };
        using var response = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        var events = ParseSse(body);
        events.Select(e => e.Event).Should().Contain("user-message-persisted");
        events.Select(e => e.Event).Should().Contain("text-delta");

        // No preflight refusal landed for this conversation.
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var refusalCount = await db.RefusalEvents.CountAsync(r => r.AssistantConversationId == conversation.Id);
        refusalCount.Should().Be(0);
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

    // HAA-14 test DTOs.
    private sealed record ConversationListResponse(IReadOnlyList<ConversationSummaryDto> Conversations);
    private sealed record ConversationSummaryDto(
        Guid Id,
        ScopeDto Scope,
        Guid SyntheticTraceId,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        int MessageCount,
        string? FirstUserMessagePreview);

    private sealed record TokenUsageSummaryResponse(
        TokenRollupDto Today,
        TokenRollupDto AllTime,
        IReadOnlyList<PerConversationTokenDto> PerConversation);

    private sealed record TokenRollupDto(
        int CallCount,
        Dictionary<string, long> Totals,
        IReadOnlyList<JsonElement> ByProviderModel);

    private sealed record PerConversationTokenDto(
        Guid ConversationId,
        Guid SyntheticTraceId,
        ScopeDto Scope,
        TokenRollupDto Rollup);

    // sc-274 phase 2 — assistant preflight refusal payload mirroring AssistantPreflightRefusalResponse.
    private sealed record AssistantPreflightRefusalPayload(
        Guid ConversationId,
        string Code,
        string Mode,
        double OverallScore,
        double Threshold,
        IReadOnlyList<PreflightDimensionPayload> Dimensions,
        IReadOnlyList<string> MissingFields,
        IReadOnlyList<string> ClarificationQuestions);

    private sealed record PreflightDimensionPayload(string Dimension, double Score, string? Reason);

    public sealed class StubAssistant : ICodeFlowAssistant
    {
        private IReadOnlyList<AssistantStreamItem> reply = Array.Empty<AssistantStreamItem>();

        public void Reset()
        {
            reply = Array.Empty<AssistantStreamItem>();
            LastToolPolicy = null;
            LastPageContext = null;
        }

        public void SetReply(IReadOnlyList<AssistantStreamItem> items) => reply = items;

        /// <summary>
        /// Captures the policy passed to the most recent <see cref="AskAsync"/> call so demo-mode
        /// tests can assert that anonymous homepage conversations resolve to <c>NoTools</c>.
        /// </summary>
        public CodeFlow.Runtime.ToolAccessPolicy? LastToolPolicy { get; private set; }

        /// <summary>
        /// Captures the page context passed on the most recent <see cref="AskAsync"/> call so
        /// HAA-8 tests can assert the client-supplied context flowed end-to-end.
        /// </summary>
        public AssistantPageContext? LastPageContext { get; private set; }

        /// <summary>HAA-16 — captures the per-call provider override so tests can assert plumbing.</summary>
        public string? LastOverrideProvider { get; private set; }

        /// <summary>HAA-16 — captures the per-call model override so tests can assert plumbing.</summary>
        public string? LastOverrideModel { get; private set; }

        /// <summary>HAA-18 — captures the conversation id passed to AskAsync so role-tool tests can assert plumbing.</summary>
        public Guid LastConversationId { get; private set; }

        /// <summary>HAA-19 — captures the per-turn workspace override so workspace-switch tests can assert plumbing.</summary>
        public AssistantWorkspaceTarget? LastWorkspaceOverride { get; private set; }

        public async IAsyncEnumerable<AssistantStreamItem> AskAsync(
            string userMessage,
            IReadOnlyList<AssistantMessage> history,
            CodeFlow.Runtime.ToolAccessPolicy? toolPolicy = null,
            AssistantPageContext? pageContext = null,
            string? overrideProvider = null,
            string? overrideModel = null,
            Guid conversationId = default,
            AssistantWorkspaceTarget? workspaceOverride = null,
            CancellationToken cancellationToken = default)
        {
            LastToolPolicy = toolPolicy;
            LastPageContext = pageContext;
            LastOverrideProvider = overrideProvider;
            LastOverrideModel = overrideModel;
            LastConversationId = conversationId;
            LastWorkspaceOverride = workspaceOverride;
            foreach (var item in reply)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }
}
