using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Container;
using FluentAssertions;
using Xunit;

namespace CodeFlow.Runtime.Tests.Container;

public sealed class SandboxControllerRunnerTests
{
    [Fact]
    public async Task RunAsync_PostsRunRequestAndMapsResponse()
    {
        JsonObject? capturedBody = null;
        var runner = NewRunner((req, _) =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/run");
            capturedBody = ReadBody(req);
            return Json(HttpStatusCode.OK, new
            {
                jobId = "01951f8d-0123-7abc-89ab-cdef00112233",
                exitCode = 0,
                stdout = "hi\n",
                stderr = "",
                stdoutTruncated = false,
                stderrTruncated = false,
                timedOut = false,
                cancelled = false,
                durationMs = 42,
            });
        });

        var argv = SampleRunArgv("trace-abc-1234567890abcdef", image: "ghcr.io/trefry/dotnet-tester:10.0", command: "echo", args: ["hi"]);
        var result = await runner.RunAsync(argv, TimeSpan.FromSeconds(60), 1024, 1024);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Be("hi\n");
        result.TimedOut.Should().BeFalse();

        capturedBody.Should().NotBeNull();
        capturedBody!["traceId"]!.GetValue<string>().Should().Be("trace-abc-1234567890abcdef");
        // Unified `/workspace/{traceId}` layout: the trace dir IS the workspace, no per-repo
        // subfolder. RepoSlug is sent empty so the controller's validator resolves the trace
        // dir directly.
        capturedBody["repoSlug"]!.GetValue<string>().Should().Be(string.Empty);
        capturedBody["image"]!.GetValue<string>().Should().Be("ghcr.io/trefry/dotnet-tester:10.0");
        var cmd = capturedBody["cmd"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        cmd.Should().Equal("echo", "hi");
        capturedBody["limits"]!["timeoutSeconds"]!.GetValue<int>().Should().Be(60);
        capturedBody["limits"]!["cpus"]!.GetValue<double>().Should().Be(2);
        capturedBody["limits"]!["memoryBytes"]!.GetValue<long>().Should().Be(4L * 1024 * 1024 * 1024);
        capturedBody["limits"]!["pids"]!.GetValue<long>().Should().Be(1024);
        Guid.TryParseExact(capturedBody["jobId"]!.GetValue<string>(), "D", out _).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_SubflowSaga_SendsRootTraceLabelNotWorkflowLabel()
    {
        // Regression: workspaces live at `{workdirRoot}/{rootTraceId}/` and are shared across
        // every saga in a trace. A subflow saga has its own correlation id (used for per-saga
        // cleanup in the Docker backend's lifecycle service) but no on-disk dir of its own.
        // When the argv carries DISTINCT cf.workflow (saga) + cf.trace (root) labels, the
        // controller request must use the trace label so the workspace lookup hits the right
        // directory. Without this, the controller responded with `workspace_invalid` and the
        // dev/reviewer loop in any subflow consumer never converged (trace
        // 5adfd4b5-e326-4d9c-b597-aa4f8bb5b2e7 burned ~2.6M tokens before exhausting).
        JsonObject? capturedBody = null;
        var runner = NewRunner((req, _) =>
        {
            capturedBody = ReadBody(req);
            return Json(HttpStatusCode.OK, new
            {
                jobId = "01951f8d-0123-7abc-89ab-cdef00112233",
                exitCode = 0, stdout = "", stderr = "",
                stdoutTruncated = false, stderrTruncated = false,
                timedOut = false, cancelled = false, durationMs = 1,
            });
        });

        var argv = SampleRunArgv(
            traceId: "rootTrace1111111111111111111111aa",
            sagaId: "subflowSaga22222222222222222222bb");
        await runner.RunAsync(argv, TimeSpan.FromSeconds(60), 1024, 1024);

        capturedBody.Should().NotBeNull();
        capturedBody!["traceId"]!.GetValue<string>()
            .Should().Be("rootTrace1111111111111111111111aa",
                "subflow sagas must use the root trace id so the controller's workspace lookup hits the on-disk dir");
    }

    [Fact]
    public async Task RunAsync_NonSuccessResponse_ReturnsFailureWithoutThrowing()
    {
        var runner = NewRunner((req, _) =>
            Plain(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"image_not_allowed\",\"message\":\"…\"}}"));

        var result = await runner.RunAsync(SampleRunArgv("trace"), TimeSpan.FromSeconds(60), 1024, 1024);

        result.ExitCode.Should().Be(-1);
        result.StandardError.Should().Contain("403");
        result.StandardError.Should().Contain("image_not_allowed");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_RequestTimeout_ReturnsTimedOut()
    {
        var runner = NewRunner((req, ct) =>
        {
            // Hold past the configured request timeout (1s in this test).
            return Task.Run<HttpResponseMessage>(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }, ct);
        }, requestTimeoutSeconds: 1);

        var result = await runner.RunAsync(SampleRunArgv("trace"), TimeSpan.FromSeconds(1), 1024, 1024);

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
    }

    [Theory]
    [InlineData("ps")]
    [InlineData("rm")]
    [InlineData("volume")]
    public async Task RunAsync_CleanupCommands_AreNoOps(string subcommand)
    {
        var calledHttp = false;
        var runner = NewRunner((_, _) =>
        {
            calledHttp = true;
            return Json(HttpStatusCode.OK, new { });
        });

        var result = await runner.RunAsync([subcommand, "-aq", "--filter", "x=y"], TimeSpan.FromSeconds(60), 1024, 1024);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().BeEmpty();
        calledHttp.Should().BeFalse("cleanup commands must not reach the controller — controller manages its own lifecycle");
    }

    [Fact]
    public async Task RunAsync_UnknownSubcommand_Throws()
    {
        var runner = NewRunner((_, _) => Json(HttpStatusCode.OK, new { }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(["build", "-t", "x", "."], TimeSpan.FromSeconds(60), 1024, 1024));
    }

    [Fact]
    public async Task RunAsync_RunArgvWithoutTraceOrWorkflowLabel_Throws()
    {
        var runner = NewRunner((_, _) => Json(HttpStatusCode.OK, new { }));

        // Strip both cf.trace= and cf.workflow= labels. The runner prefers cf.trace and
        // falls back to cf.workflow; with neither present, there's no way to resolve the
        // workspace dir and the runner refuses the call.
        var argv = new List<string> { "run", "--rm", "alpine:3", "echo", "hi" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(argv, TimeSpan.FromSeconds(60), 1024, 1024));
    }

    // -------- helpers ------------------------------------------------------

    private static SandboxControllerRunner NewRunner(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        int requestTimeoutSeconds = 30 * 60)
    {
        var http = new HttpClient(new DelegatingTestHandler(handler))
        {
            BaseAddress = new Uri("https://test-controller"),
        };
        return new SandboxControllerRunner(
            http,
            new SandboxControllerOptions
            {
                Url = "https://test-controller",
                ClientCertPath = "/dev/null",
                ClientKeyPath = "/dev/null",
                ServerCAPath = "/dev/null",
                ServerCommonName = "test-controller",
                RequestTimeoutSeconds = requestTimeoutSeconds,
            },
            jobIdProvider: () => Guid.Parse("01951f8d-0123-7abc-89ab-cdef00112233"));
    }

    private static SandboxControllerRunner NewRunner(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        => NewRunner((req, _) => handler(req));

    private static List<string> SampleRunArgv(string traceId, string image = "alpine:3", string command = "echo", string[]? args = null, string? sagaId = null)
    {
        // Mirrors DockerHostToolService's emitted argv: cf.workflow = current saga id (cleanup
        // scope), cf.trace = root trace id (workspace path resolution). When the test only
        // passes traceId, both labels carry the same value, matching the root-saga case.
        var workflowLabel = sagaId ?? traceId;
        var argv = new List<string>
        {
            "run", "--rm",
            "--name", "codeflow-1-call-deadbeef",
            "--label", $"{DockerResourceLabels.Managed}={DockerResourceLabels.ManagedValue}",
            "--label", $"{DockerResourceLabels.Workflow}={workflowLabel}",
            "--label", $"{DockerResourceLabels.Trace}={traceId}",
            "--label", $"{DockerResourceLabels.CreatedAt}=2026-05-02T00:00:00Z",
            "--label", $"{DockerResourceLabels.ResourceKind}={DockerResourceLabels.ContainerKind}",
            "--workdir", "/workspace",
            "--cpus", "2",
            "--memory", (4L * 1024 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--pids-limit", "1024",
            "--network", "none",
            "--mount", "type=bind,source=/tmp/codeflow/trace1,target=/workspace,readonly=false",
            image,
            command,
        };
        if (args is { Length: > 0 }) argv.AddRange(args);
        return argv;
    }

    private static Task<HttpResponseMessage> Json<T>(HttpStatusCode status, T payload)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = JsonContent.Create(payload),
        });

    private static Task<HttpResponseMessage> Plain(HttpStatusCode status, string body)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    private static JsonObject? ReadBody(HttpRequestMessage req)
    {
        if (req.Content is null) return null;
        var raw = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonNode.Parse(raw)?.AsObject();
    }

    private sealed class DelegatingTestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public DelegatingTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}

public sealed class DockerRunArgvParserTests
{
    [Fact]
    public void Parse_ExtractsImageCommandTraceLabelWorkflowLabelAndLimits()
    {
        IReadOnlyList<string> argv =
        [
            "run", "--rm",
            "--name", "codeflow-foo",
            "--label", $"{DockerResourceLabels.Workflow}=saga-bbb",
            "--label", $"{DockerResourceLabels.Trace}=trace-aaa",
            "--cpus", "1.5",
            "--memory", "1073741824",
            "--pids-limit", "256",
            "--network", "none",
            "--mount", "type=bind,source=/x,target=/workspace,readonly=false",
            "ghcr.io/trefry/img:1.0",
            "dotnet", "test",
        ];

        var parsed = DockerRunArgvParser.Parse(argv);

        parsed.Image.Should().Be("ghcr.io/trefry/img:1.0");
        parsed.Cmd.Should().Equal(["dotnet", "test"]);
        parsed.TraceLabel.Should().Be("trace-aaa");
        parsed.WorkflowLabel.Should().Be("saga-bbb");
        parsed.Cpus.Should().Be(1.5);
        parsed.MemoryBytes.Should().Be(1073741824);
        parsed.PidsLimit.Should().Be(256);
    }

    [Fact]
    public void Parse_BackCompat_WorkflowLabelAloneStillExtractedAsFallback()
    {
        // Older argv builders (or replayed traces) may only emit cf.workflow. The parser
        // still surfaces it so the runner can fall back gracefully — see
        // SandboxControllerRunner.PostRunAsync's `parsed.TraceLabel ?? parsed.WorkflowLabel`.
        IReadOnlyList<string> argv =
        [
            "run", "--rm",
            "--name", "codeflow-legacy",
            "--label", $"{DockerResourceLabels.Workflow}=trace-only",
            "--mount", "type=bind,source=/x,target=/workspace,readonly=false",
            "alpine:3.20",
            "sh", "-c", "true",
        ];

        var parsed = DockerRunArgvParser.Parse(argv);

        parsed.TraceLabel.Should().BeNull();
        parsed.WorkflowLabel.Should().Be("trace-only");
    }

    [Fact]
    public void Parse_RejectsNonRunArgv()
    {
        Assert.Throws<InvalidOperationException>(() => DockerRunArgvParser.Parse(["ps", "-aq"]));
    }
}
