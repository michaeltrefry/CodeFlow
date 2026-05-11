using CodeFlow.Orchestration;
using CodeFlow.Runtime;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests;

public sealed class AgentPromptScopeBuilderTests
{
    [Fact]
    public void BuildBudgetVariables_NullBudget_FallsBackToInvocationLoopBudgetDefault()
    {
        var vars = AgentPromptScopeBuilder.BuildBudgetVariables(null);

        // Prompts should always see a concrete number rather than an empty variable, so when
        // an agent doesn't override the runtime budget the builder mirrors InvocationLoopBudget.Default.
        vars["maxToolCalls"].Should().Be(InvocationLoopBudget.Default.MaxToolCalls.ToString());
        vars["maxConsecutiveNonMutatingCalls"].Should().Be(InvocationLoopBudget.Default.MaxConsecutiveNonMutatingCalls.ToString());
        vars["maxLoopDurationSeconds"].Should().Be(((long)InvocationLoopBudget.Default.MaxLoopDuration.TotalSeconds).ToString());
        vars["softWarnRemaining"].Should().Be(InvocationLoopBudget.Default.SoftWarnRemaining.ToString());
        vars["hardWarnRemaining"].Should().Be(InvocationLoopBudget.Default.HardWarnRemaining.ToString());
    }

    [Fact]
    public void BuildBudgetVariables_OverriddenBudget_SurfacesConfiguredValues()
    {
        // Mirrors a typical author-configured developer agent that bumps MaxToolCalls well
        // above the platform default — the prompt needs to see 50, not 16, so its "70% of
        // allowed tool calls" math grounds in the right number.
        var budget = new InvocationLoopBudget
        {
            MaxToolCalls = 50,
            MaxConsecutiveNonMutatingCalls = 25,
            MaxLoopDuration = TimeSpan.FromMinutes(30),
            SoftWarnRemaining = 5,
            HardWarnRemaining = 2,
        };

        var vars = AgentPromptScopeBuilder.BuildBudgetVariables(budget);

        vars["maxToolCalls"].Should().Be("50");
        vars["maxConsecutiveNonMutatingCalls"].Should().Be("25");
        vars["maxLoopDurationSeconds"].Should().Be("1800");
        vars["softWarnRemaining"].Should().Be("5");
        vars["hardWarnRemaining"].Should().Be("2");
    }

    [Fact]
    public void BuildAll_IncludesBudgetVariables_AlongsideContextWorkflowAndInput()
    {
        var budget = new InvocationLoopBudget { MaxToolCalls = 32 };

        var vars = AgentPromptScopeBuilder.BuildAll(
            workflow: null,
            context: null,
            reviewRound: null,
            reviewMaxRounds: null,
            input: null,
            budget: budget);

        vars.Should().ContainKey("maxToolCalls");
        vars["maxToolCalls"].Should().Be("32");
    }
}
