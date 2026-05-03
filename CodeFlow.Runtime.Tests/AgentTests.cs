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
    public async Task InvokeAsync_ShouldFanOutAnonymousSubAgentCallsInParallel()
    {
        // sc-571: sub-agents are anonymous workers parameterised at spawn time; the parent
        // chooses each invocation's systemPrompt, and children inherit the parent's provider/
        // model unless the spec overrides them.
        var parentClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Delegating to anonymous workers.",
                    ToolCalls:
                    [
                        new ToolCall(
                            "call_spawn",
                            "spawn_subagent",
                            new JsonObject
                            {
                                ["invocations"] = new JsonArray
                                {
                                    new JsonObject { ["systemPrompt"] = "Reviewer A.", ["input"] = "draft A" },
                                    new JsonObject { ["systemPrompt"] = "Reviewer B.", ["input"] = "draft B" },
                                    new JsonObject { ["systemPrompt"] = "Reviewer C.", ["input"] = "draft C" }
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
                payload.Select(item => item!["input"]!.GetValue<string>())
                    .Should().BeEquivalentTo(["draft A", "draft B", "draft C"]);

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

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "parent",
                Model: "parent-model",
                SubAgents: new SubAgentConfig(
                    Provider: "child",
                    Model: "child-model",
                    MaxConcurrent: 3)),
            "Coordinate the reviewers.",
            ResolvedAgentTools.Empty);

        result.Output.Should().Be("Children finished.");
        result.Decision.PortName.Should().Be("Completed");
        childClient.MaxObservedConcurrency.Should().Be(3);
        childClient.SystemPromptsObserved.Should().BeEquivalentTo(
            ["Reviewer A.", "Reviewer B.", "Reviewer C."]);
    }

    [Fact]
    public async Task InvokeAsync_ShouldInheritParentProviderAndModelWhenSpecOmitsThem()
    {
        // SubAgents spec leaves provider/model null → children should run on the parent's
        // ModelClient registration, not require a separate one.
        var capturedChildModels = new System.Collections.Concurrent.ConcurrentBag<string>();
        var registry = new ModelClientRegistry(
        [
            new ModelClientRegistration("solo", new SoloRoutingClient(capturedChildModels))
        ]);
        var agent = new Agent(registry);

        await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "solo",
                Model: "shared-model",
                SubAgents: new SubAgentConfig(MaxConcurrent: 1)),
            "spawn one",
            ResolvedAgentTools.Empty);

        capturedChildModels.Should().Contain("shared-model");
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrottleSubAgentSpawnsToMaxConcurrent()
    {
        // The parent attempts a 4-way spawn but the spec caps concurrency at 2.
        var parentClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Delegating to four workers.",
                    ToolCalls:
                    [
                        new ToolCall(
                            "call_spawn",
                            "spawn_subagent",
                            new JsonObject
                            {
                                ["invocations"] = new JsonArray
                                {
                                    new JsonObject { ["systemPrompt"] = "p1", ["input"] = "1" },
                                    new JsonObject { ["systemPrompt"] = "p2", ["input"] = "2" },
                                    new JsonObject { ["systemPrompt"] = "p3", ["input"] = "3" },
                                    new JsonObject { ["systemPrompt"] = "p4", ["input"] = "4" }
                                }
                            })
                    ]),
                InvocationStopReason.ToolCalls),
            _ => new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "Done."),
                InvocationStopReason.EndTurn)
        ]);
        var childClient = new ConcurrencyTrackingChildClient();
        var agent = new Agent(new ModelClientRegistry(
        [
            new ModelClientRegistration("parent", parentClient),
            new ModelClientRegistration("child", childClient)
        ]));

        await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "parent",
                Model: "parent-model",
                SubAgents: new SubAgentConfig(
                    Provider: "child",
                    Model: "child-model",
                    MaxConcurrent: 2)),
            "Spawn four with cap of two.",
            ResolvedAgentTools.Empty);

        childClient.TotalInvocations.Should().Be(4);
        childClient.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(2);
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

    private sealed class SoloRoutingClient(System.Collections.Concurrent.ConcurrentBag<string> capturedModels)
        : IModelClient
    {
        public Task<InvocationResponse> InvokeAsync(
            InvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            // First call is the parent's invocation; subsequent calls are the child(ren).
            // The child should use the parent's "shared-model" since the spec didn't override.
            capturedModels.Add(request.Model);

            if (capturedModels.Count == 1)
            {
                return Task.FromResult(new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Delegating.",
                        ToolCalls:
                        [
                            new ToolCall(
                                "call_spawn",
                                "spawn_subagent",
                                new JsonObject
                                {
                                    ["invocations"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["systemPrompt"] = "Worker.",
                                            ["input"] = "do it"
                                        }
                                    }
                                })
                        ]),
                    InvocationStopReason.ToolCalls));
            }

            // Child invocation — emit a Completed decision so the loop terminates promptly.
            if (capturedModels.Count == 2)
            {
                return Task.FromResult(new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "child done"),
                    InvocationStopReason.EndTurn));
            }

            // Parent's terminal call after the tool result is back.
            return Task.FromResult(new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "all done"),
                InvocationStopReason.EndTurn));
        }
    }

    private sealed class ConcurrencyTrackingChildClient : IModelClient
    {
        private int currentConcurrency;
        private int maxObservedConcurrency;
        private int totalInvocations;

        public int MaxObservedConcurrency => Volatile.Read(ref maxObservedConcurrency);
        public int TotalInvocations => Volatile.Read(ref totalInvocations);

        public async Task<InvocationResponse> InvokeAsync(
            InvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref totalInvocations);
            var current = Interlocked.Increment(ref currentConcurrency);
            UpdateMax(current);

            try
            {
                // Hold long enough that overlapping callers under the cap would clearly stack.
                await Task.Delay(40, cancellationToken);
                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "ok"),
                    InvocationStopReason.EndTurn);
            }
            finally
            {
                Interlocked.Decrement(ref currentConcurrency);
            }
        }

        private void UpdateMax(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref maxObservedConcurrency);
                if (current <= observed) return;
                if (Interlocked.CompareExchange(ref maxObservedConcurrency, current, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class ConcurrentChildModelClient(int expectedConcurrency) : IModelClient
    {
        private readonly TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentBag<string> systemPrompts = new();
        private int currentConcurrency;
        private int maxObservedConcurrency;
        private int startedCount;

        public int MaxObservedConcurrency => maxObservedConcurrency;
        public IReadOnlyCollection<string> SystemPromptsObserved => systemPrompts;

        public async Task<InvocationResponse> InvokeAsync(
            InvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            var systemMessage = request.Messages.FirstOrDefault(m => m.Role == ChatMessageRole.System);
            if (systemMessage is not null)
            {
                systemPrompts.Add(systemMessage.Content);
            }

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
