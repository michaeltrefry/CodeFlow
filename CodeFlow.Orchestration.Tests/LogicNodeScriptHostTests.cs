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
    public void Evaluate_ReadsWorkflowValues_FromSnapshotBag()
    {
        var host = BuildHost();
        const string script = """
            if (workflow.feature === 'on') { setNodePath('Enabled'); }
            else { setNodePath('Disabled'); }
            """;
        var workflowSnapshot = new Dictionary<string, JsonElement>
        {
            ["feature"] = ParseJson("\"on\"")
        };

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Enabled", "Disabled" },
            ParseJson("{}"),
            EmptyContext,
            cancellationToken: default,
            workflow: workflowSnapshot);

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("Enabled");
    }

    [Fact]
    public void Evaluate_SetWorkflow_CapturesUpdatesSeparatelyFromContext()
    {
        var host = BuildHost();
        const string script = """
            setContext('local', 'L');
            setWorkflow('shared', { hello: 'world' });
            setWorkflow('count', 42);
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
        result.WorkflowUpdates.Should().HaveCount(2);
        result.WorkflowUpdates["shared"].GetProperty("hello").GetString().Should().Be("world");
        result.WorkflowUpdates["count"].GetInt32().Should().Be(42);
    }

    [Fact]
    public void Evaluate_SetWorkflow_RejectsNonStringKey()
    {
        var host = BuildHost();
        const string script = """
            setWorkflow('', 'oops');
            setNodePath('Out');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Out" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("setWorkflow(key, value) requires a non-empty string key.");
    }

    [Fact]
    public void Evaluate_SetWorkflow_RejectsReservedKey()
    {
        var host = BuildHost();
        const string script = """
            setWorkflow('traceWorkDir', '/etc/evil');
            setNodePath('Out');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Out" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ReservedWorkflowKeyWrite);
        result.FailureMessage.Should().Contain("traceWorkDir");
        result.FailureMessage.Should().Contain("framework-managed workflow variable");
        result.WorkflowUpdates.Should().BeEmpty(
            "the failed evaluation must drop pending writes so the reserved key is not persisted");
    }

    [Fact]
    public void Evaluate_WorkflowIsFrozen_DirectAssignmentIgnoredInStrictMode()
    {
        var host = BuildHost();
        const string script = """
            try { workflow.feature = 'mutated'; } catch (e) { /* expected */ }
            setNodePath(workflow.feature);
            """;
        var workflowSnapshot = new Dictionary<string, JsonElement>
        {
            ["feature"] = ParseJson("\"on\"")
        };

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "on" },
            ParseJson("{}"),
            EmptyContext,
            cancellationToken: default,
            workflow: workflowSnapshot);

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("on", "deep-frozen workflow bag rejects direct assignment");
    }

    [Fact]
    public void Evaluate_ExposesReviewLoopRoundBindings_WhenProvided()
    {
        // Slice 5: inside a ReviewLoop child, scripts can read round/maxRounds/isLastRound and
        // route differently on the final round.
        var host = BuildHost();
        const string script = """
            if (isLastRound) { setNodePath('LastRound'); }
            else if (round === 1) { setNodePath('FirstRound'); }
            else { setNodePath('MiddleRound'); }
            """;
        var ports = new[] { "FirstRound", "MiddleRound", "LastRound" };

        var firstRound = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script, ports, ParseJson("{}"), EmptyContext,
            cancellationToken: default, workflow: null, reviewRound: 1, reviewMaxRounds: 3);
        firstRound.OutputPortName.Should().Be("FirstRound");

        var middleRound = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script, ports, ParseJson("{}"), EmptyContext,
            cancellationToken: default, workflow: null, reviewRound: 2, reviewMaxRounds: 3);
        middleRound.OutputPortName.Should().Be("MiddleRound");

        var lastRound = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script, ports, ParseJson("{}"), EmptyContext,
            cancellationToken: default, workflow: null, reviewRound: 3, reviewMaxRounds: 3);
        lastRound.OutputPortName.Should().Be("LastRound");
    }

    [Fact]
    public void Evaluate_ReviewLoopBindings_DefaultOutsideALoop()
    {
        // Scripts shared between ReviewLoop and non-ReviewLoop callers must see safe sentinels
        // (round = 0, isLastRound = false) so they don't crash on plain invocations.
        var host = BuildHost();
        const string script = """
            if (round === 0 && !isLastRound && maxRounds === 0) {
                setNodePath('OutsideLoop');
            } else {
                setNodePath('InsideLoop');
            }
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "OutsideLoop", "InsideLoop" },
            ParseJson("{}"), EmptyContext);

        result.OutputPortName.Should().Be("OutsideLoop");
    }

    [Fact]
    public void Evaluate_ShouldCaptureOutputOverride_WhenSetOutputIsCalled()
    {
        var host = BuildHost();
        const string script = """
            setOutput('# Interview Summary\n- Q1: a\n- Q2: b');
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowOutputOverride: true, inputVariableName: "output");

        result.IsSuccess.Should().BeTrue();
        result.OutputOverride.Should().Be("# Interview Summary\n- Q1: a\n- Q2: b");
    }

    [Fact]
    public void Evaluate_OutputOverride_ShouldBeNullWhenSetOutputNotCalled()
    {
        var host = BuildHost();
        const string script = "setNodePath('Completed');";

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowOutputOverride: true, inputVariableName: "output");

        result.IsSuccess.Should().BeTrue();
        result.OutputOverride.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SetOutput_ShouldRejectNonStringArgs()
    {
        var host = BuildHost();
        const string script = """
            setOutput(42);
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowOutputOverride: true, inputVariableName: "output");

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("setOutput");
        result.FailureMessage.Should().Contain("string");
    }

    [Fact]
    public void Evaluate_SetOutput_ShouldRejectObjectArgs()
    {
        var host = BuildHost();
        const string script = """
            setOutput({ x: 1 });
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowOutputOverride: true, inputVariableName: "output");

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
    }

    [Fact]
    public void Evaluate_SetOutput_ShouldRejectEmptyString()
    {
        var host = BuildHost();
        const string script = """
            setOutput('');
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowOutputOverride: true, inputVariableName: "output");

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
    }

    [Fact]
    public void Evaluate_SetOutput_ShouldRejectOversizedStrings()
    {
        var host = BuildHost();
        // 1 MiB cap (1,048,576 chars). Build a single ~1.1 MiB literal in one allocation — a
        // tight concat loop risks tripping the 4 MB Jint memory limit instead.
        const string script = """
            setOutput('x'.repeat(1200000));
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowOutputOverride: true, inputVariableName: "output");

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.OutputOverrideBudgetExceeded);
    }

    [Fact]
    public void Evaluate_SetOutput_ShouldCoexistWithSetNodePathAndSetContext()
    {
        var host = BuildHost();
        const string script = """
            setContext('stage', 'final');
            setOutput('rendered markdown');
            setWorkflow('shared', true);
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowOutputOverride: true, inputVariableName: "output");

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("Completed");
        result.OutputOverride.Should().Be("rendered markdown");
        result.ContextUpdates.Should().ContainKey("stage");
        result.ContextUpdates["stage"].GetString().Should().Be("final");
        result.WorkflowUpdates.Should().ContainKey("shared");
        result.WorkflowUpdates["shared"].GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Evaluate_SetOutput_ShouldFailScript_WhenNotAllowed()
    {
        // Default (allowOutputOverride: false) — setOutput must throw so authors get a clear
        // error rather than silently losing the override on Logic-node scripts where the concept
        // of an "output artifact" isn't well defined.
        var host = BuildHost();
        const string script = """
            setOutput('ignored');
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("agent-attached");
    }

    [Fact]
    public void Evaluate_OutputScript_ShouldExposeArtifactAsOutputVariable()
    {
        // With inputVariableName: "output", the injected artifact is visible to the script as `output`
        // rather than `input`. Output scripts run after the node produces its artifact, so `output`
        // is the semantically correct name.
        var host = BuildHost();
        const string script = """
            setOutput('echo:' + output.value);
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{\"value\":\"hello\"}"),
            EmptyContext,
            allowOutputOverride: true,
            inputVariableName: "output");

        result.IsSuccess.Should().BeTrue();
        result.OutputOverride.Should().Be("echo:hello");
    }

    [Fact]
    public void Evaluate_ShouldCaptureInputOverride_WhenSetInputIsCalled()
    {
        var host = BuildHost();
        const string script = """
            setInput('normalized prompt');
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowInputOverride: true);

        result.IsSuccess.Should().BeTrue();
        result.InputOverride.Should().Be("normalized prompt");
        result.OutputOverride.Should().BeNull();
    }

    [Fact]
    public void Evaluate_InputOverride_ShouldBeNullWhenSetInputNotCalled()
    {
        var host = BuildHost();
        const string script = "setNodePath('Completed');";

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowInputOverride: true);

        result.IsSuccess.Should().BeTrue();
        result.InputOverride.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SetInput_ShouldRejectNonStringArgs()
    {
        var host = BuildHost();
        const string script = """
            setInput(42);
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowInputOverride: true);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("setInput");
        result.FailureMessage.Should().Contain("string");
    }

    [Fact]
    public void Evaluate_SetInput_ShouldRejectObjectArgs()
    {
        var host = BuildHost();
        const string script = """
            setInput({ x: 1 });
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowInputOverride: true);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
    }

    [Fact]
    public void Evaluate_SetInput_ShouldRejectEmptyString()
    {
        var host = BuildHost();
        const string script = """
            setInput('');
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowInputOverride: true);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
    }

    [Fact]
    public void Evaluate_SetInput_ShouldRejectOversizedStrings()
    {
        var host = BuildHost();
        // 1 MiB cap (1,048,576 chars). Build a single ~1.1 MiB literal in one allocation — a
        // tight concat loop risks tripping the 4 MB Jint memory limit instead.
        const string script = """
            setInput('x'.repeat(1200000));
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowInputOverride: true);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.InputOverrideBudgetExceeded);
    }

    [Fact]
    public void Evaluate_SetInput_ShouldCoexistWithSetNodePathAndSetContext()
    {
        var host = BuildHost();
        const string script = """
            setContext('stage', 'prep');
            setInput('transformed prompt');
            setWorkflow('shared', true);
            log('prepped input');
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowInputOverride: true);

        result.IsSuccess.Should().BeTrue();
        result.OutputPortName.Should().Be("Completed");
        result.InputOverride.Should().Be("transformed prompt");
        result.ContextUpdates.Should().ContainKey("stage");
        result.ContextUpdates["stage"].GetString().Should().Be("prep");
        result.WorkflowUpdates.Should().ContainKey("shared");
        result.WorkflowUpdates["shared"].GetBoolean().Should().BeTrue();
        result.LogEntries.Should().Contain("prepped input");
    }

    [Fact]
    public void Evaluate_SetInput_ShouldFailScript_WhenNotAllowed()
    {
        // Default (allowInputOverride: false) — setInput must throw, paralleling setOutput's
        // gating on Logic nodes and on output scripts.
        var host = BuildHost();
        const string script = """
            setInput('ignored');
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("agent-attached");
    }

    [Fact]
    public void Evaluate_InputScript_ShouldSeeUpstreamArtifactAsInputVariable()
    {
        // Input scripts see the upstream artifact as `input` (the default variable name) and
        // can transform it with setInput().
        var host = BuildHost();
        const string script = """
            setInput('hello ' + input.name);
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{\"name\":\"world\"}"),
            EmptyContext,
            allowInputOverride: true);

        result.IsSuccess.Should().BeTrue();
        result.InputOverride.Should().Be("hello world");
    }

    [Fact]
    public void Evaluate_SetOutput_OnInputScript_ShouldThrow()
    {
        // An input-script invocation (allowInputOverride: true, allowOutputOverride: false)
        // must reject setOutput — the script's role is pre-dispatch input transformation only.
        var host = BuildHost();
        const string script = """
            setOutput('wrong verb');
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowInputOverride: true);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("agent-attached");
    }

    [Fact]
    public void Evaluate_SetInput_OnOutputScript_ShouldThrow()
    {
        // An output-script invocation (allowOutputOverride: true, allowInputOverride: false)
        // must reject setInput — mirror of the above.
        var host = BuildHost();
        const string script = """
            setInput('wrong verb');
            setNodePath('Completed');
            """;

        var result = host.Evaluate(
            "wf", 1, Guid.NewGuid(), script,
            new[] { "Completed" },
            ParseJson("{}"),
            EmptyContext,
            allowOutputOverride: true,
            inputVariableName: "output");

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(LogicNodeFailureKind.ScriptError);
        result.FailureMessage.Should().Contain("agent-attached");
    }

    private static LogicNodeScriptHost BuildHost()
    {
        return new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions()));
    }

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
