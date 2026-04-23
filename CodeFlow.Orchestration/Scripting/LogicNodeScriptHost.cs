using System.Diagnostics;
using System.Text.Json;
using Jint;
using Jint.Runtime;
using Microsoft.Extensions.Caching.Memory;

namespace CodeFlow.Orchestration.Scripting;

/// <summary>
/// Executes a Logic node's JavaScript using Jint in a sandboxed, bounded engine.
///
/// Scripts receive these globals:
/// <list type="bullet">
///   <item><c>input</c> — parsed JSON from the upstream node's output.</item>
///   <item><c>context</c> — frozen workflow inputs for the run (<see cref="Object.freeze"/>, deep).</item>
///   <item><c>setNodePath(portName)</c> — selects the outgoing port.</item>
///   <item><c>setContext(key, value)</c> — writes a top-level key into the workflow context
///     so downstream nodes (and re-entries of upstream nodes) see it as <c>context.key</c>.
///     The value must be JSON-serializable. Writes are applied when the script completes
///     successfully; a failed evaluation discards them.</item>
///   <item><c>log(message)</c> — appends to the evaluation's log buffer.</item>
/// </list>
///
/// No CLR interop, no <c>eval</c>, no <c>Function</c> constructor, no network/filesystem/MCP access.
/// Hard limits on recursion depth, statement count, memory, and wall-clock time.
/// </summary>
public sealed class LogicNodeScriptHost
{
    private const int DefaultRecursionLimit = 64;
    private const int DefaultStatementLimit = 10_000;
    private const long DefaultMemoryLimitBytes = 4_000_000; // 4 MB
    private const int MaxLogEntries = 1_000;
    private const int MaxLogEntryChars = 4_000;
    private const int MaxTotalLogChars = 256 * 1024; // 256 KB of UTF-16 (host-side, outside Jint accounting)
    private const int MaxContextUpdatesChars = 256 * 1024; // 256 KB of serialized JSON per evaluation
    private const string LogBudgetExceededMessage =
        "log() output exceeded the host budget and evaluation was aborted.";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(250);

    private const string BootstrapScript = """
        function __deepFreeze(o) {
            if (o === null || typeof o !== 'object') { return o; }
            Object.freeze(o);
            for (var k in o) {
                if (Object.prototype.hasOwnProperty.call(o, k)) {
                    __deepFreeze(o[k]);
                }
            }
            return o;
        }
        var __contextUpdates = Object.create(null);
        var __globalUpdates = Object.create(null);
        function setContext(key, value) {
            if (typeof key !== 'string' || key.length === 0 || key.trim().length === 0) {
                throw new TypeError('setContext(key, value) requires a non-empty string key.');
            }
            __contextUpdates[key] = value;
        }
        function setGlobal(key, value) {
            if (typeof key !== 'string' || key.length === 0 || key.trim().length === 0) {
                throw new TypeError('setGlobal(key, value) requires a non-empty string key.');
            }
            __globalUpdates[key] = value;
        }
        function __readContextUpdates() {
            try { return JSON.stringify(__contextUpdates); }
            catch (e) {
                throw new TypeError('setContext value is not JSON-serializable: ' + e.message);
            }
        }
        function __readGlobalUpdates() {
            try { return JSON.stringify(__globalUpdates); }
            catch (e) {
                throw new TypeError('setGlobal value is not JSON-serializable: ' + e.message);
            }
        }
        """;

    private readonly IMemoryCache cache;

    public LogicNodeScriptHost(IMemoryCache cache)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public LogicNodeEvaluationResult Evaluate(
        string workflowKey,
        int workflowVersion,
        Guid nodeId,
        string script,
        IReadOnlyCollection<string> declaredPorts,
        JsonElement input,
        IReadOnlyDictionary<string, JsonElement> context,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, JsonElement>? global = null,
        int? reviewRound = null,
        int? reviewMaxRounds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentNullException.ThrowIfNull(declaredPorts);
        ArgumentNullException.ThrowIfNull(context);
        var globalSnapshot = global ?? LogicNodeEvaluationResult.EmptyContextUpdates;

        var logs = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        string? chosenPort = null;
        var logBudgetExceeded = false;
        var totalLogChars = 0;

        try
        {
            var prepared = GetOrPrepare(workflowKey, workflowVersion, nodeId, script);
            using var engine = BuildSandboxedEngine(cancellationToken);

            engine.SetValue("setNodePath", (Action<string>)(port =>
            {
                if (string.IsNullOrWhiteSpace(port))
                {
                    throw new JavaScriptException("setNodePath(port) requires a non-empty port name.");
                }
                chosenPort = port;
            }));

            engine.SetValue("log", (Action<string>)(message =>
            {
                if (logBudgetExceeded)
                {
                    throw new JavaScriptException(LogBudgetExceededMessage);
                }

                if (logs.Count >= MaxLogEntries)
                {
                    logBudgetExceeded = true;
                    throw new JavaScriptException(LogBudgetExceededMessage);
                }

                const string truncationMarker = " [truncated]";
                var text = message ?? string.Empty;
                var truncated = false;
                if (text.Length > MaxLogEntryChars)
                {
                    text = text[..(MaxLogEntryChars - truncationMarker.Length)];
                    truncated = true;
                }

                var remaining = MaxTotalLogChars - totalLogChars;
                if (remaining <= 0)
                {
                    logBudgetExceeded = true;
                    throw new JavaScriptException(LogBudgetExceededMessage);
                }

                if (text.Length > remaining)
                {
                    text = text[..Math.Max(0, remaining - truncationMarker.Length)];
                    truncated = true;
                }

                if (truncated)
                {
                    text += truncationMarker;
                }

                logs.Add(text);
                totalLogChars += text.Length;
            }));

            // Sentinel defaults outside a ReviewLoop so scripts that reference these bindings
            // from shared workflows don't crash on plain invocations.
            var scriptRound = reviewRound ?? 0;
            var scriptMaxRounds = reviewMaxRounds ?? 0;
            var scriptIsLast = reviewRound is int r && reviewMaxRounds is int m && m > 0 && r >= m;

            engine.Execute(BootstrapScript);
            engine.Execute($"var input = {input.GetRawText()};");
            engine.Execute($"var context = __deepFreeze({SerializeContext(context)});");
            engine.Execute($"var global = __deepFreeze({SerializeContext(globalSnapshot)});");
            engine.Execute($"var round = {scriptRound};");
            engine.Execute($"var maxRounds = {scriptMaxRounds};");
            engine.Execute($"var isLastRound = {(scriptIsLast ? "true" : "false")};");
            engine.Execute(prepared);

            var updatesJson = engine.Evaluate("__readContextUpdates()").AsString();
            var globalUpdatesJson = engine.Evaluate("__readGlobalUpdates()").AsString();

            stopwatch.Stop();

            if (updatesJson.Length > MaxContextUpdatesChars)
            {
                return LogicNodeEvaluationResult.Fail(
                    LogicNodeFailureKind.ContextBudgetExceeded,
                    $"setContext payload exceeded {MaxContextUpdatesChars} characters when serialized.",
                    logs,
                    stopwatch.Elapsed);
            }

            if (globalUpdatesJson.Length > MaxContextUpdatesChars)
            {
                return LogicNodeEvaluationResult.Fail(
                    LogicNodeFailureKind.ContextBudgetExceeded,
                    $"setGlobal payload exceeded {MaxContextUpdatesChars} characters when serialized.",
                    logs,
                    stopwatch.Elapsed);
            }

            var contextUpdates = ParseContextUpdates(updatesJson);
            var globalUpdates = ParseContextUpdates(globalUpdatesJson);

            if (chosenPort is null)
            {
                return LogicNodeEvaluationResult.Fail(
                    LogicNodeFailureKind.MissingSetNodePath,
                    "Script did not call setNodePath(portName).",
                    logs,
                    stopwatch.Elapsed);
            }

            if (!declaredPorts.Contains(chosenPort, StringComparer.Ordinal))
            {
                return LogicNodeEvaluationResult.Fail(
                    LogicNodeFailureKind.UnknownPort,
                    $"Script chose unknown port '{chosenPort}'. Declared ports: [{string.Join(", ", declaredPorts)}].",
                    logs,
                    stopwatch.Elapsed);
            }

            return LogicNodeEvaluationResult.Success(chosenPort, logs, stopwatch.Elapsed, contextUpdates, globalUpdates);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            return LogicNodeEvaluationResult.Fail(
                LogicNodeFailureKind.Timeout,
                $"Script exceeded the {DefaultTimeout.TotalMilliseconds}ms timeout.",
                logs,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return LogicNodeEvaluationResult.Fail(
                LogicNodeFailureKind.Timeout,
                "Script evaluation was cancelled.",
                logs,
                stopwatch.Elapsed);
        }
        catch (StatementsCountOverflowException ex)
        {
            stopwatch.Stop();
            return LogicNodeEvaluationResult.Fail(
                LogicNodeFailureKind.Timeout,
                $"Script exceeded the maximum statement count: {ex.Message}",
                logs,
                stopwatch.Elapsed);
        }
        catch (MemoryLimitExceededException ex)
        {
            stopwatch.Stop();
            return LogicNodeEvaluationResult.Fail(
                LogicNodeFailureKind.ScriptError,
                $"Script exceeded the memory limit: {ex.Message}",
                logs,
                stopwatch.Elapsed);
        }
        catch (JavaScriptException ex)
        {
            stopwatch.Stop();
            return LogicNodeEvaluationResult.Fail(
                LogicNodeFailureKind.ScriptError,
                ex.Message,
                logs,
                stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            stopwatch.Stop();
            return LogicNodeEvaluationResult.Fail(
                LogicNodeFailureKind.ScriptError,
                ex.Message,
                logs,
                stopwatch.Elapsed);
        }
    }

    private Prepared<Acornima.Ast.Script> GetOrPrepare(string workflowKey, int workflowVersion, Guid nodeId, string script)
    {
        var cacheKey = $"logic:{workflowKey}:{workflowVersion}:{nodeId:N}";
        return cache.GetOrCreate(cacheKey, entry =>
        {
            entry.SetPriority(CacheItemPriority.Low);
            return Engine.PrepareScript(script);
        });
    }

    private static Engine BuildSandboxedEngine(CancellationToken cancellationToken)
    {
        return new Engine(options =>
        {
            options.Strict();
            options.LimitRecursion(DefaultRecursionLimit);
            options.MaxStatements(DefaultStatementLimit);
            options.LimitMemory(DefaultMemoryLimitBytes);
            options.TimeoutInterval(DefaultTimeout);
            options.DisableStringCompilation();
            options.CancellationToken(cancellationToken);
            // AllowClr intentionally not called — default blocks all CLR interop.
        });
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseContextUpdates(string updatesJson)
    {
        if (string.IsNullOrWhiteSpace(updatesJson))
        {
            return LogicNodeEvaluationResult.EmptyContextUpdates;
        }

        using var document = JsonDocument.Parse(updatesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return LogicNodeEvaluationResult.EmptyContextUpdates;
        }

        if (!document.RootElement.EnumerateObject().Any())
        {
            return LogicNodeEvaluationResult.EmptyContextUpdates;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static string SerializeContext(IReadOnlyDictionary<string, JsonElement> context)
    {
        if (context.Count == 0)
        {
            return "{}";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in context)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
