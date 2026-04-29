using System.Text.Json;
using CodeFlow.Orchestration;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests;

public sealed class AgentOutputTransformsTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyVars =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void NormalizeMirrorTarget_TrimsAndReturnsKey()
    {
        AgentOutputTransforms.NormalizeMirrorTarget("  spec  ").Should().Be("spec");
    }

    [Fact]
    public void NormalizeMirrorTarget_NullOrWhitespace_ReturnsNull()
    {
        AgentOutputTransforms.NormalizeMirrorTarget(null).Should().BeNull();
        AgentOutputTransforms.NormalizeMirrorTarget(" ").Should().BeNull();
    }

    [Fact]
    public void NormalizeMirrorTarget_ReservedNamespace_ReturnsNull()
    {
        AgentOutputTransforms.NormalizeMirrorTarget("__loop.round").Should().BeNull();
    }

    [Fact]
    public void NormalizePortReplacements_TrimsAndDropsBlanks()
    {
        var result = AgentOutputTransforms.NormalizePortReplacements(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [" Approved "] = " spec ",
                ["Failed"] = "  ",
                [""] = "ignored",
            });

        result.Should().NotBeNull();
        result!.Should().ContainKey("Approved").WhoseValue.Should().Be("spec");
        result.Should().NotContainKey("Failed");
    }

    [Fact]
    public void NormalizePortReplacements_AllBlank_ReturnsNull()
    {
        AgentOutputTransforms.NormalizePortReplacements(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Failed"] = "  ",
            }).Should().BeNull();
        AgentOutputTransforms.NormalizePortReplacements(null).Should().BeNull();
    }

    [Fact]
    public void Mirror_AddsKeyAsJsonString_LeavesOriginalUnchanged()
    {
        var original = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["existing"] = Json("\"keep\""),
        };

        var mirrored = AgentOutputTransforms.Mirror(original, "spec", "value");

        mirrored.Should().ContainKey("existing");
        mirrored["spec"].GetString().Should().Be("value");
        original.Should().NotContainKey("spec", "Mirror must not mutate caller's bag");
    }

    [Fact]
    public void TryGetPortReplacement_StringValue_ReturnsRawString()
    {
        var vars = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["spec"] = Json("\"hello\""),
        };
        var binding = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "spec",
        };

        AgentOutputTransforms.TryGetPortReplacement(binding, "Approved", vars).Should().Be("hello");
    }

    [Fact]
    public void TryGetPortReplacement_StructuredValue_ReturnsRawJson()
    {
        var vars = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["spec"] = Json("{\"a\":1}"),
        };
        var binding = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "spec",
        };

        AgentOutputTransforms.TryGetPortReplacement(binding, "Approved", vars)
            .Should().Be("{\"a\":1}");
    }

    [Fact]
    public void TryGetPortReplacement_PortNotBound_ReturnsNull()
    {
        var binding = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "spec",
        };

        AgentOutputTransforms.TryGetPortReplacement(binding, "Rejected", EmptyVars).Should().BeNull();
    }

    [Fact]
    public void TryGetPortReplacement_BoundVariableMissing_ReturnsNullFailSafe()
    {
        var binding = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "spec",
        };

        AgentOutputTransforms.TryGetPortReplacement(binding, "Approved", EmptyVars).Should().BeNull();
    }

    [Fact]
    public void TryGetPortReplacement_NullValue_ReturnsNullFailSafe()
    {
        var vars = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["spec"] = Json("null"),
        };
        var binding = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "spec",
        };

        AgentOutputTransforms.TryGetPortReplacement(binding, "Approved", vars).Should().BeNull();
    }

    [Fact]
    public void ResolvePortReplacement_ReturnsTextAndBoundVariableName()
    {
        var vars = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["spec"] = Json("\"hello\""),
        };
        var binding = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "spec",
        };

        var (text, varName) = AgentOutputTransforms.ResolvePortReplacement(binding, "Approved", vars);
        text.Should().Be("hello");
        varName.Should().Be("spec");
    }
}
