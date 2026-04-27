using FluentAssertions;
using Scriban.Runtime;

namespace CodeFlow.Runtime.Tests;

/// <summary>
/// F3 unit tests for the partial-aware overload of <see cref="ScribanTemplateRenderer"/>.
/// Validates that <c>{{ include "key" }}</c> resolves against the supplied dictionary, leaves
/// the no-partials path unchanged, and surfaces missing pins as a structured prompt-template
/// error.
/// </summary>
public sealed class ScribanPartialTemplateTests
{
    private static ScriptObject EmptyScope() => new();

    [Fact]
    public void Render_WithPartial_ResolvesIncludeToBody()
    {
        var renderer = new ScribanTemplateRenderer();
        var partials = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@codeflow/reviewer-base"] = "Approve unless there is a critical gap."
        };

        var output = renderer.Render(
            "Reviewer rules:\n{{ include \"@codeflow/reviewer-base\" }}",
            EmptyScope(),
            partials);

        output.Should().Contain("Approve unless there is a critical gap.");
    }

    [Fact]
    public void Render_NoPartials_PreservesLegacyNoLoaderBehavior()
    {
        // Existing call sites (no partials param) must keep working unchanged. CR1 — agents
        // without partial pins render identically to pre-F3.
        var renderer = new ScribanTemplateRenderer();

        var output = renderer.Render("Hello, {{ name }}.", BuildScope(("name", "world")));

        output.Should().Be("Hello, world.");
    }

    [Fact]
    public void Render_PartialDictPresentButNotReferenced_RendersTemplateUnchanged()
    {
        // Having partials available shouldn't perturb a template that doesn't include them.
        var renderer = new ScribanTemplateRenderer();
        var partials = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@codeflow/unused"] = "This should not appear."
        };

        var output = renderer.Render(
            "Direct content only.",
            EmptyScope(),
            partials);

        output.Should().Be("Direct content only.");
    }

    [Fact]
    public void Render_UnknownPartialInclude_RaisesPromptTemplateException()
    {
        var renderer = new ScribanTemplateRenderer();
        var partials = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@codeflow/exists"] = "OK"
        };

        var act = () => renderer.Render(
            "{{ include \"@codeflow/missing\" }}",
            EmptyScope(),
            partials);

        act.Should().Throw<PromptTemplateException>()
            .WithMessage("*missing*");
    }

    [Fact]
    public void Render_DifferentPartialBodies_ProduceDifferentOutputs()
    {
        // Bumping a partial's body must change downstream renders only when the new version is
        // supplied. Two renders with different version-bodies prove the loader routes through
        // the dict per-call.
        var renderer = new ScribanTemplateRenderer();
        var template = "Header\n{{ include \"@codeflow/footer\" }}";
        var v1 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@codeflow/footer"] = "v1 footer"
        };
        var v2 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@codeflow/footer"] = "v2 footer"
        };

        var rendered1 = renderer.Render(template, EmptyScope(), v1);
        var rendered2 = renderer.Render(template, EmptyScope(), v2);

        rendered1.Should().Contain("v1 footer");
        rendered2.Should().Contain("v2 footer");
        rendered1.Should().NotBe(rendered2);
    }

    private static ScriptObject BuildScope(params (string Key, string Value)[] entries)
    {
        var scope = new ScriptObject();
        foreach (var (key, value) in entries)
        {
            scope[key] = value;
        }
        return scope;
    }
}
