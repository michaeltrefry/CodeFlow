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
