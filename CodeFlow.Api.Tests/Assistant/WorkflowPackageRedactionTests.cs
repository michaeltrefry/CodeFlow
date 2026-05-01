using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// CE-3 — verify the workflow-package <c>tool_use.input</c> redactor strips the giant package
/// JSON while preserving an anchor the model can still reason about, and leaves non-redactable
/// tools / draft-path calls / non-object packages untouched.
/// </summary>
public sealed class WorkflowPackageRedactionTests
{
    private const string MultiNodePackageJson = """
        {
          "package": {
            "entryPoint": { "key": "demo", "version": "1.0.0" },
            "workflows": [
              { "key": "demo", "version": "1.0.0", "nodes": [ {"id":"a"}, {"id":"b"}, {"id":"c"} ] },
              { "key": "demo-helper", "version": "1.0.0", "nodes": [ {"id":"x"} ] }
            ],
            "agents": [ {"key":"reviewer"}, {"key":"writer"} ],
            "roles": [ {"key":"qa"} ]
          }
        }
        """;

    [Theory]
    [InlineData("set_workflow_package_draft", true)]
    [InlineData("save_workflow_package", true)]
    [InlineData("get_workflow_package_draft", false)]
    [InlineData("patch_workflow_package_draft", false)]
    [InlineData("read_file", false)]
    public void IsRedactableTool_ReportsExpectedSet(string tool, bool expected)
    {
        WorkflowPackageRedaction.IsRedactableTool(tool).Should().Be(expected);
    }

    [Fact]
    public void RedactArgs_NotRedactableTool_ReturnsOriginal()
    {
        var original = JsonDocument.Parse(MultiNodePackageJson).RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("read_file", original);

        redacted.GetRawText().Should().Be(original.GetRawText());
    }

    [Fact]
    public void RedactArgs_NoPackageArg_ReturnsOriginal()
    {
        // The draft path of save_workflow_package — no `package` argument, reads from disk.
        // Redaction must NOT touch this case.
        var original = JsonDocument.Parse("""{"comment": "from draft"}""").RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("save_workflow_package", original);

        redacted.GetRawText().Should().Be(original.GetRawText());
    }

    [Fact]
    public void RedactArgs_PackageIsNotObject_ReturnsOriginal()
    {
        var original = JsonDocument.Parse("""{"package": "string-not-object"}""").RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("set_workflow_package_draft", original);

        redacted.GetRawText().Should().Be(original.GetRawText());
    }

    [Fact]
    public void RedactArgs_FullPackage_RedactsAndPreservesAnchor()
    {
        var original = JsonDocument.Parse(MultiNodePackageJson).RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("set_workflow_package_draft", original);

        // Top-level shape: `package` key still present, but its value is the anchor object.
        redacted.ValueKind.Should().Be(JsonValueKind.Object);
        var pkg = redacted.GetProperty("package");
        pkg.GetProperty("_redacted").GetBoolean().Should().BeTrue();

        // sha256: 64 lowercase hex chars matching the SHA-256 of the original `package` value bytes.
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(original.GetProperty("package").GetRawText())));
        pkg.GetProperty("sha256").GetString().Should().Be(expectedSha);

        // sizeBytes: byte count of the original package JSON.
        var expectedSize = Encoding.UTF8.GetByteCount(original.GetProperty("package").GetRawText());
        pkg.GetProperty("sizeBytes").GetInt32().Should().Be(expectedSize);

        // Summary: counts that let the model anchor its later reasoning.
        var summary = pkg.GetProperty("summary");
        summary.GetProperty("workflowCount").GetInt32().Should().Be(2);
        summary.GetProperty("nodeCount").GetInt32().Should().Be(4);  // 3 + 1
        summary.GetProperty("agentCount").GetInt32().Should().Be(2);
        summary.GetProperty("roleCount").GetInt32().Should().Be(1);
        summary.GetProperty("entryPoint").GetProperty("key").GetString().Should().Be("demo");
        summary.GetProperty("entryPoint").GetProperty("version").GetString().Should().Be("1.0.0");
    }

    [Fact]
    public void RedactArgs_RedactedPayloadIsSmallerThanOriginal()
    {
        // The whole point: the redacted form should be drastically smaller. If it isn't, we've
        // failed to deliver token-count savings, and the test surfaces that regression.
        var original = JsonDocument.Parse(MultiNodePackageJson).RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("set_workflow_package_draft", original);

        var originalBytes = Encoding.UTF8.GetByteCount(original.GetRawText());
        var redactedBytes = Encoding.UTF8.GetByteCount(redacted.GetRawText());
        redactedBytes.Should().BeLessThan(originalBytes);
    }

    [Fact]
    public void RedactArgs_PreservesOtherTopLevelArgs()
    {
        // If the inline path of save_workflow_package ever grows additional sibling args, they
        // should pass through untouched. Only `package` is rewritten.
        var json = """
            {
              "package": {"workflows": [], "agents": []},
              "comment": "for the changelog",
              "force": true
            }
            """;
        var original = JsonDocument.Parse(json).RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("save_workflow_package", original);

        redacted.GetProperty("comment").GetString().Should().Be("for the changelog");
        redacted.GetProperty("force").GetBoolean().Should().BeTrue();
        redacted.GetProperty("package").GetProperty("_redacted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void RedactArgsAsDictionary_ReturnsAnthropicShapeDictionary()
    {
        var original = JsonDocument.Parse(MultiNodePackageJson).RootElement;

        var dict = WorkflowPackageRedaction.RedactArgsAsDictionary("set_workflow_package_draft", original);

        dict.Should().ContainKey("package");
        dict["package"].GetProperty("_redacted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void RedactArgsAsDictionary_NotRedactableTool_ReturnsVerbatimDictionary()
    {
        var original = JsonDocument.Parse(MultiNodePackageJson).RootElement;

        var dict = WorkflowPackageRedaction.RedactArgsAsDictionary("read_file", original);

        dict["package"].GetRawText().Should().Be(original.GetProperty("package").GetRawText());
    }

    [Fact]
    public void RedactArgsAsJsonString_ReturnsValidJson()
    {
        var original = JsonDocument.Parse(MultiNodePackageJson).RootElement;

        var json = WorkflowPackageRedaction.RedactArgsAsJsonString("set_workflow_package_draft", original);

        // Round-trip: parses back into a JSON object with the redacted shape.
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("package").GetProperty("_redacted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void RedactArgs_NonObjectInput_ReturnsOriginal()
    {
        // Defensive: the model could (in theory) emit invalid args. The redactor must not throw.
        var original = JsonDocument.Parse("\"not-an-object\"").RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("set_workflow_package_draft", original);

        redacted.GetRawText().Should().Be(original.GetRawText());
    }

    [Fact]
    public void RedactArgs_PackageWithoutWorkflowsOrAgents_ProducesPartialSummary()
    {
        // Defensive: a package missing workflows/agents arrays still gets a redaction (anchor +
        // sha + size). The summary just has whatever fields it found.
        var original = JsonDocument.Parse("""{"package": {"entryPoint": {"key": "x", "version": "1"}}}""").RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("set_workflow_package_draft", original);

        var pkg = redacted.GetProperty("package");
        pkg.GetProperty("_redacted").GetBoolean().Should().BeTrue();
        pkg.GetProperty("summary").TryGetProperty("workflowCount", out _).Should().BeFalse();
        pkg.GetProperty("summary").TryGetProperty("agentCount", out _).Should().BeFalse();
        pkg.GetProperty("summary").GetProperty("entryPoint").GetProperty("key").GetString().Should().Be("x");
    }
}
