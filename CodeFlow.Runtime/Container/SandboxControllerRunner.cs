using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Runtime.Container;

/// <summary>
/// <see cref="IDockerCommandRunner"/> implementation that routes <c>docker run</c> argv
/// through the out-of-process sandbox controller (sc-526) over mTLS, instead of invoking
/// the local <c>docker</c> CLI.
///
/// The controller is the only component allowed to talk to dockerd — see
/// <c>docs/sandbox-executor.md</c> §6 for the architecture and §11 for the API. This
/// runner adapts the legacy CLI argv shape that <see cref="DockerHostToolService"/>
/// produces into the controller's <c>POST /run</c> JSON schema.
///
/// Cleanup commands (<c>ps</c>, <c>rm</c>, <c>volume ls</c>, <c>volume rm</c>) become
/// no-ops on this backend — the controller manages its own per-job lifecycle (sc-533).
/// Anything else throws <see cref="InvalidOperationException"/> so accidental new call
/// sites surface loudly instead of silently misbehaving.
/// </summary>
public sealed class SandboxControllerRunner : IDockerCommandRunner
{
    private readonly HttpClient httpClient;
    private readonly SandboxControllerOptions options;
    private readonly Func<Guid> jobIdProvider;

    public SandboxControllerRunner(HttpClient httpClient, SandboxControllerOptions options, Func<Guid>? jobIdProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("SandboxController options are invalid: " + string.Join(" ", errors));
        }
        this.httpClient = httpClient;
        this.options = options;
        this.jobIdProvider = jobIdProvider ?? Guid.NewGuid;
    }

    public async Task<DockerCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long stdoutMaxBytes,
        long stderrMaxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new InvalidOperationException("SandboxControllerRunner requires at least one argument.");
        }

        return arguments[0] switch
        {
            "run" => await PostRunAsync(arguments, timeout, stdoutMaxBytes, stderrMaxBytes, cancellationToken),
            // Cleanup commands — no-op on this backend. The controller manages its own
            // per-job lifecycle so the legacy sweep / count paths return empty results.
            // Sweeping is also short-circuited at the service level (sc-533).
            "ps" => new DockerCommandResult(0, string.Empty, string.Empty, false, false, false),
            "rm" => new DockerCommandResult(0, string.Empty, string.Empty, false, false, false),
            "volume" => new DockerCommandResult(0, string.Empty, string.Empty, false, false, false),
            _ => throw new InvalidOperationException(
                $"SandboxControllerRunner does not handle docker subcommand '{arguments[0]}'. " +
                "Only 'run' is routed to the controller; 'ps' / 'rm' / 'volume' are no-ops; " +
                "anything else indicates an unintentional new call site."),
        };
    }

    private async Task<DockerCommandResult> PostRunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long stdoutMaxBytes,
        long stderrMaxBytes,
        CancellationToken cancellationToken)
    {
        var parsed = DockerRunArgvParser.Parse(arguments);
        var traceId = parsed.TraceLabel ?? string.Empty;
        if (string.IsNullOrEmpty(traceId))
        {
            throw new InvalidOperationException(
                "Sandbox controller requires the cf.workflow label on docker run argv. " +
                "DockerHostToolService must include it; see DockerResourceLabels.Workflow.");
        }

        var jobId = jobIdProvider().ToString("D");
        var requestUrl = options.Url.TrimEnd('/') + "/run";
        var body = new ControllerRunRequest
        {
            JobId = jobId,
            TraceId = traceId,
            // The unified `/workspace/{traceId}` layout treats the trace dir itself as the
            // workspace. The controller's workspace.Validator accepts an empty repoSlug and
            // resolves the trace dir directly — no `{traceId}/workspace/` subdirectory is
            // created or expected. The wire-format field stays on the DTO so older controller
            // builds keep parsing the request, but we always send empty.
            RepoSlug = string.Empty,
            Image = parsed.Image,
            Cmd = parsed.Cmd.ToArray(),
            Limits = new ControllerRunLimits
            {
                Cpus = parsed.Cpus ?? 0,
                MemoryBytes = parsed.MemoryBytes ?? 0,
                Pids = parsed.PidsLimit ?? 0,
                TimeoutSeconds = (int)Math.Ceiling(timeout.TotalSeconds),
                StdoutMaxBytes = stdoutMaxBytes,
                StderrMaxBytes = stderrMaxBytes,
            },
        };

        // HTTP timeout is exactly options.RequestTimeoutSeconds. Operators set this larger
        // than the largest per-job timeout the controller will accept so the C# layer
        // doesn't abort legitimate long-running jobs (controller enforces the per-job
        // timeout itself). A C#-side timeout here is a coarse last-resort.
        var httpTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(httpTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(requestUrl, body, ControllerJsonOptions, linkedCts.Token);
        }
        catch (TaskCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // We hit our HTTP timeout before the controller responded. Treat as a failed
            // run so the caller surfaces a normal failure to the agent.
            return new DockerCommandResult(
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: "sandbox-controller request timed out before completion",
                StandardOutputTruncated: false,
                StandardErrorTruncated: false,
                TimedOut: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            var rawError = await response.Content.ReadAsStringAsync(linkedCts.Token);
            return new DockerCommandResult(
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: $"sandbox-controller returned {(int)response.StatusCode}: {rawError}",
                StandardOutputTruncated: false,
                StandardErrorTruncated: false,
                TimedOut: false);
        }

        var result = await response.Content.ReadFromJsonAsync<ControllerRunResponse>(ControllerJsonOptions, linkedCts.Token)
                     ?? new ControllerRunResponse();

        return new DockerCommandResult(
            ExitCode: result.ExitCode,
            StandardOutput: result.Stdout ?? string.Empty,
            StandardError: result.Stderr ?? string.Empty,
            StandardOutputTruncated: result.StdoutTruncated,
            StandardErrorTruncated: result.StderrTruncated,
            TimedOut: result.TimedOut);
    }

    private static readonly System.Text.Json.JsonSerializerOptions ControllerJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // === wire-format DTOs (match docs/sandbox-executor.md §11.2 / §11.3) ====

    private sealed class ControllerRunRequest
    {
        [JsonPropertyName("jobId")] public string JobId { get; set; } = string.Empty;
        [JsonPropertyName("traceId")] public string TraceId { get; set; } = string.Empty;
        [JsonPropertyName("repoSlug")] public string RepoSlug { get; set; } = string.Empty;
        [JsonPropertyName("image")] public string Image { get; set; } = string.Empty;
        [JsonPropertyName("cmd")] public string[] Cmd { get; set; } = Array.Empty<string>();
        [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
        [JsonPropertyName("limits")] public ControllerRunLimits Limits { get; set; } = new();
    }

    private sealed class ControllerRunLimits
    {
        [JsonPropertyName("cpus")] public double Cpus { get; set; }
        [JsonPropertyName("memoryBytes")] public long MemoryBytes { get; set; }
        [JsonPropertyName("pids")] public long Pids { get; set; }
        [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; }
        [JsonPropertyName("stdoutMaxBytes")] public long StdoutMaxBytes { get; set; }
        [JsonPropertyName("stderrMaxBytes")] public long StderrMaxBytes { get; set; }
    }

    private sealed class ControllerRunResponse
    {
        [JsonPropertyName("jobId")] public string JobId { get; set; } = string.Empty;
        [JsonPropertyName("exitCode")] public int ExitCode { get; set; }
        [JsonPropertyName("stdout")] public string? Stdout { get; set; }
        [JsonPropertyName("stderr")] public string? Stderr { get; set; }
        [JsonPropertyName("stdoutTruncated")] public bool StdoutTruncated { get; set; }
        [JsonPropertyName("stderrTruncated")] public bool StderrTruncated { get; set; }
        [JsonPropertyName("timedOut")] public bool TimedOut { get; set; }
        [JsonPropertyName("cancelled")] public bool Cancelled { get; set; }
        [JsonPropertyName("durationMs")] public long DurationMs { get; set; }
    }
}

/// <summary>
/// Parses the <c>docker run …</c> argv shape that <see cref="DockerHostToolService"/>
/// produces. Public for unit testing the parity between the argv builder and the parser —
/// if these get out of sync the runner silently misbehaves on the SandboxController backend.
/// Brittle by design: any change to the argv shape needs an explicit update here too.
/// </summary>
public static class DockerRunArgvParser
{
    public sealed record Parsed(
        string Image,
        IReadOnlyList<string> Cmd,
        string? TraceLabel,
        double? Cpus,
        long? MemoryBytes,
        int? PidsLimit);

    public static Parsed Parse(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count < 2 || argv[0] != "run")
        {
            throw new InvalidOperationException("DockerRunArgvParser only handles argv starting with 'run'.");
        }

        string? traceLabel = null;
        double? cpus = null;
        long? memory = null;
        int? pids = null;
        int i = 1;

        // Walk flags. The argv shape DockerHostToolService produces uses --flag value pairs
        // exclusively; we don't try to handle --flag=value or short flags because that
        // builder doesn't emit them.
        while (i < argv.Count)
        {
            var token = argv[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) && token != "--rm")
            {
                break; // first positional → image
            }

            switch (token)
            {
                case "--rm":
                    i += 1;
                    break;
                case "--name":
                case "--workdir":
                case "--network":
                case "--mount":
                    i += 2;
                    break;
                case "--label":
                    if (i + 1 < argv.Count && argv[i + 1].StartsWith($"{DockerResourceLabels.Workflow}=", StringComparison.Ordinal))
                    {
                        traceLabel = argv[i + 1].Substring(DockerResourceLabels.Workflow.Length + 1);
                    }
                    i += 2;
                    break;
                case "--cpus":
                    if (i + 1 < argv.Count && double.TryParse(argv[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedCpus))
                    {
                        cpus = parsedCpus;
                    }
                    i += 2;
                    break;
                case "--memory":
                    if (i + 1 < argv.Count && long.TryParse(argv[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMem))
                    {
                        memory = parsedMem;
                    }
                    i += 2;
                    break;
                case "--pids-limit":
                    if (i + 1 < argv.Count && int.TryParse(argv[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPids))
                    {
                        pids = parsedPids;
                    }
                    i += 2;
                    break;
                default:
                    // Unknown flag — assume it takes a value; future-proof.
                    i += 2;
                    break;
            }
        }

        if (i >= argv.Count)
        {
            throw new InvalidOperationException("docker run argv ended before image was reached.");
        }
        var image = argv[i];
        var cmd = argv.Skip(i + 1).ToArray();

        return new Parsed(image, cmd, traceLabel, cpus, memory, pids);
    }
}
