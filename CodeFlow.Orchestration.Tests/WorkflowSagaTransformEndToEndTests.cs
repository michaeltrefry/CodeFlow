using CodeFlow.Contracts;
using CodeFlow.Host;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testcontainers.MariaDb;
using Testcontainers.RabbitMq;

namespace CodeFlow.Orchestration.Tests;

[Collection("Bus integration")]
[Trait("Category", "EndToEnd")]
public sealed class WorkflowSagaTransformEndToEndTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_transform_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly RabbitMqContainer rabbitMqContainer = new RabbitMqBuilder("rabbitmq:4.0-management")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly string artifactRoot = Path.Combine(
        Path.GetTempPath(),
        "codeflow-saga-transform-tests",
        Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(artifactRoot);
        await Task.WhenAll(mariaDbContainer.StartAsync(), rabbitMqContainer.StartAsync());
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(artifactRoot))
        {
            Directory.Delete(artifactRoot, recursive: true);
        }

        await rabbitMqContainer.DisposeAsync();
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task TransformNode_RendersTemplate_AgainstUpstreamInputAndContextWorkflowVars()
    {
        // Workflow: Start (kickoff) → Transform → downstream (sink) → terminal.
        // The Transform's template references input.* (kickoff's output JSON),
        // context.* (saga local context bag), and workflow.* (saga workflow bag).
        // The downstream sink agent receives the rendered string as its input — we capture
        // that input through the recording invoker to assert the render output verbatim.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff",
            new AgentDecision("Completed"),
            output: """{"text":"world","count":3}""");
        invoker.Queue("sink",
            new AgentDecision("Completed"),
            output: "sink-done");

        var configuration = BuildConfiguration();
        var endpointsReady = new EndpointReadinessTracker(["agent-invocations", "workflow-saga"]);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(
            builder.Configuration,
            bus => bus.AddConfigureEndpointsCallback((_, _, cfg) =>
                cfg.ConnectReceiveEndpointObserver(endpointsReady)));
        builder.Services.AddSingleton<IAgentInvoker>(invoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(host.Services, "kickoff", "sink");

        var workflowKey = await SeedTransformHappyPathWorkflowAsync(host.Services);
        await host.StartAsync();
        await endpointsReady.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            var traceId = Guid.NewGuid();
            var startNodeId = await GetNodeIdAsync(host.Services, workflowKey, 1, WorkflowNodeKind.Start);
            var inputRef = await WriteInputArtifactAsync(host.Services, "seed-input");

            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: Guid.NewGuid(),
                WorkflowKey: workflowKey,
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, JsonElement>
                {
                    ["greeting"] = JsonSerializer.SerializeToElement("ctxv"),
                },
                WorkflowContext: new Dictionary<string, JsonElement>
                {
                    ["flag"] = JsonSerializer.SerializeToElement("wfv"),
                }));

            var saga = await WaitForTerminalStateAsync(
                host.Services,
                traceId,
                timeout: TimeSpan.FromSeconds(120));

            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));

            var sinkInput = invoker.LastInputFor("sink");
            sinkInput.Should().Be("hello, world (3) ctx=ctxv wf=wfv");

            invoker.CallsFor("kickoff").Should().Be(1);
            invoker.CallsFor("sink").Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TransformNode_OutputTypeJson_RendersValidJson_AndPassesPayloadDownstream()
    {
        // outputType=json — the Transform's template renders a valid JSON object. The saga
        // validates it parses, then writes the rendered text verbatim as the Out artifact.
        // Sink agent receives that JSON text as its input.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff",
            new AgentDecision("Completed"),
            output: """{"name":"Alice","count":3}""");
        invoker.Queue("sink",
            new AgentDecision("Completed"),
            output: "sink-done");

        var configuration = BuildConfiguration();
        var endpointsReady = new EndpointReadinessTracker(["agent-invocations", "workflow-saga"]);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(
            builder.Configuration,
            bus => bus.AddConfigureEndpointsCallback((_, _, cfg) =>
                cfg.ConnectReceiveEndpointObserver(endpointsReady)));
        builder.Services.AddSingleton<IAgentInvoker>(invoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(host.Services, "kickoff", "sink");

        var workflowKey = await SeedTransformJsonHappyPathWorkflowAsync(host.Services);
        await host.StartAsync();
        await endpointsReady.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            var traceId = Guid.NewGuid();
            var startNodeId = await GetNodeIdAsync(host.Services, workflowKey, 1, WorkflowNodeKind.Start);
            var inputRef = await WriteInputArtifactAsync(host.Services, "seed-input");

            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: Guid.NewGuid(),
                WorkflowKey: workflowKey,
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, JsonElement>()));

            var saga = await WaitForTerminalStateAsync(
                host.Services,
                traceId,
                timeout: TimeSpan.FromSeconds(120));

            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));

            var sinkInput = invoker.LastInputFor("sink");
            sinkInput.Should().NotBeNullOrWhiteSpace();

            // Round-trip: the sink's input must parse as JSON and carry the shaped payload.
            using var doc = JsonDocument.Parse(sinkInput!);
            doc.RootElement.GetProperty("greeting").GetString().Should().Be("Hi Alice");
            doc.RootElement.GetProperty("count").GetInt32().Should().Be(3);

            invoker.CallsFor("kickoff").Should().Be(1);
            invoker.CallsFor("sink").Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TransformNode_OutputTypeJson_ParseFailureRoutesToFailedTerminal()
    {
        // outputType=json with a template that renders text which does NOT parse as JSON.
        // Render itself succeeds; the post-render JSON validation fails, and with no Failed
        // edge wired the saga terminates with FailureReason mentioning JSON.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff",
            new AgentDecision("Completed"),
            output: """{"text":"world"}""");

        var configuration = BuildConfiguration();
        var endpointsReady = new EndpointReadinessTracker(["agent-invocations", "workflow-saga"]);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(
            builder.Configuration,
            bus => bus.AddConfigureEndpointsCallback((_, _, cfg) =>
                cfg.ConnectReceiveEndpointObserver(endpointsReady)));
        builder.Services.AddSingleton<IAgentInvoker>(invoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(host.Services, "kickoff");

        var workflowKey = await SeedTransformJsonParseFailureWorkflowAsync(host.Services);
        await host.StartAsync();
        await endpointsReady.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            var traceId = Guid.NewGuid();
            var startNodeId = await GetNodeIdAsync(host.Services, workflowKey, 1, WorkflowNodeKind.Start);
            var inputRef = await WriteInputArtifactAsync(host.Services, "seed-input");

            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: Guid.NewGuid(),
                WorkflowKey: workflowKey,
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, JsonElement>()));

            var saga = await WaitForTerminalStateAsync(
                host.Services,
                traceId,
                timeout: TimeSpan.FromSeconds(120));

            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
            saga.FailureReason.Should().NotBeNullOrWhiteSpace();
            saga.FailureReason!.Should().Contain("Transform node");
            saga.FailureReason!.Should().Contain("invalid JSON");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TransformNode_InputScript_ShapesTemplateScope()
    {
        // inputScript runs BEFORE the Scriban scope is built. setInput rewrites the artifact
        // the template will read; setWorkflow lands a workflow var the template references.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff",
            new AgentDecision("Completed"),
            output: """{"name":"Bob"}""");
        invoker.Queue("sink",
            new AgentDecision("Completed"),
            output: "sink-done");

        var configuration = BuildConfiguration();
        var endpointsReady = new EndpointReadinessTracker(["agent-invocations", "workflow-saga"]);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(
            builder.Configuration,
            bus => bus.AddConfigureEndpointsCallback((_, _, cfg) =>
                cfg.ConnectReceiveEndpointObserver(endpointsReady)));
        builder.Services.AddSingleton<IAgentInvoker>(invoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(host.Services, "kickoff", "sink");

        var workflowKey = await SeedTransformInputScriptWorkflowAsync(host.Services);
        await host.StartAsync();
        await endpointsReady.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            var traceId = Guid.NewGuid();
            var startNodeId = await GetNodeIdAsync(host.Services, workflowKey, 1, WorkflowNodeKind.Start);
            var inputRef = await WriteInputArtifactAsync(host.Services, "seed-input");

            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: Guid.NewGuid(),
                WorkflowKey: workflowKey,
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, JsonElement>()));

            var saga = await WaitForTerminalStateAsync(
                host.Services,
                traceId,
                timeout: TimeSpan.FromSeconds(120));

            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));

            // inputScript called setInput(JSON.stringify({name:'Bob',count:5}))
            // and setWorkflow('greeting','Hi'). Template renders against the rewritten input.
            invoker.LastInputFor("sink").Should().Be("Hi Bob, count=5");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TransformNode_OutputScript_ModifiesRenderedPayload()
    {
        // outputScript runs AFTER render (and after JSON validation when applicable). Its
        // setOutput call replaces the rendered text before the artifact is written.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff",
            new AgentDecision("Completed"),
            output: """{"text":"world"}""");
        invoker.Queue("sink",
            new AgentDecision("Completed"),
            output: "sink-done");

        var configuration = BuildConfiguration();
        var endpointsReady = new EndpointReadinessTracker(["agent-invocations", "workflow-saga"]);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(
            builder.Configuration,
            bus => bus.AddConfigureEndpointsCallback((_, _, cfg) =>
                cfg.ConnectReceiveEndpointObserver(endpointsReady)));
        builder.Services.AddSingleton<IAgentInvoker>(invoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(host.Services, "kickoff", "sink");

        var workflowKey = await SeedTransformOutputScriptWorkflowAsync(host.Services);
        await host.StartAsync();
        await endpointsReady.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            var traceId = Guid.NewGuid();
            var startNodeId = await GetNodeIdAsync(host.Services, workflowKey, 1, WorkflowNodeKind.Start);
            var inputRef = await WriteInputArtifactAsync(host.Services, "seed-input");

            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: Guid.NewGuid(),
                WorkflowKey: workflowKey,
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, JsonElement>()));

            var saga = await WaitForTerminalStateAsync(
                host.Services,
                traceId,
                timeout: TimeSpan.FromSeconds(120));

            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));

            // Template renders "rendered: world", outputScript prefixes "TAGGED-" via setOutput.
            invoker.LastInputFor("sink").Should().Be("TAGGED-rendered: world");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TransformNode_BothScriptsInterleaveWithJsonMode()
    {
        // Combined: inputScript shapes the input, template renders JSON, JSON-validate passes,
        // outputScript wraps the parsed JSON via setOutput, JSON-revalidate passes, sink receives
        // the wrapped envelope.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff",
            new AgentDecision("Completed"),
            output: """{"x":7}""");
        invoker.Queue("sink",
            new AgentDecision("Completed"),
            output: "sink-done");

        var configuration = BuildConfiguration();
        var endpointsReady = new EndpointReadinessTracker(["agent-invocations", "workflow-saga"]);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(
            builder.Configuration,
            bus => bus.AddConfigureEndpointsCallback((_, _, cfg) =>
                cfg.ConnectReceiveEndpointObserver(endpointsReady)));
        builder.Services.AddSingleton<IAgentInvoker>(invoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(host.Services, "kickoff", "sink");

        var workflowKey = await SeedTransformBothScriptsJsonWorkflowAsync(host.Services);
        await host.StartAsync();
        await endpointsReady.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            var traceId = Guid.NewGuid();
            var startNodeId = await GetNodeIdAsync(host.Services, workflowKey, 1, WorkflowNodeKind.Start);
            var inputRef = await WriteInputArtifactAsync(host.Services, "seed-input");

            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: Guid.NewGuid(),
                WorkflowKey: workflowKey,
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, JsonElement>()));

            var saga = await WaitForTerminalStateAsync(
                host.Services,
                traceId,
                timeout: TimeSpan.FromSeconds(120));

            saga.CurrentState.Should().Be(
                nameof(WorkflowSagaStateMachine.Completed),
                because: $"saga.FailureReason was: {saga.FailureReason ?? "<null>"}");

            var sinkInput = invoker.LastInputFor("sink");
            sinkInput.Should().NotBeNullOrWhiteSpace();
            using var doc = JsonDocument.Parse(sinkInput!);
            // inputScript bumps x by 3 → 10. Template renders {"value":10}. outputScript wraps in
            // {wrapped:{value:10},source:"transform"}.
            var wrapped = doc.RootElement.GetProperty("wrapped");
            wrapped.GetProperty("value").GetInt32().Should().Be(10);
            doc.RootElement.GetProperty("source").GetString().Should().Be("transform");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TransformNode_OutputScript_InvalidJsonOverrideRoutesToFailedTerminal()
    {
        // outputType=json + an outputScript whose setOutput emits non-JSON. The render itself
        // produces valid JSON, but the override fails the post-script JSON re-validation.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff",
            new AgentDecision("Completed"),
            output: """{"x":1}""");

        var configuration = BuildConfiguration();
        var endpointsReady = new EndpointReadinessTracker(["agent-invocations", "workflow-saga"]);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(
            builder.Configuration,
            bus => bus.AddConfigureEndpointsCallback((_, _, cfg) =>
                cfg.ConnectReceiveEndpointObserver(endpointsReady)));
        builder.Services.AddSingleton<IAgentInvoker>(invoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(host.Services, "kickoff");

        var workflowKey = await SeedTransformOutputScriptInvalidJsonWorkflowAsync(host.Services);
        await host.StartAsync();
        await endpointsReady.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            var traceId = Guid.NewGuid();
            var startNodeId = await GetNodeIdAsync(host.Services, workflowKey, 1, WorkflowNodeKind.Start);
            var inputRef = await WriteInputArtifactAsync(host.Services, "seed-input");

            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: Guid.NewGuid(),
                WorkflowKey: workflowKey,
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, JsonElement>()));

            var saga = await WaitForTerminalStateAsync(
                host.Services,
                traceId,
                timeout: TimeSpan.FromSeconds(120));

            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
            saga.FailureReason.Should().NotBeNullOrWhiteSpace();
            saga.FailureReason!.Should().Contain("Transform node");
            saga.FailureReason!.Should().Contain("setOutput");
            saga.FailureReason!.Should().Contain("JSON");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task TransformNode_RouteRenderErrorToFailedTerminal()
    {
        // Transform with a runaway-loop template forces the Scriban sandbox budget to abort.
        // No 'Failed' edge is wired, so the saga must terminate with FailureReason set —
        // mirroring the implicit-Failed-port semantics for any other node kind.
        var invoker = new RecordingScriptedAgentInvoker();
        invoker.Queue("kickoff",
            new AgentDecision("Completed"),
            output: """{"text":"x"}""");

        var configuration = BuildConfiguration();
        var endpointsReady = new EndpointReadinessTracker(["agent-invocations", "workflow-saga"]);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(
            builder.Configuration,
            bus => bus.AddConfigureEndpointsCallback((_, _, cfg) =>
                cfg.ConnectReceiveEndpointObserver(endpointsReady)));
        builder.Services.AddSingleton<IAgentInvoker>(invoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentsAsync(host.Services, "kickoff");

        var workflowKey = await SeedTransformRenderFailureWorkflowAsync(host.Services);
        await host.StartAsync();
        await endpointsReady.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            var traceId = Guid.NewGuid();
            var startNodeId = await GetNodeIdAsync(host.Services, workflowKey, 1, WorkflowNodeKind.Start);
            var inputRef = await WriteInputArtifactAsync(host.Services, "seed-input");

            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: Guid.NewGuid(),
                WorkflowKey: workflowKey,
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, JsonElement>()));

            var saga = await WaitForTerminalStateAsync(
                host.Services,
                traceId,
                timeout: TimeSpan.FromSeconds(120));

            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
            saga.FailureReason.Should().NotBeNullOrWhiteSpace();
            saga.FailureReason!.Should().Contain("Transform node");
            saga.FailureReason!.Should().Contain("render");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable] = mariaDbContainer.GetConnectionString(),
                ["RabbitMq:Host"] = rabbitMqContainer.Hostname,
                ["RabbitMq:Port"] = rabbitMqContainer.GetMappedPublicPort(5672).ToString(),
                ["RabbitMq:VirtualHost"] = "/",
                ["RabbitMq:Username"] = "codeflow",
                ["RabbitMq:Password"] = "codeflow_dev",
                ["Artifacts:RootDirectory"] = artifactRoot,
                ["RabbitMq:PrefetchCount"] = "1",
                ["RabbitMq:ConsumerConcurrencyLimit"] = "1",
                ["Secrets:MasterKey"] = TestSecrets.DeterministicMasterKeyBase64
            })
            .Build();
    }

    private static async Task SeedAgentsAsync(IServiceProvider services, params string[] agentKeys)
    {
        await using var scope = services.CreateAsyncScope();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentConfigRepository>();

        foreach (var agentKey in agentKeys)
        {
            var configJson = $$"""
            {
              "provider": "lmstudio",
              "model": "{{agentKey}}",
              "enableHostTools": false
            }
            """;

            (await agentRepo.CreateNewVersionAsync(agentKey, configJson, "test")).Should().Be(1);
        }
    }

    private static async Task<string> SeedTransformHappyPathWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var transformNode = Guid.NewGuid();
        var sinkNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";
        const string sinkPortsJson = """["Completed","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = "transform-happy",
            Version = 1,
            Name = "Transform happy path",
            MaxRoundsPerRound = 5,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = transformNode,
                    Kind = WorkflowNodeKind.Transform,
                    OutputPortsJson = "[]",
                    Template = "hello, {{ input.text }} ({{ input.count }}) ctx={{ context.greeting }} wf={{ workflow.flag }}",
                    OutputType = "string",
                },
                new WorkflowNodeEntity
                {
                    NodeId = sinkNode,
                    Kind = WorkflowNodeKind.Agent,
                    AgentKey = "sink",
                    AgentVersion = 1,
                    OutputPortsJson = sinkPortsJson,
                },
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = startNode,
                    FromPort = "Completed",
                    ToNodeId = transformNode,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    SortOrder = 0,
                },
                new WorkflowEdgeEntity
                {
                    FromNodeId = transformNode,
                    FromPort = "Out",
                    ToNodeId = sinkNode,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    SortOrder = 1,
                },
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task<string> SeedTransformJsonHappyPathWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var transformNode = Guid.NewGuid();
        var sinkNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";
        const string sinkPortsJson = """["Completed","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = "transform-json-happy",
            Version = 1,
            Name = "Transform JSON happy path",
            MaxRoundsPerRound = 5,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = transformNode,
                    Kind = WorkflowNodeKind.Transform,
                    OutputPortsJson = "[]",
                    Template = """{"greeting":"Hi {{ input.name }}","count":{{ input.count }}}""",
                    OutputType = "json",
                },
                new WorkflowNodeEntity
                {
                    NodeId = sinkNode,
                    Kind = WorkflowNodeKind.Agent,
                    AgentKey = "sink",
                    AgentVersion = 1,
                    OutputPortsJson = sinkPortsJson,
                },
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = startNode,
                    FromPort = "Completed",
                    ToNodeId = transformNode,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    SortOrder = 0,
                },
                new WorkflowEdgeEntity
                {
                    FromNodeId = transformNode,
                    FromPort = "Out",
                    ToNodeId = sinkNode,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    SortOrder = 1,
                },
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task<string> SeedTransformJsonParseFailureWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var transformNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = "transform-json-failure",
            Version = 1,
            Name = "Transform JSON parse failure",
            MaxRoundsPerRound = 5,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = transformNode,
                    Kind = WorkflowNodeKind.Transform,
                    OutputPortsJson = "[]",
                    // Renders to a string that is NOT valid JSON: e.g. `not json world`.
                    // Validator parses Scriban successfully (no syntax errors); render succeeds;
                    // post-render JSON validation must fail.
                    Template = "not json {{ input.text }}",
                    OutputType = "json",
                },
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = startNode,
                    FromPort = "Completed",
                    ToNodeId = transformNode,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    SortOrder = 0,
                },
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task<string> SeedTransformInputScriptWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var transformNode = Guid.NewGuid();
        var sinkNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";
        const string sinkPortsJson = """["Completed","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = "transform-input-script",
            Version = 1,
            Name = "Transform inputScript",
            MaxRoundsPerRound = 5,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = transformNode,
                    Kind = WorkflowNodeKind.Transform,
                    OutputPortsJson = "[]",
                    InputScript =
                        "setWorkflow('greeting', 'Hi'); " +
                        "setInput(JSON.stringify({ name: input.name, count: 5 }));",
                    Template = "{{ workflow.greeting }} {{ input.name }}, count={{ input.count }}",
                    OutputType = "string",
                },
                new WorkflowNodeEntity
                {
                    NodeId = sinkNode,
                    Kind = WorkflowNodeKind.Agent,
                    AgentKey = "sink",
                    AgentVersion = 1,
                    OutputPortsJson = sinkPortsJson,
                },
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = startNode, FromPort = "Completed",
                    ToNodeId = transformNode, ToPort = WorkflowEdge.DefaultInputPort, SortOrder = 0,
                },
                new WorkflowEdgeEntity
                {
                    FromNodeId = transformNode, FromPort = "Out",
                    ToNodeId = sinkNode, ToPort = WorkflowEdge.DefaultInputPort, SortOrder = 1,
                },
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task<string> SeedTransformOutputScriptWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var transformNode = Guid.NewGuid();
        var sinkNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";
        const string sinkPortsJson = """["Completed","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = "transform-output-script",
            Version = 1,
            Name = "Transform outputScript",
            MaxRoundsPerRound = 5,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = transformNode,
                    Kind = WorkflowNodeKind.Transform,
                    OutputPortsJson = "[]",
                    Template = "rendered: {{ input.text }}",
                    OutputType = "string",
                    // String mode → `output` is a JS string. Prefix it via setOutput.
                    OutputScript = "setOutput('TAGGED-' + output);",
                },
                new WorkflowNodeEntity
                {
                    NodeId = sinkNode,
                    Kind = WorkflowNodeKind.Agent,
                    AgentKey = "sink",
                    AgentVersion = 1,
                    OutputPortsJson = sinkPortsJson,
                },
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = startNode, FromPort = "Completed",
                    ToNodeId = transformNode, ToPort = WorkflowEdge.DefaultInputPort, SortOrder = 0,
                },
                new WorkflowEdgeEntity
                {
                    FromNodeId = transformNode, FromPort = "Out",
                    ToNodeId = sinkNode, ToPort = WorkflowEdge.DefaultInputPort, SortOrder = 1,
                },
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task<string> SeedTransformBothScriptsJsonWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var transformNode = Guid.NewGuid();
        var sinkNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";
        const string sinkPortsJson = """["Completed","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = "transform-both-scripts-json",
            Version = 1,
            Name = "Transform both scripts json",
            MaxRoundsPerRound = 5,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = transformNode,
                    Kind = WorkflowNodeKind.Transform,
                    OutputPortsJson = "[]",
                    // inputScript: bump x by 3.
                    InputScript = "setInput(JSON.stringify({ x: input.x + 3 }));",
                    Template = """{"value":{{ input.x }}}""",
                    OutputType = "json",
                    // outputScript (json mode): output is the parsed object {value:10}. Wrap it.
                    OutputScript = "setOutput(JSON.stringify({ wrapped: output, source: 'transform' }));",
                },
                new WorkflowNodeEntity
                {
                    NodeId = sinkNode,
                    Kind = WorkflowNodeKind.Agent,
                    AgentKey = "sink",
                    AgentVersion = 1,
                    OutputPortsJson = sinkPortsJson,
                },
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = startNode, FromPort = "Completed",
                    ToNodeId = transformNode, ToPort = WorkflowEdge.DefaultInputPort, SortOrder = 0,
                },
                new WorkflowEdgeEntity
                {
                    FromNodeId = transformNode, FromPort = "Out",
                    ToNodeId = sinkNode, ToPort = WorkflowEdge.DefaultInputPort, SortOrder = 1,
                },
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task<string> SeedTransformOutputScriptInvalidJsonWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var transformNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = "transform-output-invalid-json",
            Version = 1,
            Name = "Transform outputScript invalid JSON override",
            MaxRoundsPerRound = 5,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = transformNode,
                    Kind = WorkflowNodeKind.Transform,
                    OutputPortsJson = "[]",
                    Template = """{"x":{{ input.x }}}""",
                    OutputType = "json",
                    // Render produces valid JSON {"x":1}; the override is plain text — fails JSON
                    // re-validation. With no Failed edge wired, saga must terminate Failed.
                    OutputScript = "setOutput('not json at all');",
                },
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = startNode, FromPort = "Completed",
                    ToNodeId = transformNode, ToPort = WorkflowEdge.DefaultInputPort, SortOrder = 0,
                },
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task<string> SeedTransformRenderFailureWorkflowAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var startNode = Guid.NewGuid();
        var transformNode = Guid.NewGuid();
        const string startPortsJson = """["Completed","Failed"]""";

        var workflow = new WorkflowEntity
        {
            Key = "transform-failure",
            Version = 1,
            Name = "Transform render failure",
            MaxRoundsPerRound = 5,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = startNode,
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "kickoff",
                    AgentVersion = 1,
                    OutputPortsJson = startPortsJson,
                },
                new WorkflowNodeEntity
                {
                    NodeId = transformNode,
                    Kind = WorkflowNodeKind.Transform,
                    OutputPortsJson = "[]",
                    // Runaway loop tripping the renderer's LoopLimit (1000) — parses fine,
                    // throws ScriptAbortException at render. Validator-side parse check passes.
                    Template = "{{ for i in 0..5000 }}x{{ end }}",
                    OutputType = "string",
                },
            ],
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromNodeId = startNode,
                    FromPort = "Completed",
                    ToNodeId = transformNode,
                    ToPort = WorkflowEdge.DefaultInputPort,
                    SortOrder = 0,
                },
            ]
        };

        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync();
        return workflow.Key;
    }

    private static async Task<Guid> GetNodeIdAsync(
        IServiceProvider services,
        string workflowKey,
        int version,
        WorkflowNodeKind kind)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var workflow = await dbContext.Workflows
            .Include(w => w.Nodes)
            .AsNoTracking()
            .SingleAsync(w => w.Key == workflowKey && w.Version == version);

        return workflow.Nodes.Single(n => n.Kind == kind).NodeId;
    }

    private async Task<Uri> WriteInputArtifactAsync(IServiceProvider services, string content)
    {
        await using var scope = services.CreateAsyncScope();
        var artifactStore = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        return await artifactStore.WriteAsync(
            stream,
            new ArtifactMetadata(
                TraceId: Guid.NewGuid(),
                RoundId: Guid.NewGuid(),
                ArtifactId: Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: "input.txt"));
    }

    private static async Task<WorkflowSagaStateEntity> WaitForTerminalStateAsync(
        IServiceProvider services,
        Guid traceId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        WorkflowSagaStateEntity? saga = null;

        while (DateTime.UtcNow < deadline)
        {
            await using var scope = services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            saga = await dbContext.WorkflowSagas
                .Include(s => s.Decisions)
                .AsNoTracking()
                .SingleOrDefaultAsync(s => s.TraceId == traceId);

            if (saga is not null && saga.CurrentState is
                nameof(WorkflowSagaStateMachine.Completed)
                or nameof(WorkflowSagaStateMachine.Failed))
            {
                return saga;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Saga for trace {traceId} did not reach a terminal state within {timeout}. Last state: {saga?.CurrentState ?? "<missing>"}");
    }

    private sealed class EndpointReadinessTracker : IReceiveEndpointObserver
    {
        private readonly HashSet<string> expected;
        private readonly HashSet<string> ready = [];
        private readonly TaskCompletionSource allReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();

        public EndpointReadinessTracker(IEnumerable<string> expectedEndpoints)
        {
            expected = new HashSet<string>(expectedEndpoints, StringComparer.OrdinalIgnoreCase);
        }

        public async Task WaitAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(allReady.Task, Task.Delay(timeout));
            if (completed != allReady.Task)
            {
                string missing;
                lock (gate)
                {
                    missing = string.Join(", ", expected.Except(ready, StringComparer.OrdinalIgnoreCase));
                }
                throw new TimeoutException(
                    $"Timed out waiting for receive endpoints to become ready. Missing: {missing}");
            }
        }

        public Task Ready(ReceiveEndpointReady ready)
        {
            ArgumentNullException.ThrowIfNull(ready);
            var name = ExtractQueueName(ready.InputAddress);
            lock (gate)
            {
                if (expected.Contains(name))
                {
                    this.ready.Add(name);
                    if (expected.IsSubsetOf(this.ready))
                    {
                        allReady.TrySetResult();
                    }
                }
            }
            return Task.CompletedTask;
        }

        public Task Stopping(ReceiveEndpointStopping stopping) => Task.CompletedTask;
        public Task Completed(ReceiveEndpointCompleted completed) => Task.CompletedTask;
        public Task Faulted(ReceiveEndpointFaulted faulted) => Task.CompletedTask;

        private static string ExtractQueueName(Uri? inputAddress)
        {
            if (inputAddress is null)
            {
                return string.Empty;
            }
            var path = inputAddress.AbsolutePath.TrimStart('/');
            var slash = path.IndexOf('/');
            return slash < 0 ? path : path[(slash + 1)..];
        }
    }

    /// <summary>
    /// Like <see cref="WorkflowSagaEndToEndTests.ScriptedAgentInvoker"/> but also captures the
    /// last <c>input</c> string seen per agent. The Transform tests assert on the rendered
    /// artifact handed downstream.
    /// </summary>
    private sealed class RecordingScriptedAgentInvoker : IAgentInvoker
    {
        private readonly ConcurrentDictionary<string, Queue<ScriptedStep>> scripts = new();
        private readonly ConcurrentDictionary<string, int> callCounts = new();
        private readonly ConcurrentDictionary<string, string?> lastInputs = new();

        public void Queue(string agentKey, AgentDecision decision, string output)
        {
            var queue = scripts.GetOrAdd(agentKey, _ => new Queue<ScriptedStep>());
            lock (queue)
            {
                queue.Enqueue(new ScriptedStep(decision, output));
            }
        }

        public int CallsFor(string agentKey) =>
            callCounts.TryGetValue(agentKey, out var count) ? count : 0;

        public string? LastInputFor(string agentKey) =>
            lastInputs.TryGetValue(agentKey, out var value) ? value : null;

        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            CancellationToken cancellationToken = default,
            CodeFlow.Runtime.ToolExecutionContext? toolExecutionContext = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var agentKey = configuration.Model;

            if (!scripts.TryGetValue(agentKey, out var queue))
            {
                throw new InvalidOperationException(
                    $"No scripted steps registered for agent '{agentKey}'.");
            }

            ScriptedStep step;
            lock (queue)
            {
                if (queue.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Scripted queue for agent '{agentKey}' is empty.");
                }
                step = queue.Dequeue();
            }

            lastInputs[agentKey] = input;
            callCounts.AddOrUpdate(agentKey, 1, (_, count) => count + 1);

            return Task.FromResult(new AgentInvocationResult(
                Output: step.Output,
                Decision: step.Decision,
                Transcript: [],
                TokenUsage: new Runtime.TokenUsage(1, 1, 2),
                ToolCallsExecuted: 0));
        }

        private sealed record ScriptedStep(AgentDecision Decision, string Output);
    }
}
