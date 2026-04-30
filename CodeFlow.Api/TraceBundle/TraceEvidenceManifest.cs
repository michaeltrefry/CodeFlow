namespace CodeFlow.Api.TraceBundle;

/// <summary>
/// Top-level manifest for a portable trace evidence bundle (sc-271). Lives at
/// <c>manifest.json</c> inside the exported zip; everything in the bundle is reachable
/// from this object via either an embedded record or a <see cref="TraceEvidenceArtifactRef"/>
/// pointer to a file under <c>artifacts/</c>.
///
/// The schema is versioned so future readers can refuse incompatible bundles cleanly.
/// </summary>
/// <param name="SchemaVersion">
/// Constant for v1 bundles: <see cref="TraceEvidenceBundleDefaults.SchemaVersionV1"/>.
/// </param>
/// <param name="GeneratedAtUtc">UTC instant the bundle was assembled.</param>
/// <param name="Trace">Trace-scoped evidence (sagas, decisions, refusals, authority, usage).</param>
/// <param name="Artifacts">Flat list of artifact refs the manifest's pointers reference.</param>
public sealed record TraceEvidenceManifest(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    TraceEvidenceTraceSummary Trace,
    IReadOnlyList<TraceEvidenceArtifactRef> Artifacts);

public static class TraceEvidenceBundleDefaults
{
    public const string SchemaVersionV1 = "codeflow.trace-bundle.v1";
    public const string ManifestFileName = "manifest.json";
    public const string ArtifactsDirectory = "artifacts/";
}

public sealed record TraceEvidenceTraceSummary(
    Guid TraceId,
    TraceEvidenceSagaSummary RootSaga,
    IReadOnlyList<TraceEvidenceSagaSummary> SubflowSagas,
    IReadOnlyList<TraceEvidenceDecision> Decisions,
    IReadOnlyList<TraceEvidenceRefusal> Refusals,
    IReadOnlyList<TraceEvidenceAuthoritySnapshot> AuthoritySnapshots,
    TraceEvidenceTokenUsageSummary TokenUsage);

public sealed record TraceEvidenceSagaSummary(
    Guid CorrelationId,
    Guid TraceId,
    Guid? ParentTraceId,
    int SubflowDepth,
    string WorkflowKey,
    int WorkflowVersion,
    string CurrentState,
    string? FailureReason,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyDictionary<string, int> PinnedAgentVersions);

public sealed record TraceEvidenceDecision(
    Guid SagaCorrelationId,
    int Ordinal,
    Guid TraceId,
    string AgentKey,
    int AgentVersion,
    string Decision,
    string? DecisionPayloadJson,
    Guid RoundId,
    DateTime RecordedAtUtc,
    Guid? NodeId,
    string? OutputPortName,
    DateTime? NodeEnteredAtUtc,
    TraceEvidenceArtifactPointer? Input,
    TraceEvidenceArtifactPointer? Output);

/// <summary>
/// Pointer from a manifest field to a deduplicated artifact under the bundle's
/// <c>artifacts/</c> directory. Carries enough metadata for a verifier to confirm the
/// bundle wasn't tampered with after export: SHA-256 of the bytes plus the byte count.
/// </summary>
public sealed record TraceEvidenceArtifactPointer(
    string OriginalRef,
    string Sha256,
    long SizeBytes,
    string BundlePath);

public sealed record TraceEvidenceRefusal(
    Guid Id,
    Guid? TraceId,
    Guid? AssistantConversationId,
    string Stage,
    string Code,
    string Reason,
    string? Axis,
    string? Path,
    string? DetailJson,
    DateTime OccurredAtUtc);

public sealed record TraceEvidenceAuthoritySnapshot(
    Guid Id,
    Guid TraceId,
    Guid RoundId,
    string AgentKey,
    int? AgentVersion,
    string? WorkflowKey,
    int? WorkflowVersion,
    string EnvelopeJson,
    string BlockedAxesJson,
    string TiersJson,
    DateTime ResolvedAtUtc);

public sealed record TraceEvidenceTokenUsageSummary(
    int RecordCount,
    IReadOnlyList<TraceEvidenceTokenUsageRecord> Records);

public sealed record TraceEvidenceTokenUsageRecord(
    Guid Id,
    Guid TraceId,
    Guid NodeId,
    Guid InvocationId,
    string Provider,
    string Model,
    DateTime RecordedAtUtc,
    string UsageJson);

/// <summary>
/// One entry in <see cref="TraceEvidenceManifest.Artifacts"/>. The flat list is the
/// canonical inventory the verifier walks; manifest decisions (and any future evidence
/// that points at an artifact) carry a <see cref="TraceEvidenceArtifactPointer"/> with
/// the same <see cref="BundlePath"/> + <see cref="Sha256"/>.
/// </summary>
public sealed record TraceEvidenceArtifactRef(
    string BundlePath,
    string Sha256,
    long SizeBytes,
    string? ContentType,
    string OriginalRef);
