using FluentAssertions;
using System.Text.Json;
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
    public async Task RunAsync_ShouldAcceptEmptyContent_WhenSubmittingPortFlaggedContentOptional()
    {
        // V2: sentinel ports like Cancelled/Skip declare ContentOptional=true so the empty-
        // content guard skips them. The decision itself carries the meaning; downstream
        // consumers don't read the artifact body.
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ToolCall("call_cancel", "submit",
                            new JsonObject { ["decision"] = "Cancelled" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Cancel if you must.")],
            "gpt-5",
            DeclaredOutputs:
            [
                new AgentOutputDeclaration("Approved", null, null),
                new AgentOutputDeclaration("Cancelled", null, null, ContentOptional: true)
            ]));

        result.Decision.PortName.Should().Be("Cancelled");
        modelClient.InvocationCount.Should().Be(1, "no retry should fire when the port is content-optional");
    }

    [Fact]
    public async Task RunAsync_ShouldStillRejectEmptyContent_OnNonOptionalPort_WhenSiblingIsContentOptional()
    {
        // Mirror of the prior test: the sibling Approved port is NOT content-optional, so an
        // empty submit there still triggers the retry. Confirms the per-port lookup is correct
        // (not "any contentOptional port disables the guard for the whole agent").
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ToolCall("call_approve", "submit",
                            new JsonObject { ["decision"] = "Approved" })
                    ]),
                InvocationStopReason.ToolCalls),
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Real artifact body.",
                    ToolCalls:
                    [
                        new ToolCall("call_approve2", "submit",
                            new JsonObject { ["decision"] = "Approved" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Approve.")],
            "gpt-5",
            DeclaredOutputs:
            [
                new AgentOutputDeclaration("Approved", null, null),
                new AgentOutputDeclaration("Cancelled", null, null, ContentOptional: true)
            ]));

        result.Decision.PortName.Should().Be("Approved");
        result.Output.Should().Be("Real artifact body.");
        modelClient.InvocationCount.Should().Be(2, "the empty Approved submit must trigger one retry");
    }

    [Fact]
    public async Task RunAsync_ShouldNameTheOffendingPort_InEmptyContentToolError()
    {
        // V2 acceptance: the tool error returned to the model names the port and tells the
        // model to write content BEFORE submit. Helps the LLM self-correct in one turn.
        InvocationRequest? capturedSecondRequest = null;
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ToolCall("call_first", "submit",
                            new JsonObject { ["decision"] = "Approved" })
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                capturedSecondRequest = request;
                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Body content.",
                        ToolCalls:
                        [
                            new ToolCall("call_second", "submit",
                                new JsonObject { ["decision"] = "Approved" })
                        ]),
                    InvocationStopReason.ToolCalls);
            }
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Approve.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Approved", null, null)]));

        capturedSecondRequest.Should().NotBeNull();
        var toolMessage = capturedSecondRequest!.Messages.Single(m =>
            m.Role == ChatMessageRole.Tool && m.ToolCallId == "call_first");
        toolMessage.IsError.Should().BeTrue();
        toolMessage.Content.Should().Contain("\"Approved\"");
        toolMessage.Content.Should().Contain("non-empty");
        toolMessage.Content.Should().Contain("BEFORE calling submit");
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
    public async Task RunAsync_HostToolStagesWorkflowWriteViaSink_PersistsOnSubmit()
    {
        // sc-680: validates the StageWorkflowBagWrite sink end-to-end. A host tool calls the
        // sink during its turn; on a successful submit the staged write must surface in
        // WorkflowUpdates, exactly as if the agent had called setWorkflow directly.
        var hostTool = new SinkUsingToolProvider("stage_test_repos");
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Calling host tool then submitting.",
                    ToolCalls:
                    [
                        new ToolCall("call_host", "stage_test_repos", new JsonObject()),
                        new ToolCall("call_submit", "submit",
                            new JsonObject { ["decision"] = "Approved" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([hostTool]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Bootstrap.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Approved", null, null)]));

        result.Decision.PortName.Should().Be("Approved");
        result.WorkflowUpdates.Should().NotBeNull(
            "the StageWorkflowBagWrite sink must commit on a successful submit, just like setWorkflow");
        result.WorkflowUpdates!["repositories"].ValueKind.Should().Be(JsonValueKind.Array);
        result.WorkflowUpdates["repositories"][0].GetProperty("url").GetString()
            .Should().Be("https://example.invalid/owner/repo");
    }

    [Fact]
    public async Task RunAsync_HostToolStagesWorkflowWriteViaSink_DiscardedOnFailedSubmit()
    {
        // Same sink + same staged write, but the agent calls the `fail` tool instead of
        // `submit`. Pending writes (including those staged via the sink) are discarded,
        // mirroring the existing setWorkflow contract under failure.
        var hostTool = new SinkUsingToolProvider("stage_test_repos");
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Calling host tool then failing.",
                    ToolCalls:
                    [
                        new ToolCall("call_host", "stage_test_repos", new JsonObject()),
                        new ToolCall("call_fail", "fail",
                            new JsonObject { ["reason"] = "blew up" })
                    ]),
                InvocationStopReason.ToolCalls)
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([hostTool]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Bootstrap.")],
            "gpt-5"));

        result.Decision.PortName.Should().Be("Failed");
        result.WorkflowUpdates.Should().BeNull(
            "fail-terminated invocations discard pending writes, including those staged via the sink");
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
                    "Trying to overwrite traceWorkDir.",
                    ToolCalls:
                    [
                        new ToolCall("call_reserved", "setWorkflow",
                            new JsonObject { ["key"] = "traceWorkDir", ["value"] = "/etc/evil" }),
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
        setWorkflowToolMessage!.Content.Should().Contain("traceWorkDir");
        setWorkflowToolMessage.Content.Should().Contain("framework-managed workflow variable");

        if (result.WorkflowUpdates is { } workflowVars)
        {
            workflowVars.ContainsKey("traceWorkDir").Should().BeFalse(
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
        // sc-680: InvocationLoop wraps the request's context with a StageWorkflowBagWrite
        // sink before passing it to host tools, so equality is checked over the data fields
        // only — the sink delegate is intentionally not part of the per-context identity.
        provider.InvokedContexts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                expectedContext,
                options => options.Excluding(c => c.StageWorkflowBagWrite));
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
    public async Task RunAsync_AppendsBudgetWarnings_OnceAtEachThresholdCrossing()
    {
        // Budget = 5 with SoftWarnRemaining=3 and HardWarnRemaining=1: soft fires after the
        // 2nd tool call (3 remain), hard fires after the 4th (1 remains). Each warning must
        // appear exactly once even though the loop iterates many tool calls between them.
        InvocationResponse ToolCallResponse(string id, string toolName) => new(
            new ChatMessage(
                ChatMessageRole.Assistant,
                "Calling " + toolName,
                ToolCalls: [new ToolCall(id, toolName, new JsonObject())]),
            InvocationStopReason.ToolCalls);

        var modelClient = new ScriptedModelClient(
        [
            _ => ToolCallResponse("c1", "now"),
            _ => ToolCallResponse("c2", "now"),
            _ => ToolCallResponse("c3", "now"),
            _ => ToolCallResponse("c4", "now"),
            _ => new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "Done.",
                    ToolCalls: [new ToolCall("call_submit", "submit", new JsonObject { ["decision"] = "Continue" })]),
                InvocationStopReason.ToolCalls)
        ]);

        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Probe budget warnings.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Continue", null, null)],
            Budget: new InvocationLoopBudget
            {
                MaxToolCalls = 5,
                MaxLoopDuration = TimeSpan.FromMinutes(1),
                MaxConsecutiveNonMutatingCalls = 10,
                SoftWarnRemaining = 3,
                HardWarnRemaining = 1
            }));

        result.Decision.PortName.Should().Be("Continue");

        var userTrailers = result.Transcript
            .Where(m => m.Role == ChatMessageRole.User && m.Content.Contains("Tool budget"))
            .Select(m => m.Content)
            .ToArray();

        userTrailers.Should().HaveCount(2,
            "soft and hard warnings should each fire exactly once across the run");
        userTrailers[0].Should().Contain("Tool budget:").And.Contain("3 remaining");
        userTrailers[1].Should().Contain("critical").And.Contain("1 call(s) remaining");
    }

    [Fact]
    public async Task RunAsync_SkipsBudgetWarnings_WhenDisabled()
    {
        InvocationResponse ToolCallResponse(string id) => new(
            new ChatMessage(
                ChatMessageRole.Assistant,
                "Calling now.",
                ToolCalls: [new ToolCall(id, "now", new JsonObject())]),
            InvocationStopReason.ToolCalls);

        var modelClient = new ScriptedModelClient(
        [
            _ => ToolCallResponse("c1"),
            _ => ToolCallResponse("c2"),
            _ => new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "Done.",
                    ToolCalls: [new ToolCall("call_submit", "submit", new JsonObject { ["decision"] = "Continue" })]),
                InvocationStopReason.ToolCalls)
        ]);

        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Disable nudges.")],
            "gpt-5",
            DeclaredOutputs: [new AgentOutputDeclaration("Continue", null, null)],
            Budget: new InvocationLoopBudget
            {
                MaxToolCalls = 3,
                MaxLoopDuration = TimeSpan.FromMinutes(1),
                MaxConsecutiveNonMutatingCalls = 10,
                SoftWarnRemaining = 0,
                HardWarnRemaining = 0
            }));

        result.Decision.PortName.Should().Be("Continue");
        result.Transcript
            .Where(m => m.Role == ChatMessageRole.User && m.Content.Contains("Tool budget"))
            .Should().BeEmpty();
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
    public void FindOrphanedToolCallIds_EmptyTranscript_ReturnsEmpty()
    {
        InvocationLoop.FindOrphanedToolCallIds(Array.Empty<ChatMessage>())
            .Should().BeEmpty();
    }

    [Fact]
    public void FindOrphanedToolCallIds_AllPaired_ReturnsEmpty()
    {
        var transcript = new[]
        {
            new ChatMessage(
                ChatMessageRole.Assistant,
                "",
                ToolCalls: [new ToolCall("call_a", "echo", new JsonObject())]),
            new ChatMessage(ChatMessageRole.Tool, "ok", ToolCallId: "call_a"),
        };

        InvocationLoop.FindOrphanedToolCallIds(transcript).Should().BeEmpty();
    }

    [Fact]
    public void FindOrphanedToolCallIds_ReturnsUnpairedIdsInDeterministicOrder()
    {
        // Multiple orphans in one assistant message — the most common shape when a mid-batch
        // early-exit aborts before processing every tool_call.
        var transcript = new[]
        {
            new ChatMessage(
                ChatMessageRole.Assistant,
                "",
                ToolCalls:
                [
                    new ToolCall("call_z", "echo", new JsonObject()),
                    new ToolCall("call_a", "echo", new JsonObject()),
                    new ToolCall("call_m", "echo", new JsonObject()),
                ]),
            // Only call_z was processed before the exit.
            new ChatMessage(ChatMessageRole.Tool, "ok", ToolCallId: "call_z"),
        };

        InvocationLoop.FindOrphanedToolCallIds(transcript)
            .Should().Equal(["call_a", "call_m"],
                because: "orphans are returned in deterministic ordinal order so error messages + drain stubs are stable");
    }

    [Fact]
    public async Task RunAsync_MidBatchConsecutiveNonMutatingExit_DrainsOrphans_NoPairingViolation()
    {
        // Regression for the orphan-tool-call bug surfaced 2026-05-13 during qwen3-35b Goal
        // testing: an assistant message emits multiple tool calls, the 1st succeeds, the 2nd
        // trips ConsecutiveNonMutatingCallsExceeded, and the 3rd was never dispatched. Before
        // the drain fix, the 3rd tool call's ID stayed orphaned in the returned transcript —
        // breaking the next iteration's `EnsureToolCallPairing` guard when the Goal-node
        // orchestrator forwarded that transcript as History.
        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Three reads in one turn.",
                    ToolCalls:
                    [
                        // First call lands and increments the non-mutating counter.
                        new ToolCall("call_first", "echo", new JsonObject { ["text"] = "a" }),
                        // Second call exceeds MaxConsecutiveNonMutatingCalls=1 → return.
                        new ToolCall("call_second", "echo", new JsonObject { ["text"] = "b" }),
                        // Third call must NOT be left orphaned.
                        new ToolCall("call_third", "now", new JsonObject()),
                    ]),
                InvocationStopReason.ToolCalls),
        ]);
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(new InvocationLoopRequest(
            [new ChatMessage(ChatMessageRole.User, "Read three things.")],
            "gpt-5",
            Budget: new InvocationLoopBudget
            {
                MaxToolCalls = 5,
                MaxLoopDuration = TimeSpan.FromMinutes(1),
                MaxConsecutiveNonMutatingCalls = 1,
            }));

        result.Decision.PortName.Should().Be("Failed");
        result.Decision.Payload!.AsObject()["reason"]!.GetValue<string>()
            .Should().Be(InvocationLoopFailureReasons.ConsecutiveNonMutatingCallsExceeded);

        // The load-bearing assertion: the returned transcript must satisfy the pairing
        // guard. Without the drain, this would throw with `call_third` as the orphan.
        var checkPairing = () => InvocationLoop.EnsureToolCallPairing(result.Transcript);
        checkPairing.Should().NotThrow(
            because: "every unpaired tool_call must be drained with an error Tool stub before "
            + "the loop returns, so a downstream consumer (e.g. Goal-node orchestrator's next "
            + "iteration) can safely use the transcript as History");

        // Specifically: call_third should have a synthetic error Tool message describing the
        // abort reason.
        result.Transcript.Should().Contain(m =>
            m.Role == ChatMessageRole.Tool
            && m.ToolCallId == "call_third"
            && m.IsError
            && m.Content.Contains("ConsecutiveNonMutatingCallsExceeded", StringComparison.Ordinal),
            because: "the drain stub explains WHY the tool call was not dispatched, so the model "
            + "sees a coherent transcript if the same conversation is resumed later");
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

    [Fact]
    public async Task RunAsync_MintsStableInvocationIdPerRound_AndPropagatesToObserverAndRequest()
    {
        // Slice 1 of the Token Usage Tracking epic. The loop must mint a fresh Guid for every
        // LLM round-trip and surface it via both the IInvocationObserver hooks AND the
        // InvocationRequest passed to the model client. Slices 2-4 will lean on this Guid as
        // the FK from a TokenUsageRecord back to the call that produced it.
        var capturedRequests = new List<InvocationRequest>();
        var modelClient = new ScriptedModelClient(
        [
            request =>
            {
                capturedRequests.Add(request);
                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "First round content.",
                        ToolCalls:
                        [
                            new ToolCall("call_set_ctx", InvocationLoop.SetContextToolName,
                                new JsonObject { ["key"] = "k", ["value"] = JsonValue.Create(1) })
                        ]),
                    InvocationStopReason.ToolCalls);
            },
            request =>
            {
                capturedRequests.Add(request);
                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Final round content.",
                        ToolCalls:
                        [
                            new ToolCall("call_submit", InvocationLoop.SubmitToolName,
                                new JsonObject { ["decision"] = "Completed" })
                        ]),
                    InvocationStopReason.ToolCalls);
            }
        ]);
        var observer = new RecordingInvocationObserver();
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        var result = await loop.RunAsync(
            new InvocationLoopRequest(
                [new ChatMessage(ChatMessageRole.User, "Run two rounds.")],
                "gpt-5",
                DeclaredOutputs: [new AgentOutputDeclaration("Completed", null, null)]),
            observer);

        result.Decision.PortName.Should().Be("Completed");

        // Two rounds → two distinct, non-empty Guids surface from both surfaces.
        observer.StartedInvocationIds.Should().HaveCount(2);
        observer.CompletedInvocationIds.Should().HaveCount(2);
        observer.StartedInvocationIds.Should().NotContain(Guid.Empty);
        observer.StartedInvocationIds.Distinct().Should().HaveCount(2,
            "each round must mint a fresh InvocationId; reusing an id across rounds would collapse two TokenUsageRecords into one and break per-round attribution");

        // Started/Completed pair on the same round must share an id (i.e. both events came
        // from the same minted Guid in the loop).
        observer.StartedInvocationIds.Should().Equal(observer.CompletedInvocationIds);

        // The Guids reach the model client too (slice 2-4 capture pivots on this).
        capturedRequests.Should().HaveCount(2);
        capturedRequests.Select(r => r.InvocationId).Should().Equal(observer.StartedInvocationIds);
    }

    [Fact]
    public async Task RunAsync_PropagatesProviderModelAndRawUsageToObserverPerRound()
    {
        // Slice 2 of the Token Usage Tracking epic. The observer surface must carry
        // (provider, model, rawUsage) on every OnModelCallCompletedAsync call so the
        // orchestration-side capture observer has every field it needs to persist a
        // TokenUsageRecord without re-querying anything else.
        using var firstUsageDoc = JsonDocument.Parse("""{"input_tokens":11,"output_tokens":3}""");
        using var secondUsageDoc = JsonDocument.Parse("""{"input_tokens":5,"output_tokens":2,"output_tokens_details":{"reasoning_tokens":1}}""");
        var firstRawUsage = firstUsageDoc.RootElement.Clone();
        var secondRawUsage = secondUsageDoc.RootElement.Clone();

        var modelClient = new ScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "round-one content",
                    ToolCalls:
                    [
                        new ToolCall("call_set_ctx", InvocationLoop.SetContextToolName,
                            new JsonObject { ["key"] = "k", ["value"] = JsonValue.Create(1) })
                    ]),
                InvocationStopReason.ToolCalls,
                new TokenUsage(11, 3, 14),
                firstRawUsage),
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "round-two content",
                    ToolCalls:
                    [
                        new ToolCall("call_submit", InvocationLoop.SubmitToolName,
                            new JsonObject { ["decision"] = "Completed" })
                    ]),
                InvocationStopReason.ToolCalls,
                new TokenUsage(5, 2, 7),
                secondRawUsage)
        ]);
        var observer = new RecordingInvocationObserver();
        var loop = new InvocationLoop(modelClient, new ToolRegistry([new HostToolProvider()]));

        await loop.RunAsync(
            new InvocationLoopRequest(
                [new ChatMessage(ChatMessageRole.User, "Run.")],
                "gpt-5",
                DeclaredOutputs: [new AgentOutputDeclaration("Completed", null, null)],
                Provider: "openai"),
            observer);

        observer.CompletedProviders.Should().Equal("openai", "openai");
        observer.CompletedModels.Should().Equal("gpt-5", "gpt-5");

        // Each round's raw usage must surface to the observer with the provider's payload intact.
        // The capture observer in CodeFlow.Orchestration relies on this round-trip being
        // verbatim — flattening or summing across rounds would lose per-call attribution.
        observer.CompletedRawUsages.Should().HaveCount(2);
        observer.CompletedRawUsages[0].Should().NotBeNull();
        observer.CompletedRawUsages[0]!.Value.GetProperty("input_tokens").GetInt32().Should().Be(11);
        observer.CompletedRawUsages[0]!.Value.GetProperty("output_tokens").GetInt32().Should().Be(3);
        observer.CompletedRawUsages[1].Should().NotBeNull();
        observer.CompletedRawUsages[1]!.Value.GetProperty("output_tokens_details")
            .GetProperty("reasoning_tokens").GetInt32().Should().Be(1);
    }

    private sealed class RecordingInvocationObserver : IInvocationObserver
    {
        public List<Guid> StartedInvocationIds { get; } = new();
        public List<Guid> CompletedInvocationIds { get; } = new();

        public Task OnModelCallStartedAsync(Guid invocationId, int roundNumber, CancellationToken cancellationToken)
        {
            StartedInvocationIds.Add(invocationId);
            return Task.CompletedTask;
        }

        public List<string> CompletedProviders { get; } = new();
        public List<string> CompletedModels { get; } = new();
        public List<JsonElement?> CompletedRawUsages { get; } = new();

        public Task OnModelCallCompletedAsync(
            Guid invocationId,
            int roundNumber,
            ChatMessage responseMessage,
            TokenUsage? callTokenUsage,
            TokenUsage? cumulativeTokenUsage,
            string provider,
            string model,
            JsonElement? rawUsage,
            CancellationToken cancellationToken)
        {
            CompletedInvocationIds.Add(invocationId);
            CompletedProviders.Add(provider);
            CompletedModels.Add(model);
            CompletedRawUsages.Add(rawUsage);
            return Task.CompletedTask;
        }

        public Task OnToolCallStartedAsync(ToolCall call, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnToolCallCompletedAsync(ToolCall call, ToolResult result, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Test-only IToolProvider exposing one host tool that calls
    /// <see cref="ToolExecutionContext.StageWorkflowBagWrite"/> with a fixed
    /// repositories[] array. Used to exercise the InvocationLoop's sink wiring without
    /// pulling in the full SetupWorkspaceHostToolService fixture.
    /// </summary>
    private sealed class SinkUsingToolProvider(string toolName) : IToolProvider
    {
        private readonly string toolName = toolName;

        public ToolCategory Category => ToolCategory.Host;

        public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy) =>
            [new ToolSchema(toolName, "Stages a workflow.repositories write via the sink.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
            }, IsMutating: true)];

        public Task<ToolResult> InvokeAsync(
            ToolCall toolCall,
            CancellationToken cancellationToken = default,
            ToolExecutionContext? context = null)
        {
            var sink = context?.StageWorkflowBagWrite
                ?? throw new InvalidOperationException("Sink not wired — InvocationLoop must populate StageWorkflowBagWrite when invoking host tools.");
            using var doc = JsonDocument.Parse("[{\"url\":\"https://example.invalid/owner/repo\",\"branch\":\"main\"}]");
            sink("repositories", doc.RootElement.Clone());
            return Task.FromResult(new ToolResult(toolCall.Id, "{\"ok\":true}"));
        }
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
