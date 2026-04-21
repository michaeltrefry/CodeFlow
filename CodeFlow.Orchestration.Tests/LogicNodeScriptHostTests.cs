using CodeFlow.Orchestration.Scripting;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace CodeFlow.Orchestration.Tests;

public sealed class LogicNodeScriptHostTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyContext =
        new Dictionary<string, JsonElement>();

    [Fact]
    public void Evaluate_HappyPath_ThreeBranchRouter_PicksCorrectPort()
    {
        var host = BuildHost();
        const string script = """
            if (input.kind === 'NewProject') {
                setNodePath('NewProjectFlow');
            } else if (input.kind === 'feature') {
                setNodePath('FeatureFlow');
            } else if (input.kind === 'bugfix') {
                setNodePath('BugFixFlow');
            }
            """;
        var ports = new[] { "NewProjectFlow", "FeatureFlow", "BugFixFlow" };

        var result = host.Evaluate(
            workflowKey: "wf",
            workflowVersion: 1,
            nodeId: Guid.NewGuid(),
            script: script,
            declaredPorts: ports,
            input: ParseJson("""{"kind":"NewProject","summary":"…"}"""),
            context: EmptyContext);

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("NewProjectFlow");
        result.Failure.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ReadsContextValues_FromWorkflowInputs()
    {
        var host = BuildHost();
        const string script = """
            if (context.repoType === 'greenfield') { setNodePath('Greenfield'); }
            else { setNodePath('Existing'); }
            """;
        var context = new Dictionary<string, JsonElement>
        {
            ["repoType"] = ParseJson("\"greenfield\""),
            ["settings"] = ParseJson("""{"tone":"formal"}""")
        };

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Greenfield", "Existing" },
            ParseJson("""{"noop":true}"""),
            context);

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("Greenfield");
    }

    [Fact]
    public void Evaluate_ContextMutation_IsIgnoredInStrictFrozen()
    {
        var host = BuildHost();
        // In strict mode, assigning to a frozen property throws a TypeError.
        const string script = """
            try { context.repoType = 'mutated'; } catch (e) { /* expected */ }
            setNodePath(context.repoType);
            """;
        var context = new Dictionary<string, JsonElement>
        {
            ["repoType"] = ParseJson("\"greenfield\"")
        };

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "greenfield", "mutated" },
            ParseJson("{}"),
            context);

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("greenfield", "frozen context must not be mutable");
    }

    [Fact]
    public void Evaluate_LogCapture_RecordsMessages()
    {
        var host = BuildHost();
        const string script = """
            log('first');
            log('second: ' + input.kind);
            setNodePath('done');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "done" },
            ParseJson("""{"kind":"X"}"""),
            EmptyContext);

        result.IsSuccess.Should().BeTrue();
        result.LogEntries.Should().Equal("first", "second: X");
    }

    [Fact]
    public void Evaluate_InfiniteLoop_Times_OutCleanly()
    {
        var host = BuildHost();
        const string script = "while (true) {}";

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "anything" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.Timeout);
    }

    [Fact]
    public void Evaluate_Eval_IsBlocked()
    {
        var host = BuildHost();
        const string script = """
            try { eval('setNodePath("fromEval")'); }
            catch (e) { log('blocked: ' + e.message); setNodePath('fallback'); }
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "fallback", "fromEval" },
            ParseJson("{}"),
            EmptyContext);

        result.OutputPortName.Should().Be("fallback", "eval must be blocked by the sandbox");
        result.LogEntries.Should().Contain(entry => entry.StartsWith("blocked:"));
    }

    [Fact]
    public void Evaluate_FunctionConstructor_IsBlocked()
    {
        var host = BuildHost();
        const string script = """
            try { (new Function('return 42'))(); setNodePath('escaped'); }
            catch (e) { setNodePath('blocked'); }
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "escaped", "blocked" },
            ParseJson("{}"),
            EmptyContext);

        result.OutputPortName.Should().Be("blocked", "Function constructor must be blocked by the sandbox");
    }

    [Fact]
    public void Evaluate_MissingSetNodePath_ReturnsFailure()
    {
        var host = BuildHost();
        const string script = "log('did nothing');";

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "anything" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.MissingSetNodePath);
    }

    [Fact]
    public void Evaluate_UnknownPort_ReturnsFailure()
    {
        var host = BuildHost();
        const string script = "setNodePath('ghost');";

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "A", "B" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.UnknownPort);
        result.FailureMessage.Should().Contain("ghost");
    }

    [Fact]
    public void Evaluate_SyntaxError_ReturnsScriptError()
    {
        var host = BuildHost();
        const string script = "this is not javascript )((;";

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "whatever" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
    }

    [Fact]
    public void Evaluate_ThrownError_IsCapturedAsScriptError()
    {
        var host = BuildHost();
        const string script = "throw new Error('boom');";

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "ok" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("boom");
    }

    [Fact]
    public void Evaluate_SameScriptCachedByVersion_ReusesPreparedScript()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var host = new LogicNodeScriptHost(cache);
        const string script = "setNodePath('A');";

        var a = host.Evaluate("wf", 1, new Guid("00000000-0000-0000-0000-000000000001"), script,
            new[] { "A" }, ParseJson("{}"), EmptyContext);
        var b = host.Evaluate("wf", 1, new Guid("00000000-0000-0000-0000-000000000001"), script,
            new[] { "A" }, ParseJson("{}"), EmptyContext);

        a.IsSuccess.Should().BeTrue();
        b.IsSuccess.Should().BeTrue();
        cache.Count.Should().Be(1);
    }

    private static LogicNodeScriptHost BuildHost()
    {
        return new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions()));
    }

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
