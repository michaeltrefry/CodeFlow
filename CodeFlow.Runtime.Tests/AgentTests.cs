using FluentAssertions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Tests;

public sealed class AgentTests
{
    [Fact]
    public async Task InvokeAsync_ShouldRunASingleAgentInvocation()
    {
        var modelClient = new DelegatingModelClient(request =>
        {
            request.Model.Should().Be("alpha-model");
            request.Messages.Should().HaveCount(2);
            request.Messages[0].Role.Should().Be(ChatMessageRole.System);
            request.Messages[0].Content.Should().Be("You are concise.");
            request.Messages[1].Role.Should().Be(ChatMessageRole.User);
            request.Messages[1].Content.Should().Be("Summarize kickoff for runtime.");

            return Task.FromResult(new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "Runtime kickoff summarized."),
                InvocationStopReason.EndTurn,
                new TokenUsage(8, 4, 12)));
        });
        var agent = new Agent(new ModelClientRegistry(
        [
            new ModelClientRegistration("test", modelClient)
        ]));

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "test",
                Model: "alpha-model",
                SystemPrompt: "You are concise."),
            "Summarize kickoff for runtime.",
            ResolvedAgentTools.Empty);

        result.Output.Should().Be("Runtime kickoff summarized.");
        result.Decision.PortName.Should().Be("Completed");
        result.TokenUsage.Should().BeEquivalentTo(new TokenUsage(8, 4, 12));
    }

    [Fact]
    public async Task InvokeAsync_ShouldRoundTripHostToolCallsThroughTheLoop()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Checking local time.",
                    ToolCalls:
                    [
                        new ToolCall("call_now", "now", new JsonObject())
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                var toolMessage = request.Messages[^1];
                toolMessage.Role.Should().Be(ChatMessageRole.Tool);
                toolMessage.ToolCallId.Should().Be("call_now");
                toolMessage.Content.Should().Be("2026-04-20T10:40:00.0000000+00:00");

                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "The current UTC timestamp is captured."),
                    InvocationStopReason.EndTurn);
            }
        ]);
        var agent = new Agent(
            new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]),
            hostToolProvider: new HostToolProvider(static () => DateTimeOffset.Parse("2026-04-20T10:40:00Z")));

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "test",
                Model: "tool-model"),
            "What time is it?",
            ResolvedAgentTools.Empty with { EnableHostTools = true });

        result.Output.Should().Be("The current UTC timestamp is captured.");
        result.Decision.PortName.Should().Be("Completed");
        result.ToolCallsExecuted.Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_ShouldFanOutSubAgentCallsInParallel()
    {
        var parentClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Delegating to child agents.",
                    ToolCalls:
                    [
                        new ToolCall(
                            "call_spawn",
                            "spawn_subagent",
                            new JsonObject
                            {
                                ["invocations"] = new JsonArray
                                {
                                    new JsonObject { ["agent"] = "alpha", ["input"] = "draft A" },
                                    new JsonObject { ["agent"] = "beta", ["input"] = "draft B" },
                                    new JsonObject { ["agent"] = "gamma", ["input"] = "draft C" }
                                }
                            })
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                var toolMessage = request.Messages[^1];
                toolMessage.Role.Should().Be(ChatMessageRole.Tool);

                var payload = JsonNode.Parse(toolMessage.Content)!.AsArray();
                payload.Should().HaveCount(3);
                payload.Select(item => item!["agent"]!.GetValue<string>())
                    .Should().BeEquivalentTo(["alpha", "beta", "gamma"]);

                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "Children finished."),
                    InvocationStopReason.EndTurn);
            }
        ]);
        var childClient = new ConcurrentChildModelClient(expectedConcurrency: 3);
        var agent = new Agent(new ModelClientRegistry(
        [
            new ModelClientRegistration("parent", parentClient),
            new ModelClientRegistration("child", childClient)
        ]));

        var childConfig = new AgentInvocationConfiguration(
            Provider: "child",
            Model: "child-model");

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "parent",
                Model: "parent-model",
                SubAgents: new Dictionary<string, AgentInvocationConfiguration>
                {
                    ["alpha"] = childConfig,
                    ["beta"] = childConfig,
                    ["gamma"] = childConfig
                }),
            "Coordinate the reviewers.",
            ResolvedAgentTools.Empty);

        result.Output.Should().Be("Children finished.");
        result.Decision.PortName.Should().Be("Completed");
        childClient.MaxObservedConcurrency.Should().Be(3);
    }

    [Fact]
    public async Task InvokeAsync_ShouldDispatchConfiguredMcpTools()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Querying MCP.",
                    ToolCalls:
                    [
                        new ToolCall(
                            "call_mcp",
                            "mcp:docs:search",
                            new JsonObject { ["query"] = "InvocationLoop" })
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                var toolMessage = request.Messages[^1];
                toolMessage.Content.Should().Be("{\"hits\":2}");

                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "MCP query complete."),
                    InvocationStopReason.EndTurn);
            }
        ]);
        var mcpClient = new FakeMcpClient();
        var agent = new Agent(
            new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]),
            mcpClient: mcpClient);

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "test",
                Model: "mcp-model"),
            "Search docs.",
            new ResolvedAgentTools(
                AllowedToolNames: new[] { "mcp:docs:search" },
                McpTools: new[]
                {
                    new McpToolDefinition(
                        "docs",
                        "search",
                        "Search project docs.",
                        new JsonObject
                        {
                            ["type"] = "object"
                        })
                },
                EnableHostTools: false));

        result.Output.Should().Be("MCP query complete.");
        mcpClient.Invocations.Should().ContainSingle();
        mcpClient.Invocations[0].Server.Should().Be("docs");
        mcpClient.Invocations[0].ToolName.Should().Be("search");
        mcpClient.Invocations[0].Arguments!["query"]!.GetValue<string>().Should().Be("InvocationLoop");
    }

    private sealed class DelegatingModelClient(Func<InvocationRequest, Task<InvocationResponse>> handler) : IModelClient
    {
        public Task<InvocationResponse> InvokeAsync(
            InvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            return handler(request);
        }
    }

    private sealed class ScriptedModelClient(IReadOnlyList<Func<InvocationRequest, InvocationResponse>> steps) : IModelClient
    {
        private int nextStepIndex;

        public Task<InvocationResponse> InvokeAsync(
            InvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (nextStepIndex >= steps.Count)
            {
                throw new InvalidOperationException("No scripted model response remains.");
            }

            return Task.FromResult(steps[nextStepIndex++](request));
        }
    }

    private sealed class ConcurrentChildModelClient(int expectedConcurrency) : IModelClient
    {
        private readonly TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int currentConcurrency;
        private int maxObservedConcurrency;
        private int startedCount;

        public int MaxObservedConcurrency => maxObservedConcurrency;

        public async Task<InvocationResponse> InvokeAsync(
            InvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref currentConcurrency);
            UpdateMaxConcurrency(current);

            var started = Interlocked.Increment(ref startedCount);
            if (started >= expectedConcurrency)
            {
                allStarted.TrySetResult();
            }

            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);

            try
            {
                var input = request.Messages[^1].Content;
                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, $"done:{input}"),
                    InvocationStopReason.EndTurn);
            }
            finally
            {
                Interlocked.Decrement(ref currentConcurrency);
            }
        }

        private void UpdateMaxConcurrency(int current)
        {
            while (true)
            {
                var observed = MaxObservedConcurrency;
                if (current <= observed)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref maxObservedConcurrency, current, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    [Fact]
    public async Task InvokeAsync_PrependsConfiguredHistoryFromJsonRoundTrip()
    {
        // Stored agent configs round-trip through JsonSerializerDefaults.Web; the test confirms
        // (a) string-form ChatMessageRole values deserialize via the JsonStringEnumConverter
        // annotation on the enum, and (b) the history is prepended between the system block and
        // the latest user input on the way through ContextAssembler.
        const string configJson = """
        {
          "provider": "test",
          "model": "m",
          "systemPrompt": "You are concise.",
          "history": [
            {"role": "user", "content": "Earlier user turn."},
            {"role": "assistant", "content": "Earlier assistant turn."}
          ]
        }
        """;

        var configuration = JsonSerializer.Deserialize<AgentInvocationConfiguration>(
            configJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        configuration.History.Should().NotBeNull();
        configuration.History!.Should().HaveCount(2);
        configuration.History[0].Role.Should().Be(ChatMessageRole.User);
        configuration.History[1].Role.Should().Be(ChatMessageRole.Assistant);

        IReadOnlyList<ChatMessage>? firstCallMessages = null;
        var modelClient = new DelegatingModelClient(request =>
        {
            firstCallMessages ??= request.Messages.ToList();
            return Task.FromResult(new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    """{"kind":"Completed","content":"done"}"""),
                InvocationStopReason.EndTurn,
                new TokenUsage(1, 1, 2)));
        });
        var agent = new Agent(new ModelClientRegistry(
        [
            new ModelClientRegistration("test", modelClient)
        ]));

        await agent.InvokeAsync(configuration, "Latest user turn.", ResolvedAgentTools.Empty);

        firstCallMessages.Should().NotBeNull();
        firstCallMessages!.Select(m => (m.Role, m.Content)).Should().BeEquivalentTo(
            new[]
            {
                (ChatMessageRole.System, "You are concise."),
                (ChatMessageRole.User, "Earlier user turn."),
                (ChatMessageRole.Assistant, "Earlier assistant turn."),
                (ChatMessageRole.User, "Latest user turn."),
            },
            opts => opts.WithStrictOrdering());
    }

    private sealed class FakeMcpClient : IMcpClient
    {
        public List<McpInvocation> Invocations { get; } = [];

        public Task<McpToolResult> InvokeAsync(
            string server,
            string toolName,
            JsonNode? arguments,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(new McpInvocation(server, toolName, arguments?.DeepClone()));
            return Task.FromResult(new McpToolResult("{\"hits\":2}"));
        }
    }

    private sealed record McpInvocation(
        string Server,
        string ToolName,
        JsonNode? Arguments);
}
