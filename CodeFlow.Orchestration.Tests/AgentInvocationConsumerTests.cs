using CodeFlow.Contracts;
using CodeFlow.Orchestration;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Orchestration.Tests;

public sealed class AgentInvocationConsumerTests
{
    [Fact]
    public async Task Consumer_ShouldResolveInputInvokeAgentWriteOutputAndPublishCompletion()
    {
        var nodeId = Guid.NewGuid();
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "article-flow",
            WorkflowVersion: 1,
            NodeId: nodeId,
            AgentKey: "reviewer",
            AgentVersion: 3,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>());
        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("Initial draft", "text/plain"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "Reviewed draft",
            Decision: new AgentDecision("Rejected", new JsonObject
            {
                ["reasons"] = new JsonArray("Needs stronger citations"),
                ["severity"] = "medium"
            }),
            Transcript: [],
            TokenUsage: new Runtime.TokenUsage(120, 45, 165),
            ToolCallsExecuted: 0));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"agent-consumer-tests-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);

            (await harness.Consumed.Any<AgentInvokeRequested>()).Should().BeTrue();
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            artifactStore.Writes.Should().ContainSingle();
            artifactStore.Writes[0].Metadata.TraceId.Should().Be(request.TraceId);
            artifactStore.Writes[0].Metadata.RoundId.Should().Be(request.RoundId);
            artifactStore.Writes[0].Content.Should().Be("Reviewed draft");

            agentInvoker.Invocations.Should().ContainSingle();
            agentInvoker.Invocations[0].Configuration.Should().BeEquivalentTo(agentConfig.Configuration);
            agentInvoker.Invocations[0].Input.Should().Be("Initial draft");

            var completion = harness.Published
                .Select<AgentInvocationCompleted>()
                .Single()
                .Context.Message;

            completion.TraceId.Should().Be(request.TraceId);
            completion.RoundId.Should().Be(request.RoundId);
            completion.AgentKey.Should().Be(request.AgentKey);
            completion.AgentVersion.Should().Be(request.AgentVersion);
            completion.FromNodeId.Should().Be(nodeId);
            completion.OutputPortName.Should().Be("Rejected");
            completion.TokenUsage.Should().BeEquivalentTo(new CodeFlow.Contracts.TokenUsage(120, 45, 165));
            completion.OutputRef.Should().Be(artifactStore.Writes[0].Uri);
            completion.DecisionPayload.Should().NotBeNull();
            completion.DecisionPayload!.Value.GetProperty("portName").GetString().Should().Be("Rejected");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("reasons")[0].GetString().Should().Be("Needs stronger citations");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_WhenRetryContextPresent_ShouldForwardItToAgentInvoker()
    {
        var retryContext = new CodeFlow.Contracts.RetryContext(
            AttemptNumber: 2,
            PriorFailureReason: "tool_call_budget_exceeded",
            PriorAttemptSummary: "Last output: tried X three times");

        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "retry-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>(),
            CorrelationHeaders: null,
            RetryContext: retryContext);

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4", SystemPrompt: "you are reviewer"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("Draft", "text/plain"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "Review done",
            Decision: new AgentDecision("Completed"),
            Transcript: []));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-retry-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            agentInvoker.Invocations.Should().ContainSingle();
            var invocation = agentInvoker.Invocations[0];
            invocation.Configuration.RetryContext.Should().NotBeNull();
            invocation.Configuration.RetryContext!.AttemptNumber.Should().Be(2);
            invocation.Configuration.RetryContext.PriorFailureReason.Should().Be("tool_call_budget_exceeded");
            invocation.Configuration.RetryContext.PriorAttemptSummary.Should().Contain("three times");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_WhenToolExecutionContextPresent_ShouldForwardItToAgentInvoker()
    {
        var toolExecutionContext = new CodeFlow.Contracts.ToolExecutionContext(
            new CodeFlow.Contracts.ToolWorkspaceContext(
                Guid.NewGuid(),
                "/tmp/codeflow/workspaces/abc123/repo",
                RepoUrl: "https://github.com/example/repo.git",
                RepoIdentityKey: "github.com/example/repo",
                RepoSlug: "example/repo"));

        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "workspace-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>(),
            ToolExecutionContext: toolExecutionContext);

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("Draft", "text/plain"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "done",
            Decision: new AgentDecision("Completed"),
            Transcript: []));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-tool-context-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            agentInvoker.Invocations.Should().ContainSingle();
            agentInvoker.Invocations[0].ToolExecutionContext.Should().BeEquivalentTo(
                new CodeFlow.Runtime.ToolExecutionContext(
                    new CodeFlow.Runtime.ToolWorkspaceContext(
                        toolExecutionContext.Workspace!.CorrelationId,
                        toolExecutionContext.Workspace.RootPath,
                        toolExecutionContext.Workspace.RepoUrl,
                        toolExecutionContext.Workspace.RepoIdentityKey,
                        toolExecutionContext.Workspace.RepoSlug)));
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_WhenGlobalWorkDirSet_ShouldUseItAsToolWorkspaceRoot()
    {
        var traceId = Guid.NewGuid();
        var workDir = Path.Combine(Path.GetTempPath(), $"codeflow-workdir-{Guid.NewGuid():N}");

        var globalContext = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["workDir"] = JsonSerializer.SerializeToElement(workDir)
        };

        var request = new AgentInvokeRequested(
            TraceId: traceId,
            RoundId: Guid.NewGuid(),
            WorkflowKey: "code-aware-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "code-setup",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>(),
            GlobalContext: globalContext);

        var observed = await RunConsumerAndCaptureToolExecutionContextAsync(request);

        observed.Should().NotBeNull();
        observed!.Workspace.Should().NotBeNull();
        observed.Workspace!.RootPath.Should().Be(workDir);
        observed.Workspace.CorrelationId.Should().Be(traceId);
        observed.Workspace.RepoUrl.Should().BeNull();
        observed.Workspace.RepoIdentityKey.Should().BeNull();
        observed.Workspace.RepoSlug.Should().BeNull();
    }

    [Fact]
    public async Task Consumer_WhenGlobalWorkDirAndLegacyContextBothPresent_ShouldPreferGlobalWorkDir()
    {
        var traceId = Guid.NewGuid();
        var workDir = Path.Combine(Path.GetTempPath(), $"codeflow-workdir-{Guid.NewGuid():N}");

        var legacy = new CodeFlow.Contracts.ToolExecutionContext(
            new CodeFlow.Contracts.ToolWorkspaceContext(
                Guid.NewGuid(),
                "/tmp/legacy/workspace",
                RepoUrl: "https://github.com/example/legacy.git",
                RepoIdentityKey: "github.com/example/legacy",
                RepoSlug: "example/legacy"));

        var globalContext = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["workDir"] = JsonSerializer.SerializeToElement(workDir)
        };

        var request = new AgentInvokeRequested(
            TraceId: traceId,
            RoundId: Guid.NewGuid(),
            WorkflowKey: "code-aware-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "code-setup",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>(),
            ToolExecutionContext: legacy,
            GlobalContext: globalContext);

        var observed = await RunConsumerAndCaptureToolExecutionContextAsync(request);

        observed!.Workspace!.RootPath.Should().Be(workDir);
        observed.Workspace.CorrelationId.Should().Be(traceId);
        observed.Workspace.RepoUrl.Should().BeNull("global.workDir overrides the legacy per-repo workspace context");
    }

    [Fact]
    public async Task Consumer_WhenGlobalWorkDirBlank_ShouldFallBackToLegacyContext()
    {
        var legacy = new CodeFlow.Contracts.ToolExecutionContext(
            new CodeFlow.Contracts.ToolWorkspaceContext(
                Guid.NewGuid(),
                "/tmp/legacy/workspace",
                RepoUrl: "https://github.com/example/legacy.git",
                RepoIdentityKey: "github.com/example/legacy",
                RepoSlug: "example/legacy"));

        var globalContext = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["workDir"] = JsonSerializer.SerializeToElement("   ")
        };

        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "code-aware-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "code-setup",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>(),
            ToolExecutionContext: legacy,
            GlobalContext: globalContext);

        var observed = await RunConsumerAndCaptureToolExecutionContextAsync(request);

        observed!.Workspace!.RootPath.Should().Be(legacy.Workspace!.RootPath);
        observed.Workspace.CorrelationId.Should().Be(legacy.Workspace.CorrelationId);
        observed.Workspace.RepoUrl.Should().Be(legacy.Workspace.RepoUrl);
    }

    [Fact]
    public async Task Consumer_WhenNoGlobalContext_ShouldLeaveLegacyBehaviorUnchanged()
    {
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "non-code-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>());

        var observed = await RunConsumerAndCaptureToolExecutionContextAsync(request);

        observed.Should().BeNull();
    }

    private static async Task<CodeFlow.Runtime.ToolExecutionContext?> RunConsumerAndCaptureToolExecutionContextAsync(
        AgentInvokeRequested request)
    {
        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("Draft", "text/plain"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "done",
            Decision: new AgentDecision("Completed"),
            Transcript: []));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-tool-context-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();
            agentInvoker.Invocations.Should().ContainSingle();
            return agentInvoker.Invocations[0].ToolExecutionContext;
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_WhenAgentFails_ShouldIncludeFailureContextInDecisionPayload()
    {
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "failure-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>());

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("Draft", "text/plain"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "Partial draft from a failed attempt",
            Decision: new AgentDecision("Failed", new JsonObject
            {
                ["reason"] = "tool_call_budget_exceeded"
            }),
            Transcript: [],
            ToolCallsExecuted: 7));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-failure-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            var completion = harness.Published
                .Select<AgentInvocationCompleted>()
                .Single()
                .Context.Message;

            completion.OutputPortName.Should().Be("Failed");
            completion.DecisionPayload.Should().NotBeNull();
            completion.DecisionPayload!.Value.GetProperty("portName").GetString().Should().Be("Failed");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("reason").GetString().Should().Be("tool_call_budget_exceeded");
            var failureContext = completion.DecisionPayload.Value.GetProperty("failure_context");
            failureContext.GetProperty("reason").GetString().Should().Be("tool_call_budget_exceeded");
            failureContext.GetProperty("last_output").GetString().Should().Contain("Partial draft");
            failureContext.GetProperty("tool_calls_executed").GetInt32().Should().Be(7);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_WhenAgentInvokerThrows_ShouldPublishFailedCompletionInsteadOfFaulting()
    {
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "exception-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>());

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("Draft", "text/plain"));
        var agentInvoker = new ThrowingAgentInvoker(new HttpRequestException("Response status code does not indicate success: 400 (Bad Request)."));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-exception-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);

            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();
            (await harness.Published.Any<Fault<AgentInvokeRequested>>()).Should().BeFalse();

            artifactStore.Writes.Should().ContainSingle();
            artifactStore.Writes[0].Content.Should().Contain("Agent invocation failed.");
            artifactStore.Writes[0].Content.Should().Contain("400 (Bad Request)");

            var completion = harness.Published
                .Select<AgentInvocationCompleted>()
                .Single()
                .Context.Message;

            completion.OutputPortName.Should().Be("Failed");
            completion.DecisionPayload.Should().NotBeNull();
            completion.DecisionPayload!.Value.GetProperty("portName").GetString().Should().Be("Failed");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("reason").GetString().Should().Be("Response status code does not indicate success: 400 (Bad Request).");
            completion.DecisionPayload!.Value.GetProperty("failure_context").GetProperty("reason").GetString().Should().Be("Response status code does not indicate success: 400 (Bad Request).");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("failure_code").GetString().Should().Be("AgentInvocationFailed");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("message").GetString().Should().Contain("400 (Bad Request)");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("exception_type").GetString().Should().Be(typeof(HttpRequestException).FullName);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_WhenModelHttpExceptionThrown_ShouldPersistDiagnosticsArtifactAndReferenceIt()
    {
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "exception-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>());

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("Draft", "text/plain"));
        var agentInvoker = new ThrowingAgentInvoker(new ModelClientHttpException(
            message: "Response status code does not indicate success: 400 (Bad Request).",
            statusCode: HttpStatusCode.BadRequest,
            method: "POST",
            requestUri: new Uri("https://api.openai.com/v1/responses"),
            requestHeaders: new Dictionary<string, string[]>
            {
                ["Authorization"] = ["[REDACTED]"],
                ["Content-Type"] = ["application/json"]
            },
            requestBody: """{"model":"gpt-5","input":"hello"}""",
            providerErrorMessage: "boom",
            responseReasonPhrase: "Bad Request",
            responseHeaders: new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"]
            },
            responseBody: """{"error":{"message":"boom"}}"""));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-http-exception-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            artifactStore.Writes.Should().HaveCount(2);
            artifactStore.Writes.Should().Contain(entry => entry.Metadata.FileName == "reviewer-http-diagnostics.txt");
            artifactStore.Writes.Should().Contain(entry => entry.Metadata.FileName == "reviewer-error.txt");

            var diagnostics = artifactStore.Writes.Single(entry => entry.Metadata.FileName == "reviewer-http-diagnostics.txt");
            diagnostics.Content.Should().Contain("Request URL: https://api.openai.com/v1/responses");
            diagnostics.Content.Should().Contain("Authorization: [REDACTED]");
            diagnostics.Content.Should().Contain("""{"model":"gpt-5","input":"hello"}""");

            var completion = harness.Published
                .Select<AgentInvocationCompleted>()
                .Single()
                .Context.Message;

            completion.DecisionPayload.Should().NotBeNull();
            completion.DecisionPayload!.Value.GetProperty("portName").GetString().Should().Be("Failed");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("reason").GetString().Should().Be("boom");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("message").GetString().Should().Be("boom");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("provider_error_message").GetString().Should().Be("boom");
            completion.DecisionPayload!.Value.GetProperty("payload").GetProperty("http_diagnostics_ref").GetString().Should().Be(diagnostics.Uri.ToString());
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_ShouldFlattenWorkflowContextIntoPromptTemplateVariables()
    {
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "context-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>
            {
                ["target"] = Json("""{"path":"/repos/blogger","branch":"main"}"""),
                ["attempt"] = Json("2"),
                ["approved"] = Json("true"),
                ["plainText"] = Json("\"hello\"")
            });

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration(
                "openai",
                "gpt-5.4",
                PromptTemplate: "Open {{context.target.path}} on {{context.target.branch}}"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("Draft", "text/plain"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "done",
            Decision: new AgentDecision("Completed"),
            Transcript: []));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-context-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            agentInvoker.Invocations.Should().ContainSingle();
            var variables = agentInvoker.Invocations[0].Configuration.Variables;
            variables.Should().NotBeNull();
            using var rawTarget = JsonDocument.Parse(variables!["context.target"]);
            rawTarget.RootElement.GetProperty("path").GetString().Should().Be("/repos/blogger");
            rawTarget.RootElement.GetProperty("branch").GetString().Should().Be("main");
            variables["context.target.path"].Should().Be("/repos/blogger");
            variables["context.target.branch"].Should().Be("main");
            variables["context.attempt"].Should().Be("2");
            variables["context.approved"].Should().Be("true");
            variables["context.plainText"].Should().Be("hello");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_ShouldExposeGlobalContextAlongsideContext()
    {
        // S6: AgentInvokeRequested may carry a `GlobalContext` dict (the saga's `global` bag).
        // The consumer should flatten it into template variables under the `global.` namespace,
        // alongside the existing `context.` namespace from `ContextInputs`.
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "global-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>
            {
                ["localKey"] = Json("\"local-value\"")
            },
            GlobalContext: new Dictionary<string, JsonElement>
            {
                ["sharedFlag"] = Json("\"on\""),
                ["sharedNumber"] = Json("42"),
                ["sharedObj"] = Json("""{"nested":"value"}""")
            });

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration(
                "openai",
                "gpt-5.4",
                PromptTemplate: "Local={{context.localKey}} Global={{global.sharedFlag}} Nested={{global.sharedObj.nested}}"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("Draft", "text/plain"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "done",
            Decision: new AgentDecision("Completed"),
            Transcript: []));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-global-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            agentInvoker.Invocations.Should().ContainSingle();
            var variables = agentInvoker.Invocations[0].Configuration.Variables;
            variables.Should().NotBeNull();
            variables!["context.localKey"].Should().Be("local-value");
            variables["global.sharedFlag"].Should().Be("on");
            variables["global.sharedNumber"].Should().Be("42");
            variables["global.sharedObj.nested"].Should().Be("value");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_ShouldFlattenJsonInputIntoPromptTemplateVariables()
    {
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "input-context-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>());

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration(
                "openai",
                "gpt-5.4",
                PromptTemplate: "Summary: {{input.summary}}"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(
            ("""{"summary":"Create a new blog website","reasoning":"greenfield"}""", "application/json"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "done",
            Decision: new AgentDecision("Completed"),
            Transcript: []));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-input-context-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            agentInvoker.Invocations.Should().ContainSingle();
            var variables = agentInvoker.Invocations[0].Configuration.Variables;
            variables.Should().NotBeNull();
            variables!["input.summary"].Should().Be("Create a new blog website");
            variables["input.reasoning"].Should().Be("greenfield");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_ShouldIgnoreNonJsonInputWhenBuildingTemplateVariables()
    {
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "input-context-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>());

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration(
                "openai",
                "gpt-5.4",
                PromptTemplate: "Raw: {{input}}\nField: {{input.summary}}"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(
            ("Create a new blog website using .NET 10 and React.", "text/plain"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "done",
            Decision: new AgentDecision("Completed"),
            Transcript: []));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-plain-input-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            agentInvoker.Invocations.Should().ContainSingle();
            var invocation = agentInvoker.Invocations[0];
            invocation.Input.Should().Be("Create a new blog website using .NET 10 and React.");
            invocation.Configuration.Variables.Should().BeNullOrEmpty();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_ShouldExposeReviewLoopRoundVariablesToThePromptTemplate()
    {
        // Slice 5: when a child saga is dispatched by a ReviewLoop parent, the agent's prompt
        // template gets {{round}}, {{maxRounds}}, {{isLastRound}} populated so the author can
        // branch the prompt (e.g. "this is your last round — approve or reject").
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "review-loop-child",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 1,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>(),
            ReviewRound: 3,
            ReviewMaxRounds: 3);

        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4", PromptTemplate: "p"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore(("draft payload", "text/plain"));
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "done",
            Decision: new AgentDecision("Completed"),
            Transcript: []));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-review-loop-{Guid.NewGuid():N}"))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(request);
            (await harness.Published.Any<AgentInvocationCompleted>()).Should().BeTrue();

            agentInvoker.Invocations.Should().ContainSingle();
            var variables = agentInvoker.Invocations[0].Configuration.Variables;
            variables.Should().NotBeNull();
            variables!.Should().ContainKey("round").WhoseValue.Should().Be("3");
            variables.Should().ContainKey("maxRounds").WhoseValue.Should().Be("3");
            variables.Should().ContainKey("isLastRound").WhoseValue.Should().Be("true",
                "round equals maxRounds on the final iteration");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_WhenHitlAgentReenteredSameRoundWithDifferentInputRef_ShouldCreateAnotherTask()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var firstInputRef = new Uri("file:///tmp/question-1.bin");
        var secondInputRef = new Uri("file:///tmp/question-2.bin");

        var agentConfig = new AgentConfig(
            Key: "human-socratic-interviewee",
            Version: 7,
            Kind: AgentKind.Hitl,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4"),
            ConfigJson: """{"type":"hitl"}""",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(new RecordingArtifactStore(("Question text", "text/plain")))
            .AddSingleton<IAgentInvoker>(new FakeAgentInvoker(new AgentInvocationResult(
                Output: "unused",
                Decision: new AgentDecision("Completed"),
                Transcript: [])))
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-hitl-repeat-{Guid.NewGuid():N}"))
            .BuildServiceProvider(true);

        using var scope = provider.CreateScope();
        var consumer = new AgentInvocationConsumer(
            scope.ServiceProvider.GetRequiredService<IAgentConfigRepository>(),
            scope.ServiceProvider.GetRequiredService<IArtifactStore>(),
            scope.ServiceProvider.GetRequiredService<IAgentInvoker>(),
            scope.ServiceProvider.GetRequiredService<IRoleResolutionService>(),
            scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>());

        await InvokeCreateHitlTaskAsync(consumer, new AgentInvokeRequested(
            TraceId: traceId,
            RoundId: roundId,
            WorkflowKey: "socratic-interview",
            WorkflowVersion: 14,
            NodeId: nodeId,
            AgentKey: agentConfig.Key,
            AgentVersion: agentConfig.Version,
            InputRef: firstInputRef,
            ContextInputs: new Dictionary<string, JsonElement>()), "Question 1");

        await InvokeCreateHitlTaskAsync(consumer, new AgentInvokeRequested(
            TraceId: traceId,
            RoundId: roundId,
            WorkflowKey: "socratic-interview",
            WorkflowVersion: 14,
            NodeId: nodeId,
            AgentKey: agentConfig.Key,
            AgentVersion: agentConfig.Version,
            InputRef: secondInputRef,
            ContextInputs: new Dictionary<string, JsonElement>()), "Question 2");

        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var tasks = await db.HitlTasks
            .Where(task => task.TraceId == traceId)
            .OrderBy(task => task.CreatedAtUtc)
            .ToListAsync();

        tasks.Should().HaveCount(2);
        tasks.Select(task => task.InputRef).Should().BeEquivalentTo(
            [firstInputRef.ToString(), secondInputRef.ToString()]);
        tasks.Should().OnlyContain(task => task.NodeId == nodeId && task.RoundId == roundId);
    }

    [Fact]
    public async Task Consumer_WhenHitlRequestIsRedeliveredForSameInvocation_ShouldNotDuplicateTask()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var inputRef = new Uri("file:///tmp/question-1.bin");

        var agentConfig = new AgentConfig(
            Key: "human-socratic-interviewee",
            Version: 7,
            Kind: AgentKind.Hitl,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4"),
            ConfigJson: """{"type":"hitl"}""",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(new RecordingArtifactStore(("Question text", "text/plain")))
            .AddSingleton<IAgentInvoker>(new FakeAgentInvoker(new AgentInvocationResult(
                Output: "unused",
                Decision: new AgentDecision("Completed"),
                Transcript: [])))
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"consumer-hitl-redelivery-{Guid.NewGuid():N}"))
            .BuildServiceProvider(true);

        using var scope = provider.CreateScope();
        var consumer = new AgentInvocationConsumer(
            scope.ServiceProvider.GetRequiredService<IAgentConfigRepository>(),
            scope.ServiceProvider.GetRequiredService<IArtifactStore>(),
            scope.ServiceProvider.GetRequiredService<IAgentInvoker>(),
            scope.ServiceProvider.GetRequiredService<IRoleResolutionService>(),
            scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>());
        var request = new AgentInvokeRequested(
            TraceId: traceId,
            RoundId: roundId,
            WorkflowKey: "socratic-interview",
            WorkflowVersion: 14,
            NodeId: nodeId,
            AgentKey: agentConfig.Key,
            AgentVersion: agentConfig.Version,
            InputRef: inputRef,
            ContextInputs: new Dictionary<string, JsonElement>());

        await InvokeCreateHitlTaskAsync(consumer, request, "Question 1");
        await InvokeCreateHitlTaskAsync(consumer, request, "Question 1");

        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var tasks = await db.HitlTasks
            .Where(task => task.TraceId == traceId)
            .ToListAsync();

        tasks.Should().HaveCount(1);
        tasks[0].InputRef.Should().Be(inputRef.ToString());
        tasks[0].NodeId.Should().Be(nodeId);
    }

    private sealed class FakeAgentConfigRepository(AgentConfig agentConfig) : IAgentConfigRepository
    {
        public Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default)
        {
            key.Should().Be(agentConfig.Key);
            version.Should().Be(agentConfig.Version);
            return Task.FromResult(agentConfig);
        }

        public Task<int> CreateNewVersionAsync(
            string key,
            string configJson,
            string? createdBy,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RetireAsync(string key, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AgentConfig> CreateForkAsync(
            string sourceKey,
            int sourceVersion,
            string workflowKey,
            string configJson,
            string? createdBy,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> CreatePublishedVersionAsync(
            string targetKey,
            string configJson,
            string forkedFromKey,
            int forkedFromVersion,
            string? createdBy,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeAgentInvoker(AgentInvocationResult result) : IAgentInvoker
    {
        public List<(AgentInvocationConfiguration Configuration, string? Input, CodeFlow.Runtime.ToolExecutionContext? ToolExecutionContext)> Invocations { get; } = [];

        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            CancellationToken cancellationToken = default,
            CodeFlow.Runtime.ToolExecutionContext? toolExecutionContext = null)
        {
            Invocations.Add((configuration, input, toolExecutionContext));
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRoleResolutionService : IRoleResolutionService
    {
        public Task<ResolvedAgentTools> ResolveAsync(string agentKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolvedAgentTools.Empty);
        }
    }

    private sealed class ThrowingAgentInvoker(Exception exception) : IAgentInvoker
    {
        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            CancellationToken cancellationToken = default,
            CodeFlow.Runtime.ToolExecutionContext? toolExecutionContext = null)
        {
            return Task.FromException<AgentInvocationResult>(exception);
        }
    }

    private sealed class RecordingArtifactStore((string Content, string? ContentType) initialContent) : IArtifactStore
    {
        public List<(Uri Uri, ArtifactMetadata Metadata, string Content)> Writes { get; } = [];

        public Task<ArtifactMetadata> GetMetadataAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            var write = Writes.Single(entry => entry.Uri == uri);
            return Task.FromResult(write.Metadata);
        }

        public Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(initialContent.Content)));
        }

        public async Task<Uri> WriteAsync(
            Stream content,
            ArtifactMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            var uri = new Uri($"file:///tmp/{metadata.TraceId:N}/{metadata.RoundId:N}/{metadata.ArtifactId:N}.bin");
            Writes.Add((uri, metadata, text));
            return uri;
        }
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task InvokeCreateHitlTaskAsync(
        AgentInvocationConsumer consumer,
        AgentInvokeRequested request,
        string? input)
    {
        var method = typeof(AgentInvocationConsumer).GetMethod(
            "CreateHitlTaskAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var result = method!.Invoke(consumer, [request, input, CancellationToken.None]);
        result.Should().BeAssignableTo<Task>();
        await (Task)result!;
    }
}
