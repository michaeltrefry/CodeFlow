using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeFlow.Api.TraceBundle;
using CodeFlow.Persistence;
using CodeFlow.Persistence.Authority;
using CodeFlow.Persistence.Replay;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.TraceBundle;

/// <summary>
/// sc-271 — covers <see cref="TraceEvidenceBundleBuilder"/> against an in-memory DbContext
/// + an in-memory artifact store stub. Exercises every shape the manifest carries: saga
/// summary, decision-with-artifact pointer, refusal, authority snapshot, token usage,
/// artifact deduplication, dangling-artifact handling, hash stability, and 404 mapping
/// for an unknown trace.
/// </summary>
public sealed class TraceEvidenceBundleBuilderTests
{
    [Fact]
    public async Task WriteBundle_UnknownTrace_ReturnsFalse()
    {
        await using var dbContext = CreateDbContext();
        var builder = new TraceEvidenceBundleBuilder(dbContext, new InMemoryArtifactStore());

        using var output = new MemoryStream();
        var found = await builder.WriteBundleAsync(Guid.NewGuid(), output);

        found.Should().BeFalse();
        output.Length.Should().Be(0);
    }

    [Fact]
    public async Task WriteBundle_HappyPath_ProducesZipWithManifestAndDeduplicatedArtifacts()
    {
        await using var dbContext = CreateDbContext();
        var artifactStore = new InMemoryArtifactStore();
        var (traceId, sagaCorrelationId) = SeedRootSaga(dbContext);
        var inputUri = artifactStore.Write("input bytes"u8.ToArray(), "text/plain");
        var outputUri = artifactStore.Write("output bytes"u8.ToArray(), "text/plain");
        // Second decision shares the input ref → exercises deduplication. Two distinct outputs.
        var output2Uri = artifactStore.Write("output two"u8.ToArray(), "text/plain");
        SeedDecision(dbContext, sagaCorrelationId, traceId, ordinal: 1, inputUri, outputUri, agentKey: "agent-a");
        SeedDecision(dbContext, sagaCorrelationId, traceId, ordinal: 2, inputUri, output2Uri, agentKey: "agent-a");
        SeedRefusal(dbContext, traceId, code: "envelope-execute-grants", stage: "tool");
        SeedAuthoritySnapshot(dbContext, traceId, agentKey: "agent-a");
        SeedTokenUsageRecord(dbContext, traceId);
        await dbContext.SaveChangesAsync();

        var fixedNow = DateTimeOffset.Parse("2026-04-30T17:00:00Z");
        var builder = new TraceEvidenceBundleBuilder(dbContext, artifactStore, () => fixedNow);

        using var bufferedOutput = new MemoryStream();
        var found = await builder.WriteBundleAsync(traceId, bufferedOutput);
        found.Should().BeTrue();

        bufferedOutput.Position = 0;
        using var archive = new ZipArchive(bufferedOutput, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry(TraceEvidenceBundleDefaults.ManifestFileName);
        manifestEntry.Should().NotBeNull("the manifest must be in the zip at a stable path");
        await using var manifestStream = manifestEntry!.Open();
        var manifest = await JsonSerializer.DeserializeAsync<TraceEvidenceManifest>(
            manifestStream, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        manifest.Should().NotBeNull();
        manifest!.SchemaVersion.Should().Be(TraceEvidenceBundleDefaults.SchemaVersionV1);
        manifest.GeneratedAtUtc.Should().Be(fixedNow);
        manifest.Trace.TraceId.Should().Be(traceId);
        manifest.Trace.RootSaga.WorkflowKey.Should().Be("demo");
        manifest.Trace.Decisions.Should().HaveCount(2);
        manifest.Trace.Refusals.Should().ContainSingle()
            .Which.Code.Should().Be("envelope-execute-grants");
        manifest.Trace.AuthoritySnapshots.Should().ContainSingle();
        manifest.Trace.TokenUsage.Records.Should().ContainSingle();

        // Deduplication: two decisions share the input artifact, so only one entry per unique
        // payload (3 unique artifacts: input, output1, output2 — two writes go to the input
        // because the second decision points at the same URI).
        manifest.Artifacts.Should().HaveCount(3);
        var inputPointer = manifest.Trace.Decisions[0].Input;
        var inputPointer2 = manifest.Trace.Decisions[1].Input;
        inputPointer.Should().NotBeNull();
        inputPointer2.Should().NotBeNull();
        inputPointer!.BundlePath.Should().Be(inputPointer2!.BundlePath);
        inputPointer.Sha256.Should().NotBeEmpty();

        // Each artifact entry should have a real zip entry with bytes matching the manifest's
        // declared SHA-256 + size.
        foreach (var artifact in manifest.Artifacts)
        {
            var entry = archive.GetEntry(artifact.BundlePath);
            entry.Should().NotBeNull($"bundle entry {artifact.BundlePath} must exist in zip");
            await using var entryStream = entry!.Open();
            using var memory = new MemoryStream();
            await entryStream.CopyToAsync(memory);
            var bytes = memory.ToArray();
            bytes.LongLength.Should().Be(artifact.SizeBytes);
            var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            actualSha.Should().Be(artifact.Sha256);
        }
    }

    [Fact]
    public async Task WriteBundle_HashStability_TwoCallsWithFixedClockProduceIdenticalManifestBytes()
    {
        await using var dbContext = CreateDbContext();
        var artifactStore = new InMemoryArtifactStore();
        var (traceId, sagaCorrelationId) = SeedRootSaga(dbContext);
        var inputUri = artifactStore.Write("hello"u8.ToArray(), "text/plain");
        var outputUri = artifactStore.Write("world"u8.ToArray(), "text/plain");
        SeedDecision(dbContext, sagaCorrelationId, traceId, ordinal: 1, inputUri, outputUri, agentKey: "agent-a");
        await dbContext.SaveChangesAsync();

        var fixedNow = DateTimeOffset.Parse("2026-04-30T17:00:00Z");
        var builder = new TraceEvidenceBundleBuilder(dbContext, artifactStore, () => fixedNow);

        var first = await builder.BuildManifestAsync(traceId);
        var second = await builder.BuildManifestAsync(traceId);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonSerializer.Serialize(first, jsonOptions)
            .Should().Be(JsonSerializer.Serialize(second, jsonOptions));
    }

    [Fact]
    public async Task BuildManifest_IncludesReplayAttempts_OrderedByCreatedAt()
    {
        // sc-275: replay-attempt rows persisted by the replay endpoint surface in the bundle
        // manifest under `trace.replayAttempts`, ordered chronologically. The bundle is the
        // canonical export, so attempt history travels with the trace.
        await using var dbContext = CreateDbContext();
        var artifactStore = new InMemoryArtifactStore();
        var (traceId, _) = SeedRootSaga(dbContext);
        var earlier = DateTime.Parse("2026-04-29T10:00:00Z").ToUniversalTime();
        var later = DateTime.Parse("2026-04-30T10:00:00Z").ToUniversalTime();

        var lineageId = Guid.NewGuid();
        dbContext.ReplayAttempts.Add(new ReplayAttemptEntity
        {
            Id = Guid.NewGuid(),
            ParentTraceId = traceId,
            LineageId = lineageId,
            ContentHash = new string('a', 64),
            Generation = 1,
            ReplayState = "Completed",
            TerminalPort = "Completed",
            DriftLevel = "None",
            Reason = "ui:replay-panel",
            CreatedAtUtc = later,
        });
        dbContext.ReplayAttempts.Add(new ReplayAttemptEntity
        {
            Id = Guid.NewGuid(),
            ParentTraceId = traceId,
            LineageId = lineageId,
            ContentHash = new string('a', 64),
            Generation = 1,
            ReplayState = "Completed",
            TerminalPort = "Completed",
            DriftLevel = "None",
            Reason = "ui:replay-panel",
            CreatedAtUtc = earlier,
        });
        await dbContext.SaveChangesAsync();

        var builder = new TraceEvidenceBundleBuilder(dbContext, artifactStore);
        var manifest = await builder.BuildManifestAsync(traceId);

        manifest.Should().NotBeNull();
        manifest!.Trace.ReplayAttempts.Should().HaveCount(2);
        manifest.Trace.ReplayAttempts[0].CreatedAtUtc.Should().Be(earlier);
        manifest.Trace.ReplayAttempts[1].CreatedAtUtc.Should().Be(later);
        manifest.Trace.ReplayAttempts.All(r => r.LineageId == lineageId).Should().BeTrue();
        manifest.Trace.ReplayAttempts.All(r => r.Reason == "ui:replay-panel").Should().BeTrue();
    }

    [Fact]
    public async Task BuildManifest_MissingArtifactBytes_RecordsDanglingPointerWithEmptySha()
    {
        // FileSystemArtifactStore throws FileNotFoundException when an old trace's recorded
        // ref points at a path the current store no longer resolves. The builder records a
        // dangling pointer rather than failing the whole bundle.
        await using var dbContext = CreateDbContext();
        var artifactStore = new InMemoryArtifactStore();
        var (traceId, sagaCorrelationId) = SeedRootSaga(dbContext);
        var missingUri = new Uri("file:///does-not-exist.bin");
        SeedDecisionRaw(
            dbContext,
            sagaCorrelationId,
            traceId,
            ordinal: 1,
            inputRef: null,
            outputRef: missingUri.ToString(),
            agentKey: "agent-a");
        await dbContext.SaveChangesAsync();

        var builder = new TraceEvidenceBundleBuilder(dbContext, artifactStore);
        var manifest = await builder.BuildManifestAsync(traceId);

        manifest.Should().NotBeNull();
        manifest!.Artifacts.Should().ContainSingle()
            .Which.Sha256.Should().BeEmpty();
        manifest.Trace.Decisions[0].Output.Should().NotBeNull()
            .And.Subject.As<TraceEvidenceArtifactPointer>().Sha256.Should().BeEmpty();
    }

    private static CodeFlowDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
            .UseInMemoryDatabase($"trace-bundle-tests-{Guid.NewGuid():N}")
            .Options;
        return new CodeFlowDbContext(options);
    }

    private static (Guid TraceId, Guid SagaCorrelationId) SeedRootSaga(CodeFlowDbContext db)
    {
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        db.WorkflowSagas.Add(new WorkflowSagaStateEntity
        {
            CorrelationId = correlationId,
            TraceId = traceId,
            CurrentState = "Completed",
            CurrentNodeId = Guid.NewGuid(),
            CurrentAgentKey = "agent-a",
            CurrentRoundId = Guid.NewGuid(),
            RoundCount = 1,
            AgentVersionsJson = """{"agent-a":1}""",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = 0,
            LogicEvaluationCount = 0,
            WorkflowKey = "demo",
            WorkflowVersion = 1,
            InputsJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Version = 1,
        });
        return (traceId, correlationId);
    }

    private static void SeedDecision(
        CodeFlowDbContext db,
        Guid sagaCorrelationId,
        Guid traceId,
        int ordinal,
        Uri inputUri,
        Uri outputUri,
        string agentKey) =>
        SeedDecisionRaw(db, sagaCorrelationId, traceId, ordinal, inputUri.ToString(), outputUri.ToString(), agentKey);

    private static void SeedDecisionRaw(
        CodeFlowDbContext db,
        Guid sagaCorrelationId,
        Guid traceId,
        int ordinal,
        string? inputRef,
        string? outputRef,
        string agentKey)
    {
        db.WorkflowSagaDecisions.Add(new WorkflowSagaDecisionEntity
        {
            SagaCorrelationId = sagaCorrelationId,
            Ordinal = ordinal,
            TraceId = traceId,
            AgentKey = agentKey,
            AgentVersion = 1,
            Decision = "Completed",
            DecisionPayloadJson = null,
            RoundId = Guid.NewGuid(),
            RecordedAtUtc = DateTime.UtcNow,
            NodeId = Guid.NewGuid(),
            OutputPortName = "Completed",
            InputRef = inputRef,
            OutputRef = outputRef,
            NodeEnteredAtUtc = DateTime.UtcNow,
        });
    }

    private static void SeedRefusal(CodeFlowDbContext db, Guid traceId, string code, string stage)
    {
        db.RefusalEvents.Add(new RefusalEventEntity
        {
            Id = Guid.NewGuid(),
            TraceId = traceId,
            Stage = stage,
            Code = code,
            Reason = $"refused: {code}",
            Axis = "executeGrants",
            Path = "test",
            DetailJson = null,
            OccurredAtUtc = DateTime.UtcNow,
        });
    }

    private static void SeedAuthoritySnapshot(CodeFlowDbContext db, Guid traceId, string agentKey)
    {
        db.AgentInvocationAuthority.Add(new AgentInvocationAuthorityEntity
        {
            Id = Guid.NewGuid(),
            TraceId = traceId,
            RoundId = Guid.NewGuid(),
            AgentKey = agentKey,
            AgentVersion = 1,
            WorkflowKey = "demo",
            WorkflowVersion = 1,
            EnvelopeJson = "{}",
            BlockedAxesJson = "[]",
            TiersJson = "[]",
            ResolvedAtUtc = DateTime.UtcNow,
        });
    }

    private static void SeedTokenUsageRecord(CodeFlowDbContext db, Guid traceId)
    {
        db.TokenUsageRecords.Add(new TokenUsageRecordEntity
        {
            Id = Guid.NewGuid(),
            TraceId = traceId,
            NodeId = Guid.NewGuid(),
            InvocationId = Guid.NewGuid(),
            ScopeChainJson = "[]",
            Provider = "openai",
            Model = "gpt-5",
            RecordedAtUtc = DateTime.UtcNow,
            UsageJson = """{"input_tokens":10,"output_tokens":5}""",
        });
    }

    private sealed class InMemoryArtifactStore : IArtifactStore
    {
        private readonly Dictionary<Uri, (byte[] Bytes, ArtifactMetadata Metadata)> store = new();

        public Uri Write(byte[] bytes, string contentType)
        {
            var uri = new Uri($"memory:///{Guid.NewGuid():N}.bin");
            store[uri] = (bytes, new ArtifactMetadata(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                ContentType: contentType,
                FileName: null));
            return uri;
        }

        public Task<Uri> WriteAsync(Stream content, ArtifactMetadata metadata, CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            content.CopyTo(memory);
            var uri = new Uri($"memory:///{Guid.NewGuid():N}.bin");
            store[uri] = (memory.ToArray(), metadata);
            return Task.FromResult(uri);
        }

        public Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            if (!store.TryGetValue(uri, out var entry))
            {
                throw new FileNotFoundException($"No artifact at {uri}");
            }
            return Task.FromResult<Stream>(new MemoryStream(entry.Bytes, writable: false));
        }

        public Task<ArtifactMetadata> GetMetadataAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            if (!store.TryGetValue(uri, out var entry))
            {
                throw new FileNotFoundException($"No artifact at {uri}");
            }
            return Task.FromResult(entry.Metadata);
        }
    }
}
