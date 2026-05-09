using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// AP-4 (sc-835): per-conversation draft store for in-progress agent packages. Sibling of
/// <see cref="WorkflowPackageDraftStore"/>. The four tools
/// (<c>set_agent_package_draft</c>, <c>get_agent_package_draft</c>,
/// <c>patch_agent_package_draft</c>, <c>clear_agent_package_draft</c>) all read/write a
/// single canonical file (<see cref="DraftFileName"/>) under the conversation's per-chat
/// workspace dir. Save then becomes a zero-arg call to <c>save_agent_package</c> that reads
/// the draft from disk — the LLM never re-emits the full payload during refinement.
/// <para/>
/// The workspace passed in MUST be the conversation workspace, not the active workspace
/// (same rationale as <see cref="WorkflowPackageDraftStore"/>'s docs — the trace-workspace
/// switch is for host-tool reads, but drafts and snapshots stay pinned to the chat).
/// <para/>
/// File-name conventions are distinct from the workflow store so agent and workflow drafts
/// coexist in the same conversation workspace without colliding.
/// </summary>
public static class AgentPackageDraftStore
{
    public const string DraftFileName = "draft.cf-agent-package.json";
    private const string SnapshotPrefix = "snapshot-";
    private const string SnapshotSuffix = ".cf-agent-package.json";

    public static string ResolveDraftPath(ToolWorkspaceContext workspace) =>
        Path.Combine(workspace.RootPath, DraftFileName);

    public static async Task<JsonNode?> ReadAsync(ToolWorkspaceContext workspace, CancellationToken cancellationToken)
    {
        var path = ResolveDraftPath(workspace);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public static async Task WriteAsync(
        ToolWorkspaceContext workspace,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace.RootPath);
        var path = ResolveDraftPath(workspace);
        var tempPath = path + ".writing";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                payload,
                AssistantToolJson.SerializerOptions,
                cancellationToken);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    public static bool Delete(ToolWorkspaceContext workspace)
    {
        var path = ResolveDraftPath(workspace);
        if (!File.Exists(path))
        {
            return false;
        }
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Snapshot the validated draft to an immutable per-save file so the user-confirmation
    /// chip can apply the EXACT bytes the LLM (and the user) saw at preview time, even if
    /// the live draft is patched or overwritten before the user clicks Save.
    /// </summary>
    public static async Task<Guid> WriteSnapshotAsync(
        ToolWorkspaceContext workspace,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace.RootPath);
        var snapshotId = Guid.NewGuid();
        var path = ResolveSnapshotPath(workspace, snapshotId);
        var tempPath = path + ".writing";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                payload,
                AssistantToolJson.SerializerOptions,
                cancellationToken);
        }
        File.Move(tempPath, path, overwrite: true);
        return snapshotId;
    }

    /// <summary>
    /// Resolves the on-disk path for a per-save snapshot. The snapshot file naming uses a
    /// fixed prefix + suffix and a GUID body so concurrent saves never collide and a stray
    /// path component (e.g. <c>"../../etc/passwd"</c>) gets rejected at the
    /// <see cref="Guid.TryParse(string?, out Guid)"/> guard inside the apply endpoint.
    /// </summary>
    public static string ResolveSnapshotPath(ToolWorkspaceContext workspace, Guid snapshotId) =>
        Path.Combine(workspace.RootPath, $"{SnapshotPrefix}{snapshotId:N}{SnapshotSuffix}");

    public static bool DeleteSnapshot(ToolWorkspaceContext workspace, Guid snapshotId)
    {
        var path = ResolveSnapshotPath(workspace, snapshotId);
        if (!File.Exists(path))
        {
            return false;
        }
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// True if the workspace holds at least one pending Save snapshot — i.e., the user has
    /// a confirmation chip awaiting their click that would import these exact bytes.
    /// </summary>
    public static bool HasPendingSnapshots(ToolWorkspaceContext workspace)
    {
        if (!Directory.Exists(workspace.RootPath))
        {
            return false;
        }
        return Directory
            .EnumerateFiles(workspace.RootPath, $"{SnapshotPrefix}*{SnapshotSuffix}", SearchOption.TopDirectoryOnly)
            .Any();
    }

    /// <summary>
    /// Build a small JSON summary of the agent-package draft for tool results. Returns enough
    /// metadata (entry-point, agent count, role count, skill count) for the LLM to reason
    /// about what's in the draft without re-receiving the full payload.
    /// </summary>
    public static JsonObject Summarize(JsonNode draft, long sizeBytes)
    {
        var summary = new JsonObject
        {
            ["sizeBytes"] = sizeBytes,
        };

        if (draft is not JsonObject root)
        {
            return summary;
        }

        if (root["entryPoint"] is JsonObject entry)
        {
            summary["entryPoint"] = JsonNode.Parse(entry.ToJsonString())!;
        }

        if (root["agents"] is JsonArray agents)
        {
            summary["agentCount"] = agents.Count;
        }

        if (root["roles"] is JsonArray roles)
        {
            summary["roleCount"] = roles.Count;
        }

        if (root["skills"] is JsonArray skills)
        {
            summary["skillCount"] = skills.Count;
        }

        if (root["mcpServers"] is JsonArray mcpServers)
        {
            summary["mcpServerCount"] = mcpServers.Count;
        }

        return summary;
    }
}
