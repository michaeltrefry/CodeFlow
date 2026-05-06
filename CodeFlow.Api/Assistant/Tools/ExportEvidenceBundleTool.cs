using System.Text.Json;
using CodeFlow.Api.Assistant.Artifacts;
using CodeFlow.Api.TraceBundle;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// AA-9 (sc-800): chat-side evidence-bundle export. Generates the same trace evidence
/// bundle the inspector page's "Export Bundle" button does, but writes the zip to the
/// conversation workspace and records an <c>EvidenceBundle</c> artifact event so the chat
/// panel surfaces a downloadable pill / rail row. The existing trace-scoped endpoint
/// (<c>GET /api/traces/{id}/bundle</c>) is untouched — this tool is the homepage-assistant
/// surface for the same artifact.
/// </summary>
/// <remarks>
/// Tool name <c>export_evidence_bundle</c>. Required input: <c>traceId</c>. The tool builds
/// the bundle in memory via <see cref="TraceEvidenceBundleBuilder"/>, writes it to
/// <c>{workspace}/evidence-{traceId:N}-{utcTs}.zip</c>, then registers the artifact event.
/// Falls back to "result-only" mode (no artifact recording) when the conversation has no
/// writable workspace; that path is unusual since the homepage assistant always has a
/// per-chat workspace, but we don't want a missing recorder to break the bundle export.
/// </remarks>
public sealed class ExportEvidenceBundleTool : IAssistantTool
{
    private readonly TraceEvidenceBundleBuilder builder;
    private readonly ToolWorkspaceContext? workspace;
    private readonly IArtifactRecorder? artifactRecorder;

    public ExportEvidenceBundleTool(
        TraceEvidenceBundleBuilder builder,
        ToolWorkspaceContext? workspace = null,
        IArtifactRecorder? artifactRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        this.builder = builder;
        this.workspace = workspace;
        this.artifactRecorder = artifactRecorder;
    }

    public string Name => "export_evidence_bundle";

    public string Description =>
        "Export a portable evidence bundle for a single trace as a zip file. The zip contains " +
        "the saga header, decision timeline, logic-evaluation rows, token-usage records, refusal " +
        "events, and the per-node IO artifacts the inspector exposes — same shape the trace " +
        "page's Export Bundle button produces. The bundle is saved to the conversation's " +
        "workspace and surfaced as a downloadable artifact in chat. Use this when the user asks " +
        "to share or archive a trace, or to attach evidence to a postmortem.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""traceId"": { ""type"": ""string"", ""description"": ""Trace id (GUID, required)."" }
        },
        ""required"": [""traceId""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!GetTraceTool.TryReadTraceId(arguments, "traceId", out var traceId, out var error))
        {
            return error;
        }

        if (workspace is null || artifactRecorder is null)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new
                {
                    error = "No conversation workspace available — cannot export the bundle to disk. "
                        + "This usually means the conversation hasn't had its workspace materialized yet.",
                }),
                IsError: true);
        }

        // Build the zip in memory. Same posture as TraceBundleEndpoints.GetTraceBundleAsync —
        // buffering before streaming so the request scope can complete its DB reads first.
        using var buffer = new MemoryStream();
        var found = await builder.WriteBundleAsync(traceId, buffer, cancellationToken);
        if (!found)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Trace '{traceId}' not found." }),
                IsError: true);
        }

        Directory.CreateDirectory(workspace.RootPath);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var fileName = $"evidence-{traceId:N}-{timestamp}.zip";
        var filePath = Path.Combine(workspace.RootPath, fileName);
        await File.WriteAllBytesAsync(filePath, buffer.ToArray(), cancellationToken);

        var summary = JsonSerializer.Serialize(new
        {
            traceId,
            sizeBytes = buffer.Length,
        });
        await artifactRecorder.RecordAsync(
            conversationId: workspace.CorrelationId,
            kind: ArtifactEventKind.EvidenceBundle,
            name: fileName,
            relativePath: fileName,
            snapshotId: null,
            summaryJson: summary,
            supersedesPriorByName: false,
            cancellationToken: cancellationToken);

        var result = new
        {
            status = "exported",
            traceId,
            artifactName = fileName,
            sizeBytes = buffer.Length,
            message = "Evidence bundle exported. The artifact is downloadable from the chat rail.",
        };
        return new AssistantToolResult(JsonSerializer.Serialize(result, AssistantToolJson.SerializerOptions));
    }
}
