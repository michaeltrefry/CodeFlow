using FluentAssertions;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Tests;

public sealed class InvocationLoopTests
{
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
        result.Decision.Should().BeOfType<CompletedDecision>();
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
                                ["decision"] = "approved_with_actions",
                                ["payload"] = new JsonObject
                                {
                                    ["actions"] = new JsonArray("Tighten validation", "Add regression tests"),
                                    ["ticket"] = "CF-17"
                                }
                            })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Review this change.")],
            "gpt-5"));

        result.Output.Should().Be("Review complete.");
        result.ToolCallsExecuted.Should().Be(1);
        var decision = result.Decision.Should().BeOfType<ApprovedWithActionsDecision>().Subject;
        decision.Actions.Should().Equal("Tighten validation", "Add regression tests");
        decision.DecisionPayload!["ticket"]!.GetValue<string>().Should().Be("CF-17");
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

        result.Decision.Should().BeOfType<CompletedDecision>();
        result.Output.Should().Be("Recovered after the tool failure.");
        provider.InvokedToolNames.Should().ContainSingle().Which.Should().Be("unstable");
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

        var decision = result.Decision.Should().BeOfType<FailedDecision>().Subject;
        decision.Reason.Should().Be(InvocationLoopFailureReasons.ToolCallBudgetExceeded);
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

        var decision = result.Decision.Should().BeOfType<FailedDecision>().Subject;
        decision.Reason.Should().Be(InvocationLoopFailureReasons.LoopDurationExceeded);
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

        var decision = result.Decision.Should().BeOfType<FailedDecision>().Subject;
        decision.Reason.Should().Be(InvocationLoopFailureReasons.ConsecutiveNonMutatingCallsExceeded);
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

        public ToolCategory Category { get; } = category;

        public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy)
        {
            return tools;
        }

        public Task<ToolResult> InvokeAsync(
            ToolCall toolCall,
            AgentInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            InvokedToolNames.Add(toolCall.Name);

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
