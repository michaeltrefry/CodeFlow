using System.Diagnostics;
using System.Text.Json;
using Jint;
using Jint.Runtime;
using Microsoft.Extensions.Caching.Memory;

namespace CodeFlow.Orchestration.Scripting;

/// <summary>
/// Executes a Logic node's JavaScript using Jint in a sandboxed, bounded engine.
///
/// Scripts receive three globals:
/// <list type="bullet">
///   <item><c>input</c> — parsed JSON from the upstream node's output.</item>
///   <item><c>context</c> — frozen workflow inputs for the run (<see cref="Object.freeze"/>, deep).</item>
///   <item><c>setNodePath(portName)</c> — selects the outgoing port.</item>
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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentNullException.ThrowIfNull(declaredPorts);
        ArgumentNullException.ThrowIfNull(context);

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

            engine.Execute(BootstrapScript);
            engine.Execute($"var input = {input.GetRawText()};");
            engine.Execute($"var context = __deepFreeze({SerializeContext(context)});");
            engine.Execute(prepared);

            stopwatch.Stop();

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

            return LogicNodeEvaluationResult.Success(chosenPort, logs, stopwatch.Elapsed);
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
