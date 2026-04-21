using CodeFlow.Contracts;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Observability;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text;

namespace CodeFlow.Orchestration.Tests;

public sealed class ObservabilityTests
{
    [Fact]
    public async Task ConsumerActivity_UsesWorkflowTraceIdAsOtelTraceId()
    {
        var workflowTraceId = Guid.NewGuid();
        var expectedOtelTraceId = CodeFlowActivity.ToOtelTraceId(workflowTraceId);

        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CodeFlowActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Add(activity)
        };

        ActivitySource.AddActivityListener(listener);

        var request = new AgentInvokeRequested(
            TraceId: workflowTraceId,
            RoundId: Guid.NewGuid(),
            WorkflowKey: "article-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 3,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, System.Text.Json.JsonElement>());
        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5.4"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "codex");
        var artifactStore = new RecordingArtifactStore("Initial draft");
        var agentInvoker = new FakeAgentInvoker(new AgentInvocationResult(
            Output: "Reviewed draft",
            Decision: new CompletedDecision(),
            Transcript: []));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"obs-tests-{Guid.NewGuid():N}"))
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
        }
        finally
        {
            await harness.Stop();
        }

        var consumeActivity = captured
            .SingleOrDefault(a => a.OperationName == "agent.invocation.consume");
        consumeActivity.Should().NotBeNull("the consumer should emit an 'agent.invocation.consume' span");
        consumeActivity!.TraceId.Should().Be(expectedOtelTraceId);
        consumeActivity.GetTagItem(CodeFlowActivity.TagNames.TraceId).Should().Be(workflowTraceId);
        consumeActivity.GetTagItem(CodeFlowActivity.TagNames.AgentKey).Should().Be(request.AgentKey);
    }

    [Fact]
    public void ToOtelTraceId_IsDeterministicAndDerivedFromGuidBytes()
    {
        var guid = new Guid("12345678-1234-1234-1234-1234567890ab");
        var traceId = CodeFlowActivity.ToOtelTraceId(guid);
        var second = CodeFlowActivity.ToOtelTraceId(guid);

        traceId.Should().Be(second);
        traceId.Should().NotBe(default(ActivityTraceId));
    }

    private sealed class FakeAgentConfigRepository(AgentConfig agentConfig) : IAgentConfigRepository
    {
        public Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(agentConfig);
        }

        public Task<int> CreateNewVersionAsync(
            string key,
            string configJson,
            string? createdBy,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> RetireAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeAgentInvoker(AgentInvocationResult result) : IAgentInvoker
    {
        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class RecordingArtifactStore(string content) : IArtifactStore
    {
        public Task<ArtifactMetadata> GetMetadataAsync(Uri uri, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)));

        public Task<Uri> WriteAsync(
            Stream content,
            ArtifactMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Uri($"file:///tmp/{metadata.ArtifactId:N}.bin"));
        }
    }
}
