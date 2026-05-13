using System.Text.Json.Nodes;
using CodeFlow.Runtime.Goal;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Goal;

/// <summary>
/// Epic 978 / GN-2 — end-to-end coverage that <see cref="Agent.BuildProviders"/> only surfaces
/// the goal.* tools when <see cref="AgentInvocationConfiguration.GoalState"/> is non-null. These
/// are the tests that catch the "tools leaked into homepage assistant" failure mode.
/// </summary>
public sealed class GoalAgentIntegrationTests
{
    [Fact]
    public async Task Agent_WithGoalState_AllowsGoalUpdateToolCall()
    {
        var state = new RecordingState();

        var modelClient = new ScriptedModelClient(
        [
            request =>
            {
                // First turn: the agent should see goal.get + goal.update in its tool catalog,
                // alongside any other tools. We don't strictly verify the catalog here (that's
                // covered by GoalHostToolProviderTests); instead we exercise the dispatch path
                // by emitting a tool_use for goal.update.
                request.Tools.Should().Contain(t => t.Name == "goal.get");
                request.Tools.Should().Contain(t => t.Name == "goal.update");

                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Audit complete, marking goal done.",
                        ToolCalls:
                        [
                            new ToolCall(
                                "call_update",
                                "goal.update",
                                new JsonObject { ["status"] = "complete" })
                        ]),
                    InvocationStopReason.ToolCalls);
            },
            request =>
            {
                // Second turn: tool result was acknowledged; close the turn.
                var toolMessage = request.Messages[^1];
                toolMessage.Role.Should().Be(ChatMessageRole.Tool);
                toolMessage.ToolCallId.Should().Be("call_update");
                toolMessage.Content.Should().Contain("acknowledged");

                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "Done."),
                    InvocationStopReason.EndTurn);
            }
        ]);

        var agent = new Agent(new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]));

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "test",
                Model: "goal-model",
                GoalState: state),
            "Pursue the objective.",
            ResolvedAgentTools.Empty);

        result.ToolCallsExecuted.Should().Be(1);
        state.MarkCompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task Agent_WithoutGoalState_DoesNotExposeGoalTools()
    {
        // Mirror of the test above, but with GoalState left null. The model client asserts that
        // goal.* tools are absent from its catalog — this is the leak-prevention guarantee.
        var modelClient = new ScriptedModelClient(
        [
            request =>
            {
                request.Tools.Should().NotContain(t => t.Name.StartsWith("goal."));

                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "No goal tools available."),
                    InvocationStopReason.EndTurn);
            }
        ]);

        var agent = new Agent(new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]));

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "test",
                Model: "regular-agent-model"),
            "Just a regular agent invocation.",
            ResolvedAgentTools.Empty);

        result.Output.Should().Be("No goal tools available.");
    }

    [Fact]
    public async Task Agent_WithGoalStateAndExistingAllowlist_AllowsGoalToolsThrough()
    {
        // When the role's resolved allowlist is non-empty (i.e. a strict allowlist exists), the
        // goal.* tools must still pass through — they are runtime-managed meta-tools that the
        // executor injects, not author-grantable. This mirrors the spawn_subagent pattern in
        // Agent.MergeToolAccessPolicy.
        var state = new RecordingState();

        var modelClient = new ScriptedModelClient(
        [
            request =>
            {
                // The catalog the model sees must include goal.* even though the role's
                // AllowedToolNames did not list them.
                request.Tools.Should().Contain(t => t.Name == "goal.update");

                return new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Marking done.",
                        ToolCalls:
                        [
                            new ToolCall("call_update", "goal.update", new JsonObject { ["status"] = "complete" })
                        ]),
                    InvocationStopReason.ToolCalls);
            },
            _ => new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "Done."),
                InvocationStopReason.EndTurn)
        ]);

        var agent = new Agent(new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]));

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "test",
                Model: "goal-model",
                GoalState: state),
            "Pursue the objective.",
            ResolvedAgentTools.Empty with
            {
                AllowedToolNames = new[] { "some_other_tool" }, // strict allowlist, no goal.* listed
            });

        result.ToolCallsExecuted.Should().Be(1);
        state.MarkCompleteCalls.Should().Be(1);
    }

    private sealed class RecordingState : IGoalRuntimeState
    {
        public int MarkCompleteCalls { get; private set; }
        public int MarkAbandonedCalls { get; private set; }
        public string? LastAbandonReason { get; private set; }

        public GoalRuntimeStateSnapshot Snapshot() => new(
            Objective: "Test objective",
            TokenBudget: 100_000,
            TokensUsed: 250,
            TokensRemaining: 99_750,
            IsCompleteRequested: MarkCompleteCalls > 0,
            IsAbandonRequested: MarkAbandonedCalls > 0,
            AbandonReason: LastAbandonReason);

        public void MarkComplete() => MarkCompleteCalls++;

        public void MarkAbandoned(string reason)
        {
            MarkAbandonedCalls++;
            LastAbandonReason = reason;
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
}
