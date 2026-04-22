using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

public sealed class ContextAssemblerTests
{
    private readonly ContextAssembler assembler = new();

    [Fact]
    public void Assemble_ShouldReturnSystemMessageOnly_WhenOnlySystemPromptIsProvided()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are a helpful assistant.",
            PromptTemplate: null,
            Input: null));

        messages.Should().Equal(
            new ChatMessage(ChatMessageRole.System, "You are a helpful assistant."));
    }

    [Fact]
    public void Assemble_ShouldAppendInputAfterPromptTemplate_WhenTemplateDoesNotConsumeInput()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: "Summarize the following text.",
            Input: "CodeFlow routes work over RabbitMQ."));

        messages.Should().Equal(
            new ChatMessage(
                ChatMessageRole.User,
                $"Summarize the following text.{Environment.NewLine}{Environment.NewLine}CodeFlow routes work over RabbitMQ."));
    }

    [Fact]
    public void Assemble_ShouldSubstituteTemplateVariables()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: "Review {{artifact}} for the {{audience}} team.{0}{0}{{input}}".Replace("{0}", Environment.NewLine),
            Input: "The latest workflow draft is attached.",
            Variables: new Dictionary<string, string?>
            {
                ["artifact"] = "workflow-spec.md",
                ["audience"] = "operations"
            }));

        messages.Should().Equal(
            new ChatMessage(
                ChatMessageRole.User,
                $"Review workflow-spec.md for the operations team.{Environment.NewLine}{Environment.NewLine}The latest workflow draft is attached."));
    }

    [Fact]
    public void Assemble_ShouldTreatInputPathVariablesAsConsumingInput()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: "Summarize {{input.summary}} for {{context.target.path}}",
            Input: """{"summary":"Build a blog"}""",
            Variables: new Dictionary<string, string?>
            {
                ["input.summary"] = "Build a blog",
                ["context.target.path"] = "/repos/blogger"
            }));

        messages.Should().Equal(
            new ChatMessage(
                ChatMessageRole.User,
                "Summarize Build a blog for /repos/blogger"));
    }

    [Fact]
    public void Assemble_ShouldKeepRawInputAvailableForPlainText_WhenInputPathVariablesAreUnresolved()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: "Raw: {{input}}\nField: {{input.summary}}",
            Input: "Create a new blog website using .NET 10 and React."));

        messages.Should().Equal(
            new ChatMessage(
                ChatMessageRole.User,
                "Raw: Create a new blog website using .NET 10 and React.\nField: {{input.summary}}"));
    }

    [Fact]
    public void Assemble_ShouldPrependRetryNote_WhenRetryContextProvided()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are a release reviewer.",
            PromptTemplate: null,
            Input: "Review this draft.",
            RetryContext: new RetryContext(
                AttemptNumber: 2,
                PriorFailureReason: "tool_call_budget_exceeded",
                PriorAttemptSummary: "Last output: attempted a large refactor. Tool calls executed: 10")));

        messages.Should().HaveCount(3);
        messages[0].Should().Be(new ChatMessage(ChatMessageRole.System, "You are a release reviewer."));
        messages[1].Role.Should().Be(ChatMessageRole.System);
        messages[1].Content.Should().Contain("This is attempt #2.");
        messages[1].Content.Should().Contain("Prior attempt #1 failed");
        messages[1].Content.Should().Contain("tool_call_budget_exceeded");
        messages[1].Content.Should().Contain("Summary of prior attempt");
        messages[1].Content.Should().Contain("Tool calls executed: 10");
        messages[1].Content.Should().Contain("Address the prior failure");
        messages[2].Role.Should().Be(ChatMessageRole.User);
    }

    [Fact]
    public void Assemble_ShouldAppendSkillsBlockToSystemMessage_AndRenderSkillVariables()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are a product designer.",
            PromptTemplate: null,
            Input: null,
            Variables: new Dictionary<string, string?>
            {
                ["analysis.summary"] = "users confused about onboarding",
                ["conversationSummary"] = "2 rounds so far",
            },
            Skills: new[]
            {
                new ResolvedSkill(
                    "socratic-interview",
                    """
                    ## Opening
                    Ask exactly one question.

                    ## Section: Current Understanding
                    {{analysis.summary}}

                    ## Section: Conversation So Far
                    {{conversationSummary}}
                    """),
            }));

        messages.Should().ContainSingle();
        var system = messages[0];
        system.Role.Should().Be(ChatMessageRole.System);
        system.Content.Should().StartWith("You are a product designer.");
        system.Content.Should().Contain("## Skills");
        system.Content.Should().Contain("### socratic-interview");
        system.Content.Should().Contain("users confused about onboarding");
        system.Content.Should().Contain("2 rounds so far");
        system.Content.Should().NotContain("{{analysis.summary}}");
        system.Content.Should().NotContain("{{conversationSummary}}");
    }

    [Fact]
    public void Assemble_ShouldLeaveUnknownSkillVariablesAsLiteralPlaceholders()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "x",
            PromptTemplate: null,
            Input: null,
            Skills: new[]
            {
                new ResolvedSkill("greeter", "Hello {{who}}"),
            }));

        messages[0].Content.Should().Contain("Hello {{who}}");
    }

    [Fact]
    public void Assemble_ShouldNotEmitSkillsBlock_WhenSkillsListIsEmpty()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are helpful.",
            PromptTemplate: null,
            Input: null,
            Skills: Array.Empty<ResolvedSkill>()));

        messages.Should().ContainSingle();
        messages[0].Content.Should().NotContain("## Skills");
    }

    [Fact]
    public void Assemble_ShouldEmitSystemMessageFromSkillsAlone_WhenNoSystemPromptIsConfigured()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: null,
            Input: null,
            Skills: new[] { new ResolvedSkill("s", "body") }));

        messages.Should().ContainSingle();
        messages[0].Role.Should().Be(ChatMessageRole.System);
        messages[0].Content.Should().StartWith("## Skills");
        messages[0].Content.Should().Contain("### s");
        messages[0].Content.Should().Contain("body");
    }

    [Fact]
    public void Assemble_ShouldCarryForwardHistoryBeforeAppendingNextUserTurn()
    {
        var history = new[]
        {
            new ChatMessage(ChatMessageRole.User, "What happened on the previous run?"),
            new ChatMessage(ChatMessageRole.Assistant, "The reviewer rejected the draft.")
        };

        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are a release reviewer.",
            PromptTemplate: "Ask one targeted follow-up question.",
            Input: "Focus on the failed validation step.",
            History: history));

        messages.Should().Equal(
            new ChatMessage(ChatMessageRole.System, "You are a release reviewer."),
            history[0],
            history[1],
            new ChatMessage(
                ChatMessageRole.User,
                $"Ask one targeted follow-up question.{Environment.NewLine}{Environment.NewLine}Focus on the failed validation step."));
    }
}
