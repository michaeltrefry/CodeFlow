using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Orchestration.Replay;

/// <summary>
/// sc-275 — deterministic content-hash + lineage-id derivation for replay-with-edit
/// requests. The hash is what makes lineage "an inspectable tree": the same edits +
/// mocks + version override + pinned agent versions against the same parent always
/// produce the same lineage id, so the trace inspector and bundle export can show
/// authors when they're re-running an identical replay versus authoring a new one.
///
/// Inputs are canonicalized before hashing: edit list sorted by (agentKey, ordinal),
/// additional mocks sorted by agentKey then list-order, pinned agent versions sorted
/// by key. The serializer is configured with stable property order so byte-equality
/// of inputs implies byte-equality of the canonical JSON, and SHA-256 of that JSON
/// is the content hash.
/// </summary>
public static class ReplayLineageHasher
{
    private static readonly JsonSerializerOptions CanonicalSerializerOptions = new()
    {
        WriteIndented = false,
        // Preserve our canonicalized property order: we hand-build a JsonObject so the
        // serializer just needs to emit fields in insertion order (which it does by default).
    };

    /// <summary>
    /// Compute a stable SHA-256 (lowercase hex, 64 chars) over the canonicalized inputs.
    /// </summary>
    public static string ComputeContentHash(ReplayLineageInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var canonical = BuildCanonicalJson(inputs);
        var bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString(CanonicalSerializerOptions));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Stable Guid derived from <c>SHA-256(parent_trace_id ‖ content_hash)</c>. Identical
    /// edits against the same parent always yield the same lineage id, so authors see
    /// "you've already tried this exact replay" instead of being confused by drift between
    /// otherwise-identical attempts.
    /// </summary>
    public static Guid ComputeLineageId(Guid parentTraceId, string contentHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentHash);

        Span<byte> input = stackalloc byte[16 + 64];
        parentTraceId.TryWriteBytes(input);
        Encoding.ASCII.GetBytes(contentHash, input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static JsonObject BuildCanonicalJson(ReplayLineageInputs inputs)
    {
        var editsArray = new JsonArray(inputs.Edits
            .OrderBy(e => e.AgentKey, StringComparer.Ordinal)
            .ThenBy(e => e.Ordinal)
            .Select(e => (JsonNode)new JsonObject
            {
                ["agentKey"] = e.AgentKey,
                ["ordinal"] = e.Ordinal,
                ["decision"] = e.Decision,
                ["output"] = e.Output,
                ["payload"] = e.Payload?.DeepClone(),
            })
            .ToArray());

        var mocksObject = new JsonObject();
        foreach (var (agentKey, mocks) in inputs.AdditionalMocks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            mocksObject[agentKey] = new JsonArray(mocks
                .Select(m => (JsonNode)new JsonObject
                {
                    ["decision"] = m.Decision,
                    ["output"] = m.Output,
                    ["payload"] = m.Payload?.DeepClone(),
                })
                .ToArray());
        }

        var pinnedObject = new JsonObject();
        foreach (var (agentKey, version) in inputs.PinnedAgentVersions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            pinnedObject[agentKey] = version;
        }

        return new JsonObject
        {
            ["edits"] = editsArray,
            ["additionalMocks"] = mocksObject,
            ["workflowVersionOverride"] = inputs.WorkflowVersionOverride,
            ["pinnedAgentVersions"] = pinnedObject,
            ["force"] = inputs.Force,
        };
    }
}

/// <summary>
/// Canonical input shape for <see cref="ReplayLineageHasher"/>. Mirrors the API replay
/// request body but lives in the orchestration layer so the hasher doesn't depend on
/// <c>CodeFlow.Api</c>.
/// </summary>
public sealed record ReplayLineageInputs(
    IReadOnlyList<ReplayLineageEdit> Edits,
    IReadOnlyDictionary<string, IReadOnlyList<ReplayLineageMock>> AdditionalMocks,
    int? WorkflowVersionOverride,
    IReadOnlyDictionary<string, int> PinnedAgentVersions,
    bool Force);

public sealed record ReplayLineageEdit(
    string AgentKey,
    int Ordinal,
    string? Decision,
    string? Output,
    JsonNode? Payload);

public sealed record ReplayLineageMock(
    string Decision,
    string? Output,
    JsonNode? Payload);
