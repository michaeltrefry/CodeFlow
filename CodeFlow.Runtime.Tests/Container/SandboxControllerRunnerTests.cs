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
        capturedBody["repoSlug"]!.GetValue<string>().Should().Be("workspace");
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
    public async Task RunAsync_RunArgvWithoutTraceLabel_Throws()
    {
        var runner = NewRunner((_, _) => Json(HttpStatusCode.OK, new { }));

        // Strip cf.workflow=… label.
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

    private static List<string> SampleRunArgv(string traceId, string image = "alpine:3", string command = "echo", string[]? args = null)
    {
        var argv = new List<string>
        {
            "run", "--rm",
            "--name", "codeflow-1-call-deadbeef",
            "--label", $"{DockerResourceLabels.Managed}={DockerResourceLabels.ManagedValue}",
            "--label", $"{DockerResourceLabels.Workflow}={traceId}",
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
    public void Parse_ExtractsImageCommandLabelAndLimits()
    {
        IReadOnlyList<string> argv =
        [
            "run", "--rm",
            "--name", "codeflow-foo",
            "--label", $"{DockerResourceLabels.Workflow}=trace-aaa",
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
        parsed.Cpus.Should().Be(1.5);
        parsed.MemoryBytes.Should().Be(1073741824);
        parsed.PidsLimit.Should().Be(256);
    }

    [Fact]
    public void Parse_RejectsNonRunArgv()
    {
        Assert.Throws<InvalidOperationException>(() => DockerRunArgvParser.Parse(["ps", "-aq"]));
    }
}
