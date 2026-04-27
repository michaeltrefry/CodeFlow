using CodeFlow.Orchestration.Scripting;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Scripting;

/// <summary>
/// Unit-level coverage for the F2 script-level extractor — pure parser, no DB.
/// </summary>
public sealed class ScriptDataflowExtractorTests
{
    [Fact]
    public void Extract_NullScript_ReturnsEmpty()
    {
        var result = ScriptDataflowExtractor.Extract(null, "input");

        result.WorkflowWrites.Should().BeEmpty();
        result.ContextWrites.Should().BeEmpty();
        result.CallsSetOutput.Should().BeFalse();
        result.CallsSetInput.Should().BeFalse();
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Extract_EmptyOrWhitespaceScript_ReturnsEmpty()
    {
        var result = ScriptDataflowExtractor.Extract("   \n\t  ", "input");

        result.WorkflowWrites.Should().BeEmpty();
    }

    [Fact]
    public void Extract_TopLevelSetWorkflow_ReturnsDefiniteWrite()
    {
        const string script = "setWorkflow('currentPlan', input.text);";

        var result = ScriptDataflowExtractor.Extract(script, "output");

        result.WorkflowWrites.Should().ContainSingle();
        result.WorkflowWrites[0].Key.Should().Be("currentPlan");
        result.WorkflowWrites[0].Confidence.Should().Be(DataflowConfidence.Definite);
    }

    [Fact]
    public void Extract_SetContext_ReturnsContextWrite()
    {
        const string script = "setContext('round', 1);";

        var result = ScriptDataflowExtractor.Extract(script, "input");

        result.ContextWrites.Should().ContainSingle()
            .Which.Should().Match<ExtractedKeyWrite>(w =>
                w.Key == "round" && w.Confidence == DataflowConfidence.Definite);
    }

    [Fact]
    public void Extract_SetOutput_FlagsCallsSetOutput()
    {
        const string script = "setOutput('done');";

        var result = ScriptDataflowExtractor.Extract(script, "output");

        result.CallsSetOutput.Should().BeTrue();
    }

    [Fact]
    public void Extract_SetInput_FlagsCallsSetInput()
    {
        const string script = "setInput('next');";

        var result = ScriptDataflowExtractor.Extract(script, "input");

        result.CallsSetInput.Should().BeTrue();
    }

    [Fact]
    public void Extract_InsideIfStatement_RecordsAsConditional()
    {
        const string script = """
            if (input.decision === 'Approved') {
                setWorkflow('approvedAt', new Date().toISOString());
            }
            """;

        var result = ScriptDataflowExtractor.Extract(script, "output");

        result.WorkflowWrites.Should().ContainSingle();
        result.WorkflowWrites[0].Confidence.Should().Be(DataflowConfidence.Conditional);
    }

    [Fact]
    public void Extract_InsideForLoop_RecordsAsConditional()
    {
        const string script = """
            for (var i = 0; i < 3; i++) {
                setWorkflow('attempt_' + i, i);
            }
            """;

        var result = ScriptDataflowExtractor.Extract(script, "input");

        // Dynamic key — no key recorded, but a diagnostic IS emitted.
        result.WorkflowWrites.Should().BeEmpty();
        result.Diagnostics.Should().NotBeEmpty();
    }

    [Fact]
    public void Extract_MixOfDefiniteAndConditional_RecordsBothAccurately()
    {
        const string script = """
            setWorkflow('always', input.alwaysValue);
            if (input.flag) {
                setWorkflow('sometimes', input.sometimesValue);
            }
            """;

        var result = ScriptDataflowExtractor.Extract(script, "output");

        result.WorkflowWrites.Should().HaveCount(2);
        var always = result.WorkflowWrites.Single(w => w.Key == "always");
        always.Confidence.Should().Be(DataflowConfidence.Definite);
        var sometimes = result.WorkflowWrites.Single(w => w.Key == "sometimes");
        sometimes.Confidence.Should().Be(DataflowConfidence.Conditional);
    }

    [Fact]
    public void Extract_DynamicKey_EmitsDiagnostic()
    {
        const string script = """
            var key = 'computed-' + input.suffix;
            setWorkflow(key, input.value);
            """;

        var result = ScriptDataflowExtractor.Extract(script, "output");

        result.WorkflowWrites.Should().BeEmpty();
        result.Diagnostics.Should().NotBeEmpty();
        result.Diagnostics.Should().Contain(d => d.Contains("dynamic key"));
    }

    [Fact]
    public void Extract_ParseError_EmitsDiagnosticWithoutThrowing()
    {
        const string script = "this is not valid javascript! function {{";

        var result = ScriptDataflowExtractor.Extract(script, "input");

        result.Diagnostics.Should().NotBeEmpty();
        result.WorkflowWrites.Should().BeEmpty();
    }

    [Fact]
    public void Extract_InsideTryCatch_RecordsAsConditional()
    {
        const string script = """
            try {
                setWorkflow('attempted', input.value);
            } catch (err) {
                setWorkflow('failed', err.message);
            }
            """;

        var result = ScriptDataflowExtractor.Extract(script, "output");

        result.WorkflowWrites.Should().HaveCount(2);
        result.WorkflowWrites.Should().AllSatisfy(w =>
            w.Confidence.Should().Be(DataflowConfidence.Conditional));
    }

    [Fact]
    public void Extract_NestedConditionalDepth_StaysConditional()
    {
        const string script = """
            if (a) {
                if (b) {
                    setWorkflow('deep', input.value);
                }
            }
            """;

        var result = ScriptDataflowExtractor.Extract(script, "output");

        result.WorkflowWrites.Should().ContainSingle()
            .Which.Confidence.Should().Be(DataflowConfidence.Conditional);
    }

    [Fact]
    public void Extract_CallExpressionWithNoArguments_EmitsDiagnostic()
    {
        const string script = "setWorkflow();";

        var result = ScriptDataflowExtractor.Extract(script, "input");

        result.WorkflowWrites.Should().BeEmpty();
        result.Diagnostics.Should().Contain(d => d.Contains("no arguments"));
    }
}
