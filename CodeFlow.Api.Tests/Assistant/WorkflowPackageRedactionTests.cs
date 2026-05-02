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
    public void RedactArgs_RedactedPayloadIsSmallerThanOriginal_ForRealisticPackages()
    {
        // The whole point: for the size of package the model actually emits (10-200KB), the
        // redacted form is dramatically smaller. The test fixture pads to ~5KB which is well
        // above the ~500-byte fixed overhead of the redacted shape (sha256 + summary +
        // _doNotCopy notice).
        var paddingChars = new string('x', 5000);
        var bigPackage = $$"""
            {
              "package": {
                "schemaVersion": "codeflow.workflow-package.v1",
                "entryPoint": { "key": "demo", "version": 1 },
                "workflows": [
                  { "key": "demo", "version": 1, "name": "{{paddingChars}}", "nodes": [ {"id":"a"}, {"id":"b"} ] }
                ],
                "agents": [ {"key":"writer", "version": 1} ]
              }
            }
            """;
        var original = JsonDocument.Parse(bigPackage).RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("set_workflow_package_draft", original);

        var originalBytes = Encoding.UTF8.GetByteCount(original.GetRawText());
        var redactedBytes = Encoding.UTF8.GetByteCount(redacted.GetRawText());
        redactedBytes.Should().BeLessThan(originalBytes);
        // Sanity: the savings are dramatic, not marginal. 5KB → < 1KB is the floor for our shape.
        redactedBytes.Should().BeLessThan(1500);
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

    // -- Layer 2 (do-not-copy marker) --------------------------------------------------------

    [Fact]
    public void RedactArgs_IncludesDoNotCopyMarker()
    {
        // The redacted shape MUST carry the _doNotCopy notice so a model that ignores the system
        // prompt still has the instruction inline. Belt-and-suspenders against pattern-matching.
        var original = JsonDocument.Parse(MultiNodePackageJson).RootElement;

        var redacted = WorkflowPackageRedaction.RedactArgs("set_workflow_package_draft", original);

        var pkg = redacted.GetProperty("package");
        pkg.GetProperty("_redacted").GetBoolean().Should().BeTrue();
        pkg.GetProperty("_doNotCopy").GetString().Should().Contain("transcript stub");
        pkg.GetProperty("_doNotCopy").GetString().Should().Contain("Do not copy");
    }

    // -- Layer 1 (placeholder detection on inbound side) -------------------------------------

    [Fact]
    public void IsRedactionPlaceholder_TrueForRedactedShape()
    {
        var redacted = JsonDocument.Parse("""{"_redacted": true, "sha256": "abc"}""").RootElement;
        WorkflowPackageRedaction.IsRedactionPlaceholder(redacted).Should().BeTrue();
    }

    [Fact]
    public void IsRedactionPlaceholder_FalseForRealPackageShape()
    {
        var realPackage = JsonDocument.Parse("""{"schemaVersion": "codeflow.workflow-package.v1", "workflows": []}""").RootElement;
        WorkflowPackageRedaction.IsRedactionPlaceholder(realPackage).Should().BeFalse();
    }

    [Fact]
    public void IsRedactionPlaceholder_FalseForNonObject()
    {
        var notObject = JsonDocument.Parse("\"some string\"").RootElement;
        WorkflowPackageRedaction.IsRedactionPlaceholder(notObject).Should().BeFalse();
    }

    [Fact]
    public void IsRedactionPlaceholder_FalseWhenRedactedFieldIsNotTrue()
    {
        // Defensive: if `_redacted` is present but isn't literal `true` (e.g., string "true"),
        // treat as a real package — don't reject legitimate input.
        var stringTrue = JsonDocument.Parse("""{"_redacted": "true"}""").RootElement;
        WorkflowPackageRedaction.IsRedactionPlaceholder(stringTrue).Should().BeFalse();
    }

    // -- Layer 4 (result-side redaction) -----------------------------------------------------

    [Theory]
    [InlineData("get_workflow_package_draft", true)]
    [InlineData("get_workflow_package", true)]
    [InlineData("set_workflow_package_draft", false)]
    [InlineData("read_file", false)]
    public void IsRedactableResultTool_ReportsExpectedSet(string tool, bool expected)
    {
        WorkflowPackageRedaction.IsRedactableResultTool(tool).Should().Be(expected);
    }

    [Fact]
    public void RedactResultJson_DraftWrapper_RedactsPackageFieldOnly()
    {
        // get_workflow_package_draft returns { status, package: {...} }. Only the package field
        // gets the redaction shape; status/message survive.
        var resultJson = """
            {
              "status": "ok",
              "package": {
                "schemaVersion": "codeflow.workflow-package.v1",
                "entryPoint": { "key": "demo", "version": 1 },
                "workflows": [ { "nodes": [{"id":"a"}, {"id":"b"}] } ],
                "agents": [ {"key":"x"} ]
              }
            }
            """;

        var redacted = WorkflowPackageRedaction.RedactResultJson("get_workflow_package_draft", resultJson);
        using var doc = JsonDocument.Parse(redacted);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("ok");
        root.GetProperty("package").GetProperty("_redacted").GetBoolean().Should().BeTrue();
        root.GetProperty("package").GetProperty("_doNotCopy").GetString().Should().Contain("transcript stub");
        root.GetProperty("package").GetProperty("summary").GetProperty("workflowCount").GetInt32().Should().Be(1);
        root.GetProperty("package").GetProperty("summary").GetProperty("nodeCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public void RedactResultJson_FullPackageBody_ReplacesWholeBody()
    {
        // get_workflow_package returns the workflow package object directly — the whole body
        // gets replaced with the redacted shape.
        var resultJson = """
            {
              "schemaVersion": "codeflow.workflow-package.v1",
              "entryPoint": { "key": "demo", "version": 1 },
              "workflows": [ { "nodes": [{"id":"a"}] } ],
              "agents": []
            }
            """;

        var redacted = WorkflowPackageRedaction.RedactResultJson("get_workflow_package", resultJson);
        using var doc = JsonDocument.Parse(redacted);
        doc.RootElement.GetProperty("_redacted").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("summary").GetProperty("workflowCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public void RedactResultJson_NotARedactableResultTool_ReturnsOriginal()
    {
        var resultJson = """{"status": "ok", "package": {"workflows": []}}""";
        var redacted = WorkflowPackageRedaction.RedactResultJson("read_file", resultJson);
        redacted.Should().Be(resultJson);
    }

    [Fact]
    public void RedactResultJson_DraftWrapperWithoutPackage_ReturnsOriginal()
    {
        // not_found path: { status: "not_found", message: "..." }. Nothing to redact.
        var resultJson = """{"status": "not_found", "message": "no draft yet"}""";
        var redacted = WorkflowPackageRedaction.RedactResultJson("get_workflow_package_draft", resultJson);
        redacted.Should().Be(resultJson);
    }

    [Fact]
    public void ResultCarriesPackagePayload_TrueForFreshDraftWrapper()
    {
        var resultJson = """{"status": "ok", "package": {"workflows": []}}""";
        WorkflowPackageRedaction.ResultCarriesPackagePayload("get_workflow_package_draft", resultJson)
            .Should().BeTrue();
    }

    [Fact]
    public void ResultCarriesPackagePayload_FalseWhenAlreadyRedacted()
    {
        var resultJson = """{"status": "ok", "package": {"_redacted": true, "sha256": "abc"}}""";
        WorkflowPackageRedaction.ResultCarriesPackagePayload("get_workflow_package_draft", resultJson)
            .Should().BeFalse();
    }

    [Fact]
    public void ResultCarriesPackagePayload_FalseForUnshapedBody()
    {
        // get_workflow_package returns either the package or an error body. An error body lacks
        // schemaVersion / workflows and shouldn't claim a carrier slot.
        var errorBody = """{"error": "Workflow not found"}""";
        WorkflowPackageRedaction.ResultCarriesPackagePayload("get_workflow_package", errorBody)
            .Should().BeFalse();
    }

    [Fact]
    public void InputCarriesPackagePayload_FalseForDraftSavePath()
    {
        // The zero-arg form of save_workflow_package omits `package` entirely — not a carrier.
        var args = JsonDocument.Parse("""{}""").RootElement;
        WorkflowPackageRedaction.InputCarriesPackagePayload("save_workflow_package", args)
            .Should().BeFalse();
    }

    [Fact]
    public void InputCarriesPackagePayload_FalseForRedactedPlaceholder()
    {
        // If the model copies the redacted shape into the args, it shouldn't claim a carrier
        // slot — the data is already redacted, nothing to compress.
        var args = JsonDocument.Parse("""{"package": {"_redacted": true, "sha256": "abc"}}""").RootElement;
        WorkflowPackageRedaction.InputCarriesPackagePayload("set_workflow_package_draft", args)
            .Should().BeFalse();
    }
}
