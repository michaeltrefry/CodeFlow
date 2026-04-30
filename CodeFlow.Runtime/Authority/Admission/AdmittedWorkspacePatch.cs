using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Runtime.Authority.Admission;

/// <summary>
/// Witness that a raw workspace patch payload passed admission: the patch parsed cleanly,
/// every Add/Update/Delete path is confined to the workspace root, and (when the active
/// workspace's symlink policy says so) no path resolves through a symlink. Produced only
/// by <see cref="WorkspacePatchValidator"/>; consumers — i.e.
/// <see cref="WorkspaceHostToolService"/> — accept this type rather than raw patch text
/// so the no-validation path is gone at compile time.
///
/// Run-time invariants the executor still re-checks after admission:
/// <list type="bullet">
///   <item><description>Preimage SHA-256s match the file on disk at write time. Filesystem state
///   can change between admission and execution; the executor's preimage check is the
///   authoritative defence and stays where it was in sc-270.</description></item>
///   <item><description>Destination-exists guards (<c>Add</c> over an existing path,
///   <c>Update with MoveTo</c> into an existing path) — same race-window argument.</description></item>
/// </list>
///
/// Re-mint discipline: persistence stores <see cref="SourcePatchText"/> + the active
/// envelope snapshot. On a fresh process, replaying the raw patch text through the
/// validator produces an equivalent admitted value (validate, don't trust).
/// </summary>
public sealed class AdmittedWorkspacePatch
{
    /// <summary>Validator-only constructor. The internal scope keeps consumers honest.</summary>
    internal AdmittedWorkspacePatch(
        Guid workspaceCorrelationId,
        string workspaceRootPath,
        string sourcePatchText,
        DateTimeOffset admittedAt,
        WorkspacePatchDocument document)
    {
        WorkspaceCorrelationId = workspaceCorrelationId;
        WorkspaceRootPath = workspaceRootPath;
        SourcePatchText = sourcePatchText;
        AdmittedAt = admittedAt;
        Document = document;
    }

    /// <summary>Active workspace correlation id at admission time.</summary>
    public Guid WorkspaceCorrelationId { get; }

    /// <summary>Absolute filesystem root the patch is admitted against.</summary>
    public string WorkspaceRootPath { get; }

    /// <summary>Raw patch text the validator was handed. Kept for re-mint and audit.</summary>
    public string SourcePatchText { get; }

    /// <summary>UTC instant the validator minted this admission.</summary>
    public DateTimeOffset AdmittedAt { get; }

    /// <summary>Number of patch commands the document contained. Kept on the public surface so
    /// observers can build evidence summaries without reaching into the internal document.</summary>
    public int CommandCount => Document.Commands.Count;

    /// <summary>Internal-only: the parsed patch document the executor consumes.</summary>
    internal WorkspacePatchDocument Document { get; }
}

/// <summary>
/// Raw request the workspace patch validator turns into an
/// <see cref="AdmittedWorkspacePatch"/>. Captures the inputs that need to round-trip for
/// re-mint: the workspace identity, the root path, and the patch text itself.
/// </summary>
public sealed record WorkspacePatchAdmissionRequest(
    Guid WorkspaceCorrelationId,
    string WorkspaceRootPath,
    string PatchText);
