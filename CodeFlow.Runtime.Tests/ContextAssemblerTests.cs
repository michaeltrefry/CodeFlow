using System.Text.Json;
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
    public void Assemble_ShouldAppendOutputFormatBlock_WhenDeclaredOutputsHavePayloadExamples()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are a release reviewer.",
            PromptTemplate: null,
            Input: null,
            DeclaredOutputs: new[]
            {
                new AgentOutputDeclaration(
                    "Rejected",
                    "Draft cannot ship",
                    ParseJson("""{"reasons":["..."]}""")),
                new AgentOutputDeclaration(
                    "Completed",
                    null,
                    ParseJson("""{"summary":"..."}"""))
            }));

        messages.Should().ContainSingle();
        var system = messages[0];
        system.Role.Should().Be(ChatMessageRole.System);
        system.Content.Should().StartWith("You are a release reviewer.");
        system.Content.Should().Contain("## Response format");
        system.Content.Should().Contain("### Rejected — Draft cannot ship");
        system.Content.Should().Contain("### Completed");
        system.Content.Should().Contain("\"reasons\"");
        system.Content.Should().Contain("\"summary\"");
        system.Content.Should().Contain("```json");
    }

    [Fact]
    public void Assemble_ShouldOmitOutputsWithoutPayloadExample_FromFormatBlock()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "x",
            PromptTemplate: null,
            Input: null,
            DeclaredOutputs: new[]
            {
                new AgentOutputDeclaration("Completed", null, ParseJson("""{"ok":true}""")),
                new AgentOutputDeclaration("Failed", null, null)
            }));

        messages[0].Content.Should().Contain("### Completed");
        messages[0].Content.Should().NotContain("### Failed");
    }

    [Fact]
    public void Assemble_ShouldNotEmitFormatBlock_WhenNoDeclaredOutputHasPayloadExample()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are helpful.",
            PromptTemplate: null,
            Input: null,
            DeclaredOutputs: new[]
            {
                new AgentOutputDeclaration("Completed", null, null),
                new AgentOutputDeclaration("Failed", null, null)
            }));

        messages.Should().ContainSingle();
        messages[0].Content.Should().NotContain("## Response format");
    }

    [Fact]
    public void Assemble_ShouldNotEmitFormatBlock_WhenDeclaredOutputsIsNull()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are helpful.",
            PromptTemplate: null,
            Input: null));

        messages[0].Content.Should().NotContain("## Response format");
    }

    [Fact]
    public void Assemble_ShouldEmitFormatBlockAfterSkillsBlock_WhenBothPresent()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are a reviewer.",
            PromptTemplate: null,
            Input: null,
            Skills: new[] { new ResolvedSkill("guide", "Body text.") },
            DeclaredOutputs: new[]
            {
                new AgentOutputDeclaration("Completed", null, ParseJson("""{"ok":true}"""))
            }));

        var content = messages[0].Content;
        var skillsIndex = content.IndexOf("## Skills", StringComparison.Ordinal);
        var formatIndex = content.IndexOf("## Response format", StringComparison.Ordinal);
        skillsIndex.Should().BeGreaterThan(0);
        formatIndex.Should().BeGreaterThan(skillsIndex);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Assemble_ShouldRenderConditionalBranch_OnIsLastRoundBoolean()
    {
        var template =
            "{{ if isLastRound }}Ship it now.{{ else }}Round {{ round }} of {{ maxRounds }}.{{ end }}";

        var lastRound = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: template,
            Input: null,
            Variables: new Dictionary<string, string?>
            {
                ["round"] = "3",
                ["maxRounds"] = "3",
                ["isLastRound"] = "true"
            })).Single();

        lastRound.Content.Should().Be("Ship it now.");

        var earlyRound = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: template,
            Input: null,
            Variables: new Dictionary<string, string?>
            {
                ["round"] = "1",
                ["maxRounds"] = "3",
                ["isLastRound"] = "false"
            })).Single();

        earlyRound.Content.Should().Be("Round 1 of 3.");
    }

    [Fact]
    public void Assemble_ShouldIterateOverJsonArray_FromContextVariables()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: "{{ for item in context.items }}- {{ item }}\n{{ end }}",
            Input: null,
            Variables: new Dictionary<string, string?>
            {
                ["context.items"] = "[\"alpha\",\"bravo\",\"charlie\"]",
                ["context.items.0"] = "alpha",
                ["context.items.1"] = "bravo",
                ["context.items.2"] = "charlie"
            }));

        messages.Single().Content.Should().Be("- alpha\n- bravo\n- charlie");
    }

    [Fact]
    public void Assemble_ShouldLeaveUnresolvedLegacyPlaceholdersAsLiterals_WhenRenderedThroughScriban()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: "Hello {{ unresolved }} and {{ also.missing }}.",
            Input: null,
            Variables: new Dictionary<string, string?>
            {
                ["greeting"] = "Hi"
            }));

        messages.Single().Content.Should().Be("Hello {{ unresolved }} and {{ also.missing }}.");
    }

    [Fact]
    public void Assemble_ShouldThrowReadableError_WhenTemplateHasSyntaxError()
    {
        var act = () => assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: "{{ if isLastRound }}missing end",
            Input: null,
            Variables: new Dictionary<string, string?>
            {
                ["isLastRound"] = "true"
            }));

        act.Should().Throw<PromptTemplateException>()
            .WithMessage("Prompt template has syntax errors*");
    }

    [Fact]
    public void Assemble_ShouldTerminatePathologicalTemplate_WithinSandboxBudget()
    {
        // Scriban's LoopLimit + wall-clock CancellationToken cooperate to abort runaway templates.
        var template = "{{ for i in 1..999999999 }}{{ i }} {{ end }}";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var act = () => assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: template,
            Input: null));

        act.Should().Throw<PromptTemplateException>();
        stopwatch.Stop();
        // Generous ceiling — render budget is 100ms and LoopLimit=1000 caps iterations almost instantly.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Assemble_ShouldResolveNestedPath_WhenParentAndChildrenAreProvided()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: "{{ context.target.repo }} / {{ context.target.branch }}",
            Input: null,
            Variables: new Dictionary<string, string?>
            {
                ["context.target"] = "{\"repo\":\"codeflow\",\"branch\":\"main\"}",
                ["context.target.repo"] = "codeflow",
                ["context.target.branch"] = "main"
            }));

        messages.Single().Content.Should().Be("codeflow / main");
    }

    [Fact]
    public void Assemble_ShouldRenderInputPathFromJsonInput_InsideConditional()
    {
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: null,
            PromptTemplate: "{{ if input.priority == \"high\" }}URGENT: {{ input.title }}{{ else }}{{ input.title }}{{ end }}",
            Input: null,
            Variables: new Dictionary<string, string?>
            {
                ["input.priority"] = "high",
                ["input.title"] = "Review the draft"
            }));

        messages.Single().Content.Should().Be("URGENT: Review the draft");
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
