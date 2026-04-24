using FluentAssertions;
using Scriban.Runtime;

namespace CodeFlow.Runtime.Tests;

public sealed class ScribanTemplateRendererTests
{
    private readonly ScribanTemplateRenderer renderer = new();

    [Fact]
    public void Render_ShouldSubstituteScalarVariables()
    {
        var scope = new ScriptObject
        {
            ["decision"] = "Approved",
            ["output"] = "shipped"
        };

        var result = renderer.Render("[{{ decision }}] {{ output }}", scope);

        result.Should().Be("[Approved] shipped");
    }

    [Fact]
    public void Render_ShouldSupportConditionals()
    {
        var scope = new ScriptObject { ["decision"] = "Approved" };

        var result = renderer.Render(
            "{{ if decision == \"Approved\" }}OK{{ else }}NO{{ end }}",
            scope);

        result.Should().Be("OK");
    }

    [Fact]
    public void Render_ShouldThrowPromptTemplateException_WhenTemplateIsMalformed()
    {
        var scope = new ScriptObject();

        var act = () => renderer.Render("{{ if unterminated", scope);

        act.Should()
            .Throw<PromptTemplateException>()
            .WithMessage("*syntax errors*");
    }

    [Fact]
    public void Render_ShouldThrowPromptTemplateException_WhenSandboxBudgetExceeded()
    {
        var scope = new ScriptObject();
        // Exceed LoopLimit (1000) to abort under the sandbox rather than spinning forever.
        const string runawayLoopTemplate = "{{ for i in 0..5000 }}x{{ end }}";

        var act = () => renderer.Render(runawayLoopTemplate, scope);

        act.Should()
            .Throw<PromptTemplateException>();
    }

    [Fact]
    public void Render_ShouldHonourExternalCancellation()
    {
        var scope = new ScriptObject();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => renderer.Render("{{ for i in 0..100 }}x{{ end }}", scope, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Render_ShouldLeaveUnresolvedVariablesAsEmpty()
    {
        // Scriban with StrictVariables=false renders unresolved expressions as empty strings,
        // which matches the behaviour prompt templates already rely on.
        var scope = new ScriptObject();

        var result = renderer.Render("before {{ missing }} after", scope);

        result.Should().Be("before  after");
    }

    [Fact]
    public void Render_ShouldThrow_WhenTemplateIsNull()
    {
        var scope = new ScriptObject();

        var act = () => renderer.Render(null!, scope);

        act.Should().Throw<ArgumentNullException>();
    }
}
