using CodeFlow.Contracts;
using CodeFlow.Orchestration;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Orchestration.Tests;

public sealed class AgentInvocationConsumerTests
{
    [Fact]
    public async Task Consumer_ShouldResolveInputInvokeAgentWriteOutputAndPublishCompletion()
    {
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "article-flow",
            WorkflowVersion: 1,
            AgentKey: "reviewer",
            AgentVersion: 3,
            InputRef: new Uri("file:///tmp/input.bin"));
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
            Decision: new RejectedDecision(["Needs stronger citations"], JsonNode.Parse("""{"severity":"medium"}""")),
            Transcript: [],
            TokenUsage: new Runtime.TokenUsage(120, 45, 165),
            ToolCallsExecuted: 0));

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(agentInvoker)
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
            completion.Decision.Should().Be(CodeFlow.Contracts.AgentDecisionKind.Rejected);
            completion.TokenUsage.Should().BeEquivalentTo(new CodeFlow.Contracts.TokenUsage(120, 45, 165));
            completion.OutputRef.Should().Be(artifactStore.Writes[0].Uri);
            completion.DecisionPayload.Should().NotBeNull();
            completion.DecisionPayload!.Value.GetProperty("kind").GetString().Should().Be("Rejected");
            completion.DecisionPayload!.Value.GetProperty("reasons")[0].GetString().Should().Be("Needs stronger citations");
        }
        finally
        {
            await harness.Stop();
        }
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
    }

    private sealed class FakeAgentInvoker(AgentInvocationResult result) : IAgentInvoker
    {
        public List<(AgentInvocationConfiguration Configuration, string? Input)> Invocations { get; } = [];

        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add((configuration, input));
            return Task.FromResult(result);
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
}
