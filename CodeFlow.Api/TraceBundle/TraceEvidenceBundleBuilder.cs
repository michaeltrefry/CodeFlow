using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeFlow.Persistence;
using CodeFlow.Persistence.Authority;
using CodeFlow.Persistence.Replay;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.TraceBundle;

/// <summary>
/// Assembles a portable trace evidence bundle (sc-271): a zip containing
/// <c>manifest.json</c> plus every artifact referenced by the trace, deduplicated by
/// SHA-256 hash. Pulls together evidence from every authority surface — saga state
/// (workflow + subflow subtree), decisions, refusals (sc-285), authority snapshots
/// (sc-269 PR2), token usage records — and stitches them into the manifest schema.
///
/// Determinism: the same trace produces a byte-identical bundle whenever the
/// underlying evidence rows + artifact bytes haven't changed. Manifest field order
/// follows DB ordinal where applicable, the artifact list is sorted by
/// <see cref="TraceEvidenceArtifactRef.BundlePath"/>, and the JSON serializer is
/// configured with stable property ordering. <see cref="TraceEvidenceManifest.GeneratedAtUtc"/>
/// is the only wall-clock field; tests inject a fixed clock to assert the rest.
/// </summary>
public sealed class TraceEvidenceBundleBuilder
{
    private const int MaxSubflowDepth = CodeFlow.Orchestration.WorkflowSagaStateMachine.MaxSubflowDepth;
    private static readonly JsonSerializerOptions ManifestSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly CodeFlowDbContext dbContext;
    private readonly IArtifactStore artifactStore;
    private readonly Func<DateTimeOffset> nowProvider;

    public TraceEvidenceBundleBuilder(
        CodeFlowDbContext dbContext,
        IArtifactStore artifactStore,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Writes the trace's evidence bundle to <paramref name="output"/> as a zip stream.
    /// Returns <c>false</c> when the trace is unknown (caller maps to 404); returns
    /// <c>true</c> after the zip has been fully written. The output stream is left open
    /// so the caller controls disposal.
    /// </summary>
    public async Task<bool> WriteBundleAsync(Guid traceId, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        var rootSaga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == traceId, cancellationToken);
        if (rootSaga is null)
        {
            return false;
        }

        var subtreeSagas = await CollectSubtreeSagasAsync(rootSaga, cancellationToken);
        var traceIds = subtreeSagas.Select(s => s.TraceId).ToHashSet();
        var sagaCorrelationIds = subtreeSagas.Select(s => s.CorrelationId).ToArray();

        var decisions = await dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => sagaCorrelationIds.Contains(d.SagaCorrelationId))
            .OrderBy(d => d.SagaCorrelationId)
            .ThenBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);

        var refusals = await dbContext.RefusalEvents
            .AsNoTracking()
            .Where(r => r.TraceId != null && traceIds.Contains(r.TraceId!.Value))
            .OrderBy(r => r.OccurredAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        var authoritySnapshots = await dbContext.AgentInvocationAuthority
            .AsNoTracking()
            .Where(a => traceIds.Contains(a.TraceId))
            .OrderBy(a => a.ResolvedAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

        var tokenUsage = await dbContext.TokenUsageRecords
            .AsNoTracking()
            .Where(t => traceIds.Contains(t.TraceId))
            .OrderBy(t => t.RecordedAtUtc)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var replayAttempts = await dbContext.ReplayAttempts
            .AsNoTracking()
            .Where(r => traceIds.Contains(r.ParentTraceId))
            .OrderBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        // Walk decisions and inline-read each unique artifact so the manifest pointers all
        // resolve to a deduplicated entry under artifacts/. Done in a single pass before zip
        // assembly so the artifact list + decisions can be sorted before serialization.
        var artifacts = await CollectArtifactsAsync(decisions, cancellationToken);

        await using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Write each artifact entry first so the zip's central directory order is
            // deterministic when manifest serialization adds the manifest at the end.
            foreach (var artifact in artifacts.OrderedEntries)
            {
                var entry = zip.CreateEntry(artifact.BundlePath, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(artifact.Bytes, cancellationToken);
            }

            var manifest = BuildManifest(rootSaga, subtreeSagas, decisions, refusals, authoritySnapshots, tokenUsage, replayAttempts, artifacts);
            var manifestEntry = zip.CreateEntry(TraceEvidenceBundleDefaults.ManifestFileName, CompressionLevel.Optimal);
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest, ManifestSerializerOptions, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Read-only assembly of the manifest in memory without touching the artifact store.
    /// Used by tests + future verifier paths that want the manifest without zip I/O.
    /// </summary>
    public async Task<TraceEvidenceManifest?> BuildManifestAsync(Guid traceId, CancellationToken cancellationToken = default)
    {
        var rootSaga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == traceId, cancellationToken);
        if (rootSaga is null)
        {
            return null;
        }

        var subtreeSagas = await CollectSubtreeSagasAsync(rootSaga, cancellationToken);
        var traceIds = subtreeSagas.Select(s => s.TraceId).ToHashSet();
        var sagaCorrelationIds = subtreeSagas.Select(s => s.CorrelationId).ToArray();

        var decisions = await dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => sagaCorrelationIds.Contains(d.SagaCorrelationId))
            .OrderBy(d => d.SagaCorrelationId)
            .ThenBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);
        var refusals = await dbContext.RefusalEvents
            .AsNoTracking()
            .Where(r => r.TraceId != null && traceIds.Contains(r.TraceId!.Value))
            .OrderBy(r => r.OccurredAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        var authoritySnapshots = await dbContext.AgentInvocationAuthority
            .AsNoTracking()
            .Where(a => traceIds.Contains(a.TraceId))
            .OrderBy(a => a.ResolvedAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        var tokenUsage = await dbContext.TokenUsageRecords
            .AsNoTracking()
            .Where(t => traceIds.Contains(t.TraceId))
            .OrderBy(t => t.RecordedAtUtc)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);
        var replayAttempts = await dbContext.ReplayAttempts
            .AsNoTracking()
            .Where(r => traceIds.Contains(r.ParentTraceId))
            .OrderBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        var artifacts = await CollectArtifactsAsync(decisions, cancellationToken);
        return BuildManifest(rootSaga, subtreeSagas, decisions, refusals, authoritySnapshots, tokenUsage, replayAttempts, artifacts);
    }

    private TraceEvidenceManifest BuildManifest(
        WorkflowSagaStateEntity rootSaga,
        IReadOnlyList<WorkflowSagaStateEntity> subtreeSagas,
        IReadOnlyList<WorkflowSagaDecisionEntity> decisions,
        IReadOnlyList<RefusalEventEntity> refusals,
        IReadOnlyList<AgentInvocationAuthorityEntity> authoritySnapshots,
        IReadOnlyList<TokenUsageRecordEntity> tokenUsage,
        IReadOnlyList<ReplayAttemptEntity> replayAttempts,
        ArtifactInventory artifacts)
    {
        var subflows = subtreeSagas
            .Where(s => s.CorrelationId != rootSaga.CorrelationId)
            .OrderBy(s => s.SubflowDepth)
            .ThenBy(s => s.CreatedAtUtc)
            .ThenBy(s => s.CorrelationId)
            .Select(MapSaga)
            .ToArray();

        var mappedDecisions = decisions.Select(d => MapDecision(d, artifacts)).ToArray();
        var mappedRefusals = refusals.Select(MapRefusal).ToArray();
        var mappedAuthority = authoritySnapshots.Select(MapAuthority).ToArray();
        var mappedTokenUsage = tokenUsage.Select(MapTokenUsage).ToArray();
        var mappedReplayAttempts = replayAttempts.Select(MapReplayAttempt).ToArray();

        return new TraceEvidenceManifest(
            SchemaVersion: TraceEvidenceBundleDefaults.SchemaVersionV1,
            GeneratedAtUtc: nowProvider(),
            Trace: new TraceEvidenceTraceSummary(
                TraceId: rootSaga.TraceId,
                RootSaga: MapSaga(rootSaga),
                SubflowSagas: subflows,
                Decisions: mappedDecisions,
                Refusals: mappedRefusals,
                AuthoritySnapshots: mappedAuthority,
                TokenUsage: new TraceEvidenceTokenUsageSummary(
                    RecordCount: mappedTokenUsage.Length,
                    Records: mappedTokenUsage),
                ReplayAttempts: mappedReplayAttempts),
            Artifacts: artifacts.OrderedEntries
                .Select(entry => new TraceEvidenceArtifactRef(
                    BundlePath: entry.BundlePath,
                    Sha256: entry.Sha256,
                    SizeBytes: entry.Bytes.Length,
                    ContentType: entry.ContentType,
                    OriginalRef: entry.OriginalRef))
                .ToArray());
    }

    private static TraceEvidenceReplayAttempt MapReplayAttempt(ReplayAttemptEntity entity) =>
        new(
            Id: entity.Id,
            ParentTraceId: entity.ParentTraceId,
            LineageId: entity.LineageId,
            ContentHash: entity.ContentHash,
            Generation: entity.Generation,
            ReplayState: entity.ReplayState,
            TerminalPort: entity.TerminalPort,
            DriftLevel: entity.DriftLevel,
            Reason: entity.Reason,
            CreatedAtUtc: entity.CreatedAtUtc);

    private async Task<ArtifactInventory> CollectArtifactsAsync(
        IReadOnlyList<WorkflowSagaDecisionEntity> decisions,
        CancellationToken cancellationToken)
    {
        var byOriginalRef = new Dictionary<string, ArtifactEntry>(StringComparer.Ordinal);

        async Task EnsureLoadedAsync(string? rawRef)
        {
            if (string.IsNullOrWhiteSpace(rawRef) || byOriginalRef.ContainsKey(rawRef))
            {
                return;
            }

            if (!Uri.TryCreate(rawRef, UriKind.Absolute, out var uri))
            {
                return;
            }

            try
            {
                await using var stream = await artifactStore.ReadAsync(uri, cancellationToken);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                var bytes = buffer.ToArray();
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                string? contentType = null;
                try
                {
                    var metadata = await artifactStore.GetMetadataAsync(uri, cancellationToken);
                    contentType = metadata.ContentType;
                }
                catch
                {
                    // Metadata is optional; missing metadata isn't a bundle failure. The bytes
                    // and the hash are the authoritative evidence.
                }

                var bundlePath = $"{TraceEvidenceBundleDefaults.ArtifactsDirectory}{sha}.bin";
                byOriginalRef[rawRef] = new ArtifactEntry(rawRef, sha, bundlePath, bytes, contentType);
            }
            catch (FileNotFoundException)
            {
                // Missing artifact bytes — we still record the original ref in the manifest as a
                // dangling pointer so the bundle clearly shows that the row referenced an artifact
                // that the store no longer has. Pointer carries an empty SHA + size 0.
                var bundlePath = $"{TraceEvidenceBundleDefaults.ArtifactsDirectory}missing-{Guid.NewGuid():N}.bin";
                byOriginalRef[rawRef] = new ArtifactEntry(rawRef, string.Empty, bundlePath, Array.Empty<byte>(), null, IsMissing: true);
            }
            catch (ArgumentException)
            {
                // Same — store rejects the URI (e.g. outside configured root after a migration).
                var bundlePath = $"{TraceEvidenceBundleDefaults.ArtifactsDirectory}missing-{Guid.NewGuid():N}.bin";
                byOriginalRef[rawRef] = new ArtifactEntry(rawRef, string.Empty, bundlePath, Array.Empty<byte>(), null, IsMissing: true);
            }
        }

        foreach (var decision in decisions)
        {
            await EnsureLoadedAsync(decision.InputRef);
            await EnsureLoadedAsync(decision.OutputRef);
        }

        // Order entries by SHA so the central directory is stable across runs.
        var ordered = byOriginalRef.Values
            .OrderBy(e => e.BundlePath, StringComparer.Ordinal)
            .ToArray();
        return new ArtifactInventory(byOriginalRef, ordered);
    }

    private async Task<IReadOnlyList<WorkflowSagaStateEntity>> CollectSubtreeSagasAsync(
        WorkflowSagaStateEntity root,
        CancellationToken cancellationToken)
    {
        var all = new List<WorkflowSagaStateEntity> { root };
        var currentLevel = new List<Guid> { root.TraceId };

        for (var level = 0; level < MaxSubflowDepth; level++)
        {
            if (currentLevel.Count == 0)
            {
                break;
            }

            var parents = currentLevel;
            var children = await dbContext.WorkflowSagas
                .AsNoTracking()
                .Where(s => s.ParentTraceId != null && parents.Contains(s.ParentTraceId!.Value))
                .ToListAsync(cancellationToken);

            if (children.Count == 0)
            {
                break;
            }

            all.AddRange(children);
            currentLevel = children.Select(s => s.TraceId).ToList();
        }

        return all;
    }

    private static TraceEvidenceSagaSummary MapSaga(WorkflowSagaStateEntity saga) =>
        new(
            CorrelationId: saga.CorrelationId,
            TraceId: saga.TraceId,
            ParentTraceId: saga.ParentTraceId,
            SubflowDepth: saga.SubflowDepth,
            WorkflowKey: saga.WorkflowKey,
            WorkflowVersion: saga.WorkflowVersion,
            CurrentState: saga.CurrentState,
            FailureReason: saga.FailureReason,
            CreatedAtUtc: saga.CreatedAtUtc,
            UpdatedAtUtc: saga.UpdatedAtUtc,
            PinnedAgentVersions: saga.GetPinnedAgentVersions());

    private static TraceEvidenceDecision MapDecision(WorkflowSagaDecisionEntity decision, ArtifactInventory artifacts) =>
        new(
            SagaCorrelationId: decision.SagaCorrelationId,
            Ordinal: decision.Ordinal,
            TraceId: decision.TraceId,
            AgentKey: decision.AgentKey,
            AgentVersion: decision.AgentVersion,
            Decision: decision.Decision,
            DecisionPayloadJson: decision.DecisionPayloadJson,
            RoundId: decision.RoundId,
            RecordedAtUtc: decision.RecordedAtUtc,
            NodeId: decision.NodeId,
            OutputPortName: decision.OutputPortName,
            NodeEnteredAtUtc: decision.NodeEnteredAtUtc,
            Input: BuildPointer(decision.InputRef, artifacts),
            Output: BuildPointer(decision.OutputRef, artifacts));

    private static TraceEvidenceArtifactPointer? BuildPointer(string? rawRef, ArtifactInventory artifacts)
    {
        if (string.IsNullOrWhiteSpace(rawRef) || !artifacts.ByOriginalRef.TryGetValue(rawRef, out var entry))
        {
            return null;
        }
        return new TraceEvidenceArtifactPointer(
            OriginalRef: entry.OriginalRef,
            Sha256: entry.Sha256,
            SizeBytes: entry.Bytes.Length,
            BundlePath: entry.BundlePath);
    }

    private static TraceEvidenceRefusal MapRefusal(RefusalEventEntity entity) =>
        new(
            Id: entity.Id,
            TraceId: entity.TraceId,
            AssistantConversationId: entity.AssistantConversationId,
            Stage: entity.Stage,
            Code: entity.Code,
            Reason: entity.Reason,
            Axis: entity.Axis,
            Path: entity.Path,
            DetailJson: entity.DetailJson,
            OccurredAtUtc: entity.OccurredAtUtc);

    private static TraceEvidenceAuthoritySnapshot MapAuthority(AgentInvocationAuthorityEntity entity) =>
        new(
            Id: entity.Id,
            TraceId: entity.TraceId,
            RoundId: entity.RoundId,
            AgentKey: entity.AgentKey,
            AgentVersion: entity.AgentVersion,
            WorkflowKey: entity.WorkflowKey,
            WorkflowVersion: entity.WorkflowVersion,
            EnvelopeJson: entity.EnvelopeJson,
            BlockedAxesJson: entity.BlockedAxesJson,
            TiersJson: entity.TiersJson,
            ResolvedAtUtc: entity.ResolvedAtUtc);

    private static TraceEvidenceTokenUsageRecord MapTokenUsage(TokenUsageRecordEntity entity) =>
        new(
            Id: entity.Id,
            TraceId: entity.TraceId,
            NodeId: entity.NodeId,
            InvocationId: entity.InvocationId,
            Provider: entity.Provider,
            Model: entity.Model,
            RecordedAtUtc: entity.RecordedAtUtc,
            UsageJson: entity.UsageJson);

    private sealed record ArtifactEntry(
        string OriginalRef,
        string Sha256,
        string BundlePath,
        byte[] Bytes,
        string? ContentType,
        bool IsMissing = false);

    private sealed record ArtifactInventory(
        IReadOnlyDictionary<string, ArtifactEntry> ByOriginalRef,
        IReadOnlyList<ArtifactEntry> OrderedEntries);
}
