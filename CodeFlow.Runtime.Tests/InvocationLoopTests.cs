using FluentAssertions;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Tests;

public sealed class InvocationLoopTests
{
    [Fact]
    public async Task RunAsync_ShouldExposeSetContextWrites_OnSuccessfulSubmit()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Saving and submitting.",
                    ToolCalls:
                    [
                        new ToolCall("call_setctx", "setContext",
                            new JsonObject { ["key"] = "foo", ["value"] = 42 }),
                        new ToolCall("call_setglobal", "setGlobal",
                            new JsonObject { ["key"] = "bar", ["value"] = "hello" }),
                        new ToolCall("call_submit", "submit",
                            new JsonObject { ["decision"] = "Approved" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Persist and ship.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Approved", null, null)]));

        result.Decision.PortName.Should().Be("Approved");
        result.ContextUpdates.Should().NotBeNull();
        result.ContextUpdates!["foo"].GetInt32().Should().Be(42);
        result.GlobalUpdates.Should().NotBeNull();
        result.GlobalUpdates!["bar"].GetString().Should().Be("hello");
    }

    [Fact]
    public async Task RunAsync_ShouldDiscardSetContextWrites_OnFailedTerminal()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Will save then fail.",
                    ToolCalls:
                    [
                        new ToolCall("call_setctx", "setContext",
                            new JsonObject { ["key"] = "foo", ["value"] = 1 }),
                        new ToolCall("call_fail", "fail",
                            new JsonObject { ["reason"] = "blew up" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Try.")],
            "gpt-5"));

        result.Decision.PortName.Should().Be("Failed");
        result.ContextUpdates.Should().BeNull("failed terminations discard pending writes");
        result.GlobalUpdates.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_ShouldRejectSetContextOversizedValue_AsToolError()
    {
        var oversized = new string('x', 256 * 1024 + 16);
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Trying a big value.",
                    ToolCalls:
                    [
                        new ToolCall("call_set", "setContext",
                            new JsonObject { ["key"] = "blob", ["value"] = oversized })
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                request.Messages[^1].Role.Should().Be(ChatMessageRole.Tool);
                request.Messages[^1].IsError.Should().BeTrue();
                request.Messages[^1].Content.Should().Contain("setContext");
                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Recovered.",
                        ToolCalls:
                        [
                            new ToolCall("call_submit", "submit",
                                new JsonObject { ["decision"] = "Approved" })
                        ]),
                    InvocationStopReason.ToolCalls);
            }
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Save big stuff.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Approved", null, null)]));

        result.Decision.PortName.Should().Be("Approved");
        result.ContextUpdates.Should().BeNull("rejected writes never enter the pending bag");
    }

    [Fact]
    public async Task RunAsync_ShouldAdvertise_SetContextAndSetGlobalTools()
    {
        IReadOnlyList<ToolSchema>? advertisedTools = null;
        var modelClient = new ScriptedModelClient(
        [
            request =>
            {
                advertisedTools = request.Tools;
                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "done."),
                    InvocationStopReason.EndTurn);
            }
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "anything")],
            "gpt-5"));

        advertisedTools.Should().NotBeNull();
        advertisedTools!.Should().Contain(t => t.Name == "setContext");
        advertisedTools.Should().Contain(t => t.Name == "setGlobal");
    }

    [Fact]
    public async Task RunAsync_ShouldReturnCompletedDecision_WhenModelReturnsFinalText()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "All done."),
                InvocationStopReason.EndTurn,
                new TokenUsage(12, 6, 18))
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Ship it.")],
            "gpt-5"));

        result.Output.Should().Be("All done.");
        result.Decision.PortName.Should().Be("Completed");
        result.ToolCallsExecuted.Should().Be(0);
        result.TokenUsage.Should().BeEquivalentTo(new TokenUsage(12, 6, 18));
    }

    [Fact]
    public async Task RunAsync_ShouldReturnTerminalDecision_WhenSubmitToolIsInvoked()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Review complete.",
                    ToolCalls:
                    [
                        new ToolCall(
                            "call_submit",
                            "submit",
                            new JsonObject
                            {
                                ["decision"] = "Rejected",
                                ["payload"] = new JsonObject
                                {
                                    ["reasons"] = new JsonArray("Tighten validation", "Add regression tests"),
                                    ["ticket"] = "CF-17"
                                }
                            })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Review this change.")],
            "gpt-5",
            DeclaredOutputs:
            [
                new AgentOutputDeclaration("Approved", null, null),
                new AgentOutputDeclaration("Rejected", null, null)
            ]));

        result.Output.Should().Be("Review complete.");
        result.ToolCallsExecuted.Should().Be(1);
        result.Decision.PortName.Should().Be("Rejected");
        var payload = result.Decision.Payload!.AsObject();
        payload["reasons"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .Should().Equal("Tighten validation", "Add regression tests");
        payload["ticket"]!.GetValue<string>().Should().Be("CF-17");
    }

    [Fact]
    public async Task RunAsync_ShouldAdvertiseSubmitTool_WithDeclaredOutputsAsEnum()
    {
        IReadOnlyList<ToolSchema>? advertisedTools = null;
        var modelClient = new ScriptedModelClient(
        [
            request =>
            {
                advertisedTools = request.Tools;
                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "done."),
                    InvocationStopReason.EndTurn);
            }
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Classify it.")],
            "gpt-5",
            DeclaredOutputs:
            [
                new AgentOutputDeclaration("NewProject", null, null),
                new AgentOutputDeclaration("Feature", null, null),
                new AgentOutputDeclaration("BugFix", null, null)
            ]));

        advertisedTools.Should().NotBeNull();
        var submitTool = advertisedTools!.Should().ContainSingle(tool => tool.Name == "submit").Subject;
        var decisionEnum = submitTool.Parameters!["properties"]!["decision"]!["enum"]!.AsArray();
        decisionEnum.Select(node => node!.GetValue<string>())
            .Should().Equal("NewProject", "Feature", "BugFix");
    }

    [Fact]
    public async Task RunAsync_ShouldRouteToCustomDeclaredPort()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Classified as a bug.",
                    ToolCalls:
                    [
                        new ToolCall(
                            "call_submit",
                            "submit",
                            new JsonObject { ["decision"] = "BugFix" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Triage this.")],
            "gpt-5",
            DeclaredOutputs:
            [
                new AgentOutputDeclaration("NewProject", null, null),
                new AgentOutputDeclaration("Feature", null, null),
                new AgentOutputDeclaration("BugFix", null, null)
            ]));

        result.Decision.PortName.Should().Be("BugFix");
    }

    [Fact]
    public async Task RunAsync_ShouldReturnToolError_WhenSubmittedPortIsNotDeclared()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Picking an unknown port.",
                    ToolCalls:
                    [
                        new ToolCall(
                            "call_submit",
                            "submit",
                            new JsonObject { ["decision"] = "ImaginaryPort" })
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                request.Messages[^1].Role.Should().Be(ChatMessageRole.Tool);
                request.Messages[^1].IsError.Should().BeTrue();
                request.Messages[^1].Content.Should().Contain("ImaginaryPort");
                request.Messages[^1].Content.Should().Contain("Approved");
                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Retrying with a real port.",
                        ToolCalls:
                        [
                            new ToolCall(
                                "call_submit",
                                "submit",
                                new JsonObject { ["decision"] = "Approved" })
                        ]),
                    InvocationStopReason.ToolCalls);
            }
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Decide.")],
            "gpt-5",
            DeclaredOutputs:
            [
                new AgentOutputDeclaration("Approved", null, null),
                new AgentOutputDeclaration("Rejected", null, null)
            ]));

        result.Decision.PortName.Should().Be("Approved");
        result.ToolCallsExecuted.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_ShouldPropagateToolErrorsBackToTheModel()
    {
        var provider = new FakeToolProvider(
            ToolCategory.Execution,
            [new ToolSchema("unstable", "Fails every time.", new JsonObject(), IsMutating: true)])
        {
            ExceptionToThrow = new InvalidOperationException("Tool exploded.")
        };
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Let me try that.",
                    ToolCalls:
                    [
                        new ToolCall("call_tool", "unstable", new JsonObject())
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                request.Messages.Should().HaveCount(3);
                var toolMessage = request.Messages[^1];
                toolMessage.Role.Should().Be(ChatMessageRole.Tool);
                toolMessage.ToolCallId.Should().Be("call_tool");
                toolMessage.IsError.Should().BeTrue();
                toolMessage.Content.Should().Contain("Tool exploded.");

                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "Recovered after the tool failure."),
                    InvocationStopReason.EndTurn);
            }
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([provider]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Run the flaky tool.")],
            "gpt-5"));

        result.Decision.PortName.Should().Be("Completed");
        result.Output.Should().Be("Recovered after the tool failure.");
        provider.InvokedToolNames.Should().ContainSingle().Which.Should().Be("unstable");
    }

    [Fact]
    public async Task RunAsync_ShouldPassToolExecutionContextToProviders()
    {
        var expectedContext = new ToolExecutionContext(
            new ToolWorkspaceContext(
                Guid.NewGuid(),
                "/tmp/codeflow/workspaces/abc123/repo",
                RepoUrl: "https://github.com/example/repo.git",
                RepoIdentityKey: "github.com/example/repo",
                RepoSlug: "example/repo"));
        var provider = new FakeToolProvider(
            ToolCategory.Execution,
            [new ToolSchema("capture_context", "Captures execution context.", new JsonObject())]);
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Capturing context.",
                    ToolCalls:
                    [
                        new ToolCall("call_context", "capture_context", new JsonObject())
                    ]),
                InvocationStopReason.ToolCalls),
            _ => new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "done"),
                InvocationStopReason.EndTurn)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([provider]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Capture the current tool context.")],
            "gpt-5",
            ToolExecutionContext: expectedContext));

        result.Decision.PortName.Should().Be("Completed");
        provider.InvokedContexts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(expectedContext);
    }

    [Fact]
    public async Task RunAsync_ShouldFailWhenToolCallBudgetIsExceeded()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "First tool.",
                    ToolCalls:
                    [
                        new ToolCall("call_echo", "echo", new JsonObject { ["text"] = "hello" })
                    ]),
                InvocationStopReason.ToolCalls),
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Second tool.",
                    ToolCalls:
                    [
                        new ToolCall("call_now", "now", new JsonObject())
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Use two tools.")],
            "gpt-5",
            Budget: new InvocationLoopBudget
            {
                MaxToolCalls = 1,
                MaxLoopDuration = TimeSpan.FromMinutes(1),
                MaxConsecutiveNonMutatingCalls = 10
            }));

        result.Decision.PortName.Should().Be("Failed");
        result.Decision.Payload!.AsObject()["reason"]!.GetValue<string>()
            .Should().Be(InvocationLoopFailureReasons.ToolCallBudgetExceeded);
        result.ToolCallsExecuted.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_ShouldFailWhenLoopDurationIsExceeded()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Need one tool first.",
                    ToolCalls:
                    [
                        new ToolCall("call_echo", "echo", new JsonObject { ["text"] = "hello" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var clock = new SequenceClock(
        [
            DateTimeOffset.Parse("2026-04-20T09:30:00Z"),
            DateTimeOffset.Parse("2026-04-20T09:30:00Z"),
            DateTimeOffset.Parse("2026-04-20T09:30:00Z"),
            DateTimeOffset.Parse("2026-04-20T09:30:05Z")
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]), clock.GetUtcNow);

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Take too long.")],
            "gpt-5",
            Budget: new InvocationLoopBudget
            {
                MaxToolCalls = 5,
                MaxLoopDuration = TimeSpan.FromSeconds(1),
                MaxConsecutiveNonMutatingCalls = 5
            }));

        result.Decision.PortName.Should().Be("Failed");
        result.Decision.Payload!.AsObject()["reason"]!.GetValue<string>()
            .Should().Be(InvocationLoopFailureReasons.LoopDurationExceeded);
        modelClient.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ShouldFailWhenConsecutiveNonMutatingToolCallsExceedBudget()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "First read-only tool.",
                    ToolCalls:
                    [
                        new ToolCall("call_echo", "echo", new JsonObject { ["text"] = "hello" })
                    ]),
                InvocationStopReason.ToolCalls),
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Second read-only tool.",
                    ToolCalls:
                    [
                        new ToolCall("call_now", "now", new JsonObject())
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Keep reading.")],
            "gpt-5",
            Budget: new InvocationLoopBudget
            {
                MaxToolCalls = 5,
                MaxLoopDuration = TimeSpan.FromMinutes(1),
                MaxConsecutiveNonMutatingCalls = 1
            }));

        result.Decision.PortName.Should().Be("Failed");
        result.Decision.Payload!.AsObject()["reason"]!.GetValue<string>()
            .Should().Be(InvocationLoopFailureReasons.ConsecutiveNonMutatingCallsExceeded);
        result.ToolCallsExecuted.Should().Be(2);
    }

    private sealed class ScriptedModelClient(IReadOnlyList<Func<InvocationRequest, InvocationResponse>> steps) : IModelClient
    {
        private int nextStepIndex;

        public int InvocationCount { get; private set; }

        public Task<InvocationResponse> InvokeAsync(
            InvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;

            if (nextStepIndex >= steps.Count)
            {
                throw new InvalidOperationException("No scripted model response remains.");
            }

            return Task.FromResult(steps[nextStepIndex++](request));
        }
    }

    private sealed class FakeToolProvider(ToolCategory category, IReadOnlyList<ToolSchema> tools) : IToolProvider
    {
        public Exception? ExceptionToThrow { get; init; }

        public List<string> InvokedToolNames { get; } = [];
        public List<ToolExecutionContext?> InvokedContexts { get; } = [];

        public ToolCategory Category { get; } = category;

        public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy)
        {
            return tools;
        }

        public Task<ToolResult> InvokeAsync(
            ToolCall toolCall,
            CancellationToken cancellationToken = default,
            ToolExecutionContext? context = null)
        {
            InvokedToolNames.Add(toolCall.Name);
            InvokedContexts.Add(context);

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(new ToolResult(toolCall.Id, $"handled:{toolCall.Name}"));
        }
    }

    private sealed class SequenceClock(IReadOnlyList<DateTimeOffset> timestamps)
    {
        private int nextIndex;

        public DateTimeOffset GetUtcNow()
        {
            if (timestamps.Count == 0)
            {
                throw new InvalidOperationException("At least one timestamp is required.");
            }

            var index = Math.Min(nextIndex, timestamps.Count - 1);
            nextIndex++;
            return timestamps[index];
        }
    }
}
