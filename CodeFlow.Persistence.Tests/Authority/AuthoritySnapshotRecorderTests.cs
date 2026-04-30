using System.Text.Json;
using CodeFlow.Persistence.Authority;
using CodeFlow.Runtime.Authority;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests.Authority;

/// <summary>
/// Round-trip tests for <see cref="AuthoritySnapshotRecorder"/> + <see cref="AgentInvocationAuthorityRepository"/>.
/// Real MariaDB via Testcontainers so the migration, indexes, and JSON column types are
/// exercised against the same schema production sees.
/// </summary>
public sealed class AuthoritySnapshotRecorderTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private string? connectionString;

    public async Task InitializeAsync()
    {
        await mariaDbContainer.StartAsync();
        connectionString = mariaDbContainer.GetConnectionString();

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task RecordsSnapshot_AndRepositoryRoundTrips()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var resolver = new StubResolver(new EnvelopeResolutionResult(
            Envelope: WorkflowExecutionEnvelope.NoOpinion with
            {
                ToolGrants = new[] { new ToolGrant("apply_patch", ToolGrant.CategoryHost) }
            },
            BlockedAxes: Array.Empty<BlockedBy>(),
            Tiers: new[]
            {
                new EnvelopeTier(Tiers.Role, WorkflowExecutionEnvelope.NoOpinion with
                {
                    ToolGrants = new[] { new ToolGrant("apply_patch", ToolGrant.CategoryHost) }
                })
            }));

        await using var dbContext = CreateDbContext();
        var recorder = new AuthoritySnapshotRecorder(
            resolver,
            dbContext,
            refusalSink: null,
            NullLogger<AuthoritySnapshotRecorder>.Instance);

        var resolution = await recorder.ResolveAndRecordAsync(new AuthoritySnapshotInput(
            AgentKey: "dev",
            TraceId: traceId,
            RoundId: roundId,
            WorkflowKey: "scaffold",
            WorkflowVersion: 1));

        resolution.Envelope.ToolGrants.Should().ContainSingle();

        await using var verify = CreateDbContext();
        var repo = new AgentInvocationAuthorityRepository(verify);
        var stored = await repo.GetByRoundAsync(traceId, roundId);

        stored.Should().NotBeNull();
        stored!.AgentKey.Should().Be("dev");
        stored.WorkflowKey.Should().Be("scaffold");
        stored.WorkflowVersion.Should().Be(1);
        stored.EnvelopeJson.Should().Contain("apply_patch");
        stored.BlockedAxesJson.Should().Be("[]");
        stored.TiersJson.Should().Contain("\"name\":\"role\"");
    }

    [Fact]
    public async Task EmitsAdmissionRefusals_PerBlockedAxis_WhenSinkProvided()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();

        var blocked = new[]
        {
            new BlockedBy(
                Tier: Tiers.Tenant,
                Axis: BlockedBy.Axes.ToolGrants,
                Code: BlockedBy.Codes.TierRemoved,
                Reason: "Tenant denies apply_patch.",
                RequestedValue: "Host:apply_patch"),
            new BlockedBy(
                Tier: Tiers.Context,
                Axis: BlockedBy.Axes.Budget,
                Code: BlockedBy.Codes.Narrowed,
                Reason: "Budget MaxTokens narrowed.",
                RequestedValue: "100000",
                AllowedValue: "5000")
        };

        var resolver = new StubResolver(new EnvelopeResolutionResult(
            Envelope: WorkflowExecutionEnvelope.NoOpinion,
            BlockedAxes: blocked,
            Tiers: Array.Empty<EnvelopeTier>()));

        var sink = new RecordingSink();
        await using var dbContext = CreateDbContext();
        var recorder = new AuthoritySnapshotRecorder(
            resolver,
            dbContext,
            sink,
            NullLogger<AuthoritySnapshotRecorder>.Instance);

        await recorder.ResolveAndRecordAsync(new AuthoritySnapshotInput(
            AgentKey: "dev",
            TraceId: traceId,
            RoundId: roundId));

        sink.Recorded.Should().HaveCount(2);
        sink.Recorded.Should().OnlyContain(r =>
            r.Stage == RefusalStages.Admission
            && r.TraceId == traceId
            && r.AssistantConversationId == null);
        sink.Recorded.Select(r => r.Code).Should().BeEquivalentTo(
            new[] { BlockedBy.Codes.TierRemoved, BlockedBy.Codes.Narrowed });
        sink.Recorded.Select(r => r.Path).Should().BeEquivalentTo(new[] { Tiers.Tenant, Tiers.Context });
    }

    [Fact]
    public async Task NoBlockedAxes_DoesNotCallSink()
    {
        var resolver = new StubResolver(new EnvelopeResolutionResult(
            Envelope: WorkflowExecutionEnvelope.NoOpinion,
            BlockedAxes: Array.Empty<BlockedBy>(),
            Tiers: Array.Empty<EnvelopeTier>()));

        var sink = new RecordingSink();
        await using var dbContext = CreateDbContext();
        var recorder = new AuthoritySnapshotRecorder(
            resolver,
            dbContext,
            sink,
            NullLogger<AuthoritySnapshotRecorder>.Instance);

        await recorder.ResolveAndRecordAsync(new AuthoritySnapshotInput(
            AgentKey: "dev",
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid()));

        sink.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task RepositoryListByTrace_OrdersByResolvedAt()
    {
        var traceId = Guid.NewGuid();
        var resolver = new StubResolver(new EnvelopeResolutionResult(
            Envelope: WorkflowExecutionEnvelope.NoOpinion,
            BlockedAxes: Array.Empty<BlockedBy>(),
            Tiers: Array.Empty<EnvelopeTier>()));

        // Use ascending fixed timestamps to make ordering deterministic.
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        for (var i = 0; i < 3; i++)
        {
            var index = i;
            await using var dbContext = CreateDbContext();
            var recorder = new AuthoritySnapshotRecorder(
                resolver,
                dbContext,
                refusalSink: null,
                NullLogger<AuthoritySnapshotRecorder>.Instance,
                () => t0.AddMinutes(index));

            await recorder.ResolveAndRecordAsync(new AuthoritySnapshotInput(
                AgentKey: $"agent-{i}",
                TraceId: traceId,
                RoundId: Guid.NewGuid()));
        }

        await using var verify = CreateDbContext();
        var repo = new AgentInvocationAuthorityRepository(verify);
        var rows = await repo.ListByTraceAsync(traceId);

        rows.Should().HaveCount(3);
        rows.Select(r => r.AgentKey).Should().BeEquivalentTo(
            new[] { "agent-0", "agent-1", "agent-2" },
            opts => opts.WithStrictOrdering());
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }

    private sealed class StubResolver : IAuthorityResolver
    {
        private readonly EnvelopeResolutionResult result;

        public StubResolver(EnvelopeResolutionResult result) => this.result = result;

        public Task<EnvelopeResolutionResult> ResolveAsync(
            ResolveAuthorityRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class RecordingSink : IRefusalEventSink
    {
        public List<RefusalEvent> Recorded { get; } = new();

        public Task RecordAsync(RefusalEvent refusal, CancellationToken cancellationToken = default)
        {
            Recorded.Add(refusal);
            return Task.CompletedTask;
        }
    }
}
