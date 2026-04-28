using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Contracts;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Orchestration.Tests.TokenTracking;

/// <summary>
/// Integration tests for slice 2 of the Token Usage Tracking epic. Verifies that
/// <see cref="CodeFlow.Orchestration.AgentInvocationConsumer"/> wires a
/// <see cref="CodeFlow.Orchestration.TokenTracking.TokenUsageCaptureObserver"/> through to the
/// agent invoker and that the observer's hook persists a record per LLM round-trip with the
/// pre-resolved root TraceId + scope chain.
/// </summary>
public sealed class AgentInvocationConsumerTokenUsageTests
{
    [Fact]
    public async Task Consumer_ForwardsCaptureObserverThroughInvoker_PersistsOneRecordPerRoundWithVerbatimUsage()
    {
        var nodeId = Guid.NewGuid();
        var traceId = Guid.NewGuid();
        var request = new AgentInvokeRequested(
            TraceId: traceId,
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
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "claude");
        var artifactStore = new RecordingArtifactStore("Initial draft");

        // Two-round invoker: each round fires OnModelCallCompletedAsync with a distinct raw
        // usage payload. The observer should write one record per round.
        var firstUsage = """{"input_tokens":11,"output_tokens":3,"output_tokens_details":{"reasoning_tokens":2}}""";
        var secondUsage = """{"input_tokens":5,"output_tokens":2,"input_tokens_details":{"cached_tokens":3}}""";
        var fakeInvoker = new ObserverFiringFakeAgentInvoker(
            result: new AgentInvocationResult(
                Output: "Reviewed",
                Decision: new AgentDecision("Completed"),
                Transcript: [],
                TokenUsage: new Runtime.TokenUsage(16, 5, 21),
                ToolCallsExecuted: 0),
            usagePayloads: [firstUsage, secondUsage]);

        var tokenUsageRepository = new RecordingTokenUsageRecordRepository();

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(fakeInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddSingleton<ITokenUsageRecordRepository>(tokenUsageRepository)
            .AddScoped<IPromptPartialRepository, PromptPartialRepository>()
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"agent-consumer-tokens-{Guid.NewGuid():N}"))
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

            // The capture observer's repository now holds two records — one per round-trip.
            tokenUsageRepository.Records.Should().HaveCount(2);
            var byTrace = await tokenUsageRepository.ListByTraceAsync(traceId);
            byTrace.Should().HaveCount(2);

            var first = byTrace[0];
            first.TraceId.Should().Be(traceId,
                "top-level traces store their own TraceId as the root and an empty scope chain");
            first.NodeId.Should().Be(nodeId);
            first.ScopeChain.Should().BeEmpty();
            first.Provider.Should().Be("openai");
            first.Model.Should().Be("gpt-5");
            first.Usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32().Should().Be(2);

            var second = byTrace[1];
            second.Usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(3);
            second.InvocationId.Should().NotBe(first.InvocationId,
                "each LLM round-trip mints a fresh InvocationId so the records can be distinguished");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consumer_WithoutTokenUsageRepository_SkipsCapture_ExistingTestFixturesUnaffected()
    {
        // The token-usage repository is registered as an optional dependency on the consumer so
        // the existing test fixtures (which never seeded one) keep working. Verify the consumer
        // simply skips capture when the repo is absent rather than throwing.
        var request = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "article-flow",
            WorkflowVersion: 1,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 3,
            InputRef: new Uri("file:///tmp/input.bin"),
            ContextInputs: new Dictionary<string, JsonElement>());
        var agentConfig = new AgentConfig(
            Key: request.AgentKey,
            Version: request.AgentVersion,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5"),
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "claude");
        var artifactStore = new RecordingArtifactStore("Initial draft");
        var fakeInvoker = new ObserverFiringFakeAgentInvoker(
            result: new AgentInvocationResult(
                Output: "Reviewed",
                Decision: new AgentDecision("Completed"),
                Transcript: [],
                TokenUsage: new Runtime.TokenUsage(11, 3, 14),
                ToolCallsExecuted: 0),
            usagePayloads: ["""{"input_tokens":11,"output_tokens":3}"""]);

        await using var provider = new ServiceCollection()
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentConfig))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IAgentInvoker>(fakeInvoker)
            .AddSingleton<IRoleResolutionService>(new FakeRoleResolutionService())
            .AddScoped<IPromptPartialRepository, PromptPartialRepository>()
            .AddDbContext<CodeFlowDbContext>(options => options
                .UseInMemoryDatabase($"agent-consumer-no-tokens-{Guid.NewGuid():N}"))
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

            // The legacy invoker overload must have been called (no observer threading), and
            // the captured passthrough record-count must be zero. Critically, the consumer
            // should NOT have thrown.
            fakeInvoker.ObserverInvocationsByRound.Should().BeEmpty(
                "without an ITokenUsageRecordRepository, no observer should be wired through to the invoker");
        }
        finally
        {
            await harness.Stop();
        }
    }

    private sealed class ObserverFiringFakeAgentInvoker : IAgentInvoker
    {
        private readonly AgentInvocationResult result;
        private readonly IReadOnlyList<string> usagePayloads;

        public ObserverFiringFakeAgentInvoker(AgentInvocationResult result, IReadOnlyList<string> usagePayloads)
        {
            this.result = result;
            this.usagePayloads = usagePayloads;
        }

        public ConcurrentBag<int> ObserverInvocationsByRound { get; } = new();

        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            CancellationToken cancellationToken = default,
            Runtime.ToolExecutionContext? toolExecutionContext = null)
        {
            // Legacy overload: no observer threading. The "no repo" test exercises this path.
            return Task.FromResult(result);
        }

        public async Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            IInvocationObserver? observer,
            CancellationToken cancellationToken = default,
            Runtime.ToolExecutionContext? toolExecutionContext = null)
        {
            if (observer is not null)
            {
                for (var round = 0; round < usagePayloads.Count; round++)
                {
                    using var doc = JsonDocument.Parse(usagePayloads[round]);
                    var rawUsage = doc.RootElement.Clone();
                    var invocationId = Guid.NewGuid();
                    await observer.OnModelCallStartedAsync(invocationId, round + 1, cancellationToken);
                    await observer.OnModelCallCompletedAsync(
                        invocationId,
                        round + 1,
                        new ChatMessage(ChatMessageRole.Assistant, "stub"),
                        callTokenUsage: new Runtime.TokenUsage(0, 0, 0),
                        cumulativeTokenUsage: new Runtime.TokenUsage(0, 0, 0),
                        provider: configuration.Provider,
                        model: configuration.Model,
                        rawUsage: rawUsage,
                        cancellationToken: cancellationToken);
                    ObserverInvocationsByRound.Add(round + 1);
                }
            }

            return result;
        }
    }

    private sealed class RecordingTokenUsageRecordRepository : ITokenUsageRecordRepository
    {
        private readonly List<TokenUsageRecord> records = new();
        private readonly object gate = new();

        public IReadOnlyList<TokenUsageRecord> Records
        {
            get
            {
                lock (gate)
                {
                    return records.ToArray();
                }
            }
        }

        public Task AddAsync(TokenUsageRecord record, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                records.Add(record);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TokenUsageRecord>> ListByTraceAsync(Guid traceId, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                IReadOnlyList<TokenUsageRecord> filtered = records
                    .Where(r => r.TraceId == traceId)
                    .OrderBy(r => r.RecordedAtUtc)
                    .ThenBy(r => r.Id)
                    .ToArray();
                return Task.FromResult(filtered);
            }
        }
    }

    private sealed class FakeAgentConfigRepository(AgentConfig agentConfig) : IAgentConfigRepository
    {
        public Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default)
            => Task.FromResult(agentConfig);

        public Task<int> CreateNewVersionAsync(string key, string configJson, string? createdBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> RetireAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentConfig> CreateForkAsync(string sourceKey, int sourceVersion, string workflowKey, string configJson, string? createdBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> CreatePublishedVersionAsync(string targetKey, string configJson, string forkedFromKey, int forkedFromVersion, string? createdBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeRoleResolutionService : IRoleResolutionService
    {
        public Task<ResolvedAgentTools> ResolveAsync(string agentKey, CancellationToken cancellationToken = default)
            => Task.FromResult(ResolvedAgentTools.Empty);
    }

    private sealed class RecordingArtifactStore(string initialContent) : IArtifactStore
    {
        public List<(Uri Uri, ArtifactMetadata Metadata, string Content)> Writes { get; } = [];

        public Task<ArtifactMetadata> GetMetadataAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            var write = Writes.Single(entry => entry.Uri == uri);
            return Task.FromResult(write.Metadata);
        }

        public Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(initialContent)));

        public async Task<Uri> WriteAsync(Stream content, ArtifactMetadata metadata, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            var uri = new Uri($"file:///tmp/{metadata.TraceId:N}/{metadata.RoundId:N}/{metadata.ArtifactId:N}.bin");
            Writes.Add((uri, metadata, text));
            return uri;
        }
    }
}
