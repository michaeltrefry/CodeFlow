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

    [Fact]
    public void Evaluate_FailsScript_WhenLogEntryCountExceedsHostBudget()
    {
        var host = BuildHost();
        // MaxLogEntries = 1000; push past the cap in a tight loop. Statement limit (10_000) is
        // high enough that we hit the log cap first.
        const string script = """
            for (var i = 0; i < 1500; i++) {
                log('entry-' + i);
            }
            setNodePath('A');
            """;

        var result = host.Evaluate(
            workflowKey: "wf",
            workflowVersion: 1,
            nodeId: Guid.NewGuid(),
            script: script,
            declaredPorts: new[] { "A" },
            input: ParseJson("{}"),
            context: EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("log()");
        result.LogEntries.Count.Should().BeLessThanOrEqualTo(1_000);
    }

    [Fact]
    public void Evaluate_TruncatesOversizedLogMessage()
    {
        var host = BuildHost();
        // MaxLogEntryChars = 4_000. Emit a single 10k-character message and verify it was trimmed.
        const string script = """
            log('x'.repeat(10000));
            setNodePath('A');
            """;

        var result = host.Evaluate(
            workflowKey: "wf",
            workflowVersion: 1,
            nodeId: Guid.NewGuid(),
            script: script,
            declaredPorts: new[] { "A" },
            input: ParseJson("{}"),
            context: EmptyContext);

        result.IsSuccess.Should().BeTrue();
        result.LogEntries.Should().HaveCount(1);
        result.LogEntries[0].Length.Should().BeLessThanOrEqualTo(4_100); // cap + " [truncated]" marker
        result.LogEntries[0].Should().EndWith("[truncated]");
    }

    [Fact]
    public void Evaluate_SetContext_NotCalled_ReturnsEmptyUpdates()
    {
        var host = BuildHost();
        const string script = "setNodePath('A');";

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "A" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeTrue();
        result.ContextUpdates.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_SetContext_WithScalar_CapturedInResult()
    {
        var host = BuildHost();
        const string script = """
            setContext('turn', 3);
            setContext('status', 'active');
            setNodePath('A');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "A" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeTrue();
        result.ContextUpdates.Should().HaveCount(2);
        result.ContextUpdates["turn"].GetInt32().Should().Be(3);
        result.ContextUpdates["status"].GetString().Should().Be("active");
    }

    [Fact]
    public void Evaluate_SetContext_WithArray_AppendsPriorContextValue()
    {
        // Simulates the interviewer loop: prior transcript is in context.transcript,
        // script appends the latest Q&A and writes it back.
        var host = BuildHost();
        const string script = """
            var prior = (context.transcript || []).slice();
            prior.push({ q: input.question, a: input.answer });
            setContext('transcript', prior);
            setNodePath('A');
            """;
        var context = new Dictionary<string, JsonElement>
        {
            ["transcript"] = ParseJson("""[{"q":"q1","a":"a1"}]""")
        };

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "A" },
            ParseJson("""{"question":"q2","answer":"a2"}"""),
            context);

        result.IsSuccess.Should().BeTrue();
        result.ContextUpdates.Should().ContainKey("transcript");
        var transcript = result.ContextUpdates["transcript"];
        transcript.ValueKind.Should().Be(JsonValueKind.Array);
        transcript.GetArrayLength().Should().Be(2);
        transcript[1].GetProperty("q").GetString().Should().Be("q2");
        transcript[1].GetProperty("a").GetString().Should().Be("a2");
    }

    [Fact]
    public void Evaluate_SetContext_LastWriteWins_ForSameKey()
    {
        var host = BuildHost();
        const string script = """
            setContext('k', 'first');
            setContext('k', 'second');
            setNodePath('A');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "A" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeTrue();
        result.ContextUpdates["k"].GetString().Should().Be("second");
    }

    [Fact]
    public void Evaluate_SetContext_EmptyKey_FailsScript()
    {
        var host = BuildHost();
        const string script = """
            setContext('', 'x');
            setNodePath('A');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "A" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("non-empty string key");
    }

    [Fact]
    public void Evaluate_SetContext_NonStringKey_FailsScript()
    {
        var host = BuildHost();
        const string script = """
            setContext(42, 'x');
            setNodePath('A');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "A" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
    }

    [Fact]
    public void Evaluate_SetContext_DroppedWhenScriptFails()
    {
        var host = BuildHost();
        const string script = """
            setContext('k', 'should-not-persist');
            throw new Error('boom');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "A" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.ContextUpdates.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_SetContext_OversizedPayload_FailsWithContextBudgetExceeded()
    {
        var host = BuildHost();
        // 256KB cap — emit a single 300k-character string.
        const string script = """
            setContext('big', 'x'.repeat(300000));
            setNodePath('A');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "A" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ContextBudgetExceeded);
    }

    [Fact]
    public void Evaluate_ContextMutation_StillFrozen_EvenWithSetContextAvailable()
    {
        // Regression: setContext must not open a write path through the frozen context object.
        var host = BuildHost();
        const string script = """
            try { context.existing = 'mutated'; } catch (e) { /* expected */ }
            setNodePath(context.existing);
            """;
        var context = new Dictionary<string, JsonElement>
        {
            ["existing"] = ParseJson("\"original\"")
        };

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "original", "mutated" },
            ParseJson("{}"),
            context);

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("original");
    }

    [Fact]
    public void Evaluate_ReadsGlobalValues_FromSnapshotBag()
    {
        var host = BuildHost();
        const string script = """
            if (global.feature === 'on') { setNodePath('Enabled'); }
            else { setNodePath('Disabled'); }
            """;
        var global = new Dictionary<string, JsonElement>
        {
            ["feature"] = ParseJson("\"on\"")
        };

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Enabled", "Disabled" },
            ParseJson("{}"),
            EmptyContext,
            cancellationToken: default,
            global: global);

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("Enabled");
    }

    [Fact]
    public void Evaluate_SetGlobal_CapturesUpdatesSeparatelyFromContext()
    {
        var host = BuildHost();
        const string script = """
            setContext('local', 'L');
            setGlobal('shared', { hello: 'world' });
            setGlobal('count', 42);
            setNodePath('Out');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Out" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeTrue();
        result.ContextUpdates.Should().HaveCount(1);
        result.ContextUpdates["local"].GetString().Should().Be("L");
        result.GlobalUpdates.Should().HaveCount(2);
        result.GlobalUpdates["shared"].GetProperty("hello").GetString().Should().Be("world");
        result.GlobalUpdates["count"].GetInt32().Should().Be(42);
    }

    [Fact]
    public void Evaluate_SetGlobal_RejectsNonStringKey()
    {
        var host = BuildHost();
        const string script = """
            setGlobal('', 'oops');
            setNodePath('Out');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Out" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("setGlobal(key, value) requires a non-empty string key.");
    }

    [Fact]
    public void Evaluate_GlobalIsFrozen_DirectAssignmentIgnoredInStrictMode()
    {
        var host = BuildHost();
        const string script = """
            try { global.feature = 'mutated'; } catch (e) { /* expected */ }
            setNodePath(global.feature);
            """;
        var global = new Dictionary<string, JsonElement>
        {
            ["feature"] = ParseJson("\"on\"")
        };

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "on" },
            ParseJson("{}"),
            EmptyContext,
            cancellationToken: default,
            global: global);

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("on", "deep-frozen global rejects direct assignment");
    }

    private static LogicNodeScriptHost BuildHost()
    {
        return new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions()));
    }

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
