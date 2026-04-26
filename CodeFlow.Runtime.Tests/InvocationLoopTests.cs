using FluentAssertions;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Tests;

public sealed class InvocationLoopTests
{
    [Fact]
    public async Task RunAsync_ShouldRePromptOnEmptyContentSubmit_WhenDeclaredOutputsExist()
    {
        // Real bug from prd-intake testing: an LLM agent with declared outputs called
        // `submit` on its very first turn with NO assistant message content. The runtime
        // used to accept that and write a 0-byte artifact downstream, which then rendered
        // as an empty HITL form / empty next-agent input. Now the loop must re-prompt
        // the LLM to produce real content before accepting the terminal submit.
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ToolCall("call_submit", "submit",
                            new JsonObject { ["decision"] = "Continue" })
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                request.Messages[^1].Role.Should().Be(ChatMessageRole.User);
                request.Messages[^1].Content.Should().Contain("without writing any assistant message");
                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "What problem are you trying to solve?",
                        ToolCalls:
                        [
                            new ToolCall("call_submit2", "submit",
                                new JsonObject { ["decision"] = "Continue" })
                        ]),
                    InvocationStopReason.ToolCalls);
            }
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Interview me.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Continue", null, null)]));

        result.Decision.PortName.Should().Be("Continue");
        result.Output.Should().Be("What problem are you trying to solve?",
            "the second submit succeeds because the assistant produced real content this time");
    }

    [Fact]
    public async Task RunAsync_ShouldAddToolOutputForRejectedEmptyContentSubmit_BeforeRePromptingUser()
    {
        // Regression: the empty-content retry path used to push only a User reminder back into
        // the transcript, leaving the prior assistant turn's `submit` function_call without a
        // matching `function_call_output`. The next request to OpenAI's Responses API failed
        // with "No tool output found for function call <id>". The fix adds a Tool message
        // (function_call_output) for the rejected submit before the User reminder.
        var capturedSecondRequest = (InvocationRequest?)null;
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ToolCall("call_submit_first", "submit",
                            new JsonObject { ["decision"] = "Continue" })
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                capturedSecondRequest = request;
                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Real content this time.",
                        ToolCalls:
                        [
                            new ToolCall("call_submit_second", "submit",
                                new JsonObject { ["decision"] = "Continue" })
                        ]),
                    InvocationStopReason.ToolCalls);
            }
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Interview me.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Continue", null, null)]));

        capturedSecondRequest.Should().NotBeNull("the second round must occur for the retry to land");
        var messages = capturedSecondRequest!.Messages;

        // Sanity: prior assistant turn with the rejected submit is in the transcript.
        var assistantSubmit = messages.Single(m =>
            m.Role == ChatMessageRole.Assistant
            && m.ToolCalls is not null
            && m.ToolCalls.Any(t => t.Id == "call_submit_first"));
        assistantSubmit.Should().NotBeNull();

        // The fix: a Tool message paired by ToolCallId must follow the assistant turn before the
        // User reminder, satisfying the Responses-API requirement that every prior function_call
        // has a function_call_output.
        var toolOutputs = messages
            .Where(m => m.Role == ChatMessageRole.Tool && m.ToolCallId == "call_submit_first")
            .ToList();
        toolOutputs.Should().HaveCount(1,
            "the rejected submit must have a matching function_call_output to satisfy the provider protocol");
        toolOutputs[0].IsError.Should().BeTrue();

        // The User reminder is still pushed back so the model knows what to fix. (Search by
        // content rather than position — the InvocationRequest holds the transcript by
        // reference, so the loop's later writes are visible on capturedSecondRequest.Messages.)
        messages.Should().Contain(m =>
            m.Role == ChatMessageRole.User
            && m.Content != null
            && m.Content.Contains("without writing any assistant message"));

        // Order check: the rejected submit's tool output and the User reminder must both come
        // BEFORE the next assistant turn (round 2's response). Otherwise the protocol breaks.
        var indexed = messages.Select((m, i) => (m, i)).ToList();
        var rejectedSubmitIdx = indexed.Single(t => ReferenceEquals(t.m, assistantSubmit)).i;
        var toolOutputIdx = indexed.Single(t =>
            t.m.Role == ChatMessageRole.Tool && t.m.ToolCallId == "call_submit_first").i;
        var userReminderIdx = indexed.Single(t =>
            t.m.Role == ChatMessageRole.User
            && t.m.Content != null
            && t.m.Content.Contains("without writing any assistant message")).i;
        toolOutputIdx.Should().BeGreaterThan(rejectedSubmitIdx);
        userReminderIdx.Should().BeGreaterThan(toolOutputIdx);
    }

    [Fact]
    public async Task RunAsync_ShouldAcceptEmptyContent_WhenSubmittingFailedPort()
    {
        // The Failed port is the implicit error sink — failure messages naturally have
        // their reason in the payload, not the assistant message. Don't force content.
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ToolCall("call_fail", "fail",
                            new JsonObject { ["reason"] = "I cannot continue." })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Try.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Continue", null, null)]));

        result.Decision.PortName.Should().Be("Failed");
    }

    [Fact]
    public async Task RunAsync_ShouldRePromptOnNoToolCall_WhenDeclaredOutputsExist()
    {
        // Real bug from prd-intake testing: an interviewer with declared outputs
        // [Continue, Sufficient] ended its 3rd round with just text and no tool call. The
        // runtime used to default to port "Completed" — which isn't in the declared set —
        // silently bypassing downstream routing. Now the loop must re-prompt, and the
        // LLM's next turn (which DOES submit) determines the routing port.
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "I think I have enough information now."),
                InvocationStopReason.EndTurn),
            request =>
            {
                request.Messages[^1].Role.Should().Be(ChatMessageRole.User);
                request.Messages[^1].Content.Should().Contain("submit");
                request.Messages[^1].Content.Should().Contain("Continue");
                request.Messages[^1].Content.Should().Contain("Sufficient");
                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Okay, submitting Sufficient.",
                        ToolCalls:
                        [
                            new ToolCall("call_submit", "submit",
                                new JsonObject { ["decision"] = "Sufficient" })
                        ]),
                    InvocationStopReason.ToolCalls);
            }
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Interview me.")],
            "gpt-5",
            DeclaredOutputs:
            [
                new AgentOutputDeclaration("Continue", null, null),
                new AgentOutputDeclaration("Sufficient", null, null)
            ]));

        result.Decision.PortName.Should().Be("Sufficient",
            "the routing port must come from a real submit, not a runtime default");
    }

    [Fact]
    public async Task RunAsync_ShouldKeepLastNonEmptyAssistantContent_AsOutput_AcrossSplitTurns()
    {
        // Real-world LLM behavior: the model writes its substantive output (question, PRD, etc.)
        // alongside a non-terminal tool call (e.g., setContext), then submits in a later round
        // with empty content. The artifact handed downstream must be the substantive content,
        // not the empty submit-round content.
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "What problem are you trying to solve?",
                    ToolCalls:
                    [
                        new ToolCall("call_setctx", "setContext",
                            new JsonObject { ["key"] = "lastQuestion", ["value"] = "What problem?" })
                    ]),
                InvocationStopReason.ToolCalls),
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ToolCall("call_submit", "submit",
                            new JsonObject { ["decision"] = "Continue" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Interview me.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Continue", null, null)]));

        result.Decision.PortName.Should().Be("Continue");
        result.Output.Should().Be("What problem are you trying to solve?",
            "the substantive question must survive past the empty-content submit round");
    }

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
                        new ToolCall("call_setworkflow", "setWorkflow",
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
        result.WorkflowUpdates.Should().NotBeNull();
        result.WorkflowUpdates!["bar"].GetString().Should().Be("hello");
    }

    [Fact]
    public async Task RunAsync_SetWorkflowReservedKey_ReturnsToolErrorAndDoesNotPersist()
    {
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Trying to overwrite workDir.",
                    ToolCalls:
                    [
                        new ToolCall("call_reserved", "setWorkflow",
                            new JsonObject { ["key"] = "workDir", ["value"] = "/etc/evil" }),
                        new ToolCall("call_submit", "submit",
                            new JsonObject { ["decision"] = "Continue" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Try the reserved key.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Continue", null, null)]));

        result.Decision.PortName.Should().Be("Continue",
            "the rejected setWorkflow returns an error tool result; the loop continues to submit");

        var setWorkflowToolMessage = result.Transcript
            .OfType<ChatMessage>()
            .FirstOrDefault(m => m.Role == ChatMessageRole.Tool
                && m.ToolCallId == "call_reserved");
        setWorkflowToolMessage.Should().NotBeNull("the reserved-key write must surface a tool result");
        setWorkflowToolMessage!.Content.Should().Contain("workDir");
        setWorkflowToolMessage.Content.Should().Contain("framework-managed workflow variable");

        if (result.WorkflowUpdates is { } workflowVars)
        {
            workflowVars.ContainsKey("workDir").Should().BeFalse(
                "the rejected write must not be persisted into the workflow bag");
        }
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
        result.WorkflowUpdates.Should().BeNull();
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
    public async Task RunAsync_ShouldRejectSetWorkflowOversizedSingleValue_WithRemediationPointer()
    {
        // V1: per-call 16 KiB cap on setWorkflow values. An agent that tries to stream a large
        // document (PRD, plan) into the workflow bag mid-turn must get a typed tool error
        // pointing at the output-script remediation path, the loop continues, and the model
        // retries with a smaller value or a different approach.
        var justOver16KiB = new string('x', 16 * 1024 + 1);
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Streaming the plan into the workflow bag.",
                    ToolCalls:
                    [
                        new ToolCall("call_set", "setWorkflow",
                            new JsonObject { ["key"] = "currentPlan", ["value"] = justOver16KiB })
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                request.Messages[^1].Role.Should().Be(ChatMessageRole.Tool);
                request.Messages[^1].IsError.Should().BeTrue();
                request.Messages[^1].Content.Should().Contain("setWorkflow");
                request.Messages[^1].Content.Should().Contain("output script");
                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Recovered with a smaller summary.",
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
            [new ChatMessage(ChatMessageRole.User, "Save the plan.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Approved", null, null)]));

        result.Decision.PortName.Should().Be("Approved");
        result.WorkflowUpdates.Should().BeNull(
            "the rejected per-call write must not reach the workflow bag");
    }

    [Fact]
    public async Task RunAsync_ShouldAcceptSetWorkflowJustUnderPerCallCap()
    {
        // CR1: small writes continue to succeed unchanged. A 500-byte setWorkflow call sits well
        // below the 16 KiB cap and persists into the pending bag like before.
        var smallValue = new string('x', 500);
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Saving and submitting.",
                    ToolCalls:
                    [
                        new ToolCall("call_set", "setWorkflow",
                            new JsonObject { ["key"] = "summary", ["value"] = smallValue }),
                        new ToolCall("call_submit", "submit",
                            new JsonObject { ["decision"] = "Approved" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Save and ship.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Approved", null, null)]));

        result.Decision.PortName.Should().Be("Approved");
        result.WorkflowUpdates.Should().NotBeNull();
        result.WorkflowUpdates!["summary"].GetString().Should().Be(smallValue);
    }

    [Fact]
    public async Task RunAsync_ShouldAdvertise_SetContextAndSetWorkflowTools()
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
        advertisedTools.Should().Contain(t => t.Name == "setWorkflow");
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
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Classified.",
                        ToolCalls:
                        [
                            new ToolCall("call_submit", "submit",
                                new JsonObject { ["decision"] = "NewProject" })
                        ]),
                    InvocationStopReason.ToolCalls);
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

    [Fact]
    public void EnsureToolCallPairing_PassesOnEmptyTranscript()
    {
        // Defensive smoke check — the precondition is invoked before the very first model call,
        // when the transcript may be just a single User message.
        var transcript = new[] { new ChatMessage(ChatMessageRole.User, "Hi.") };

        var act = () => InvocationLoop.EnsureToolCallPairing(transcript);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureToolCallPairing_PassesWhenEveryFunctionCallHasMatchingOutput()
    {
        var transcript = new[]
        {
            new ChatMessage(ChatMessageRole.User, "Use a tool."),
            new ChatMessage(
                ChatMessageRole.Assistant,
                "Calling tool.",
                ToolCalls: [new ToolCall("call_a", "echo", new JsonObject())]),
            new ChatMessage(ChatMessageRole.Tool, "ok", ToolCallId: "call_a"),
            new ChatMessage(
                ChatMessageRole.Assistant,
                "Calling another.",
                ToolCalls: [new ToolCall("call_b", "echo", new JsonObject())]),
            new ChatMessage(ChatMessageRole.Tool, "ok", ToolCallId: "call_b")
        };

        var act = () => InvocationLoop.EnsureToolCallPairing(transcript);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureToolCallPairing_ThrowsWithOffendingIdsWhenAFunctionCallHasNoOutput()
    {
        // Synthetic regression target for V3: a future loop bug that pushes a User reminder
        // back into the transcript without first writing a Tool message for the prior assistant
        // tool call. Without this guard the next model call would surface the provider's
        // "No tool output found for function call <id>" — opaque and two layers from the bug.
        var transcript = new[]
        {
            new ChatMessage(ChatMessageRole.User, "Use a tool."),
            new ChatMessage(
                ChatMessageRole.Assistant,
                "Calling tool.",
                ToolCalls:
                [
                    new ToolCall("call_paired", "echo", new JsonObject()),
                    new ToolCall("call_orphan", "echo", new JsonObject())
                ]),
            new ChatMessage(ChatMessageRole.Tool, "ok", ToolCallId: "call_paired"),
            new ChatMessage(ChatMessageRole.User, "Why didn't you finish?")
        };

        var act = () => InvocationLoop.EnsureToolCallPairing(transcript);

        var thrown = act.Should().Throw<OrphanFunctionCallException>().Which;
        thrown.OrphanedCallIds.Should().ContainSingle().Which.Should().Be("call_orphan");
        thrown.Message.Should().Contain("call_orphan");
    }

    [Fact]
    public async Task RunAsync_ThrowsOrphanFunctionCallException_WhenTranscriptStartsWithUnpairedAssistantToolCall()
    {
        // End-to-end shape: an attacker / buggy caller hands the loop an initial transcript
        // that already has an orphaned assistant function_call. The loop must throw before
        // the first HTTP call so the caller's stack frame is in the trace.
        var modelClient = new ScriptedModelClient(
        [
            _ => throw new InvalidOperationException(
                "model client must not be invoked when the transcript is invalid")
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var transcript = new[]
        {
            new ChatMessage(ChatMessageRole.User, "Begin."),
            new ChatMessage(
                ChatMessageRole.Assistant,
                "Half-done.",
                ToolCalls: [new ToolCall("call_lost", "echo", new JsonObject())])
        };

        var act = async () => await loop.RunAsync(new InvocationLoopRequest(
            transcript,
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Continue", null, null)]));

        await act.Should().ThrowAsync<OrphanFunctionCallException>();
        modelClient.InvocationCount.Should().Be(0);
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
