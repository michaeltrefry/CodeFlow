using System.Text.Json.Nodes;
using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Runtime.Authority.Admission;

/// <summary>
/// Mints <see cref="AdmittedWorkspacePatch"/> values from raw <c>apply_patch</c> requests.
/// Catches the structural failure modes — malformed patch syntax, paths that escape the
/// workspace root, paths that resolve through symlinks under the active policy — up-front
/// so the executor can run the actual filesystem operations against a verified shape.
///
/// Filesystem-state-dependent checks (preimage hash matches, destination-exists guards)
/// stay in the executor where they belong: the filesystem can change between admission
/// and write, so admission cannot make those guarantees on the executor's behalf.
/// </summary>
public sealed class WorkspacePatchValidator : IAdmissionValidator<WorkspacePatchAdmissionRequest, AdmittedWorkspacePatch>
{
    private readonly WorkspaceOptions options;
    private readonly Func<DateTimeOffset> nowProvider;

    public WorkspacePatchValidator(
        WorkspaceOptions? options = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.options = options ?? new WorkspaceOptions();
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public Admission<AdmittedWorkspacePatch> Validate(WorkspacePatchAdmissionRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.WorkspaceRootPath))
        {
            return Admission<AdmittedWorkspacePatch>.Reject(new Rejection(
                Code: RejectionCodes.InvariantViolated,
                Reason: "Workspace root path is required for patch admission.",
                Axis: "workspace-mutation"));
        }

        WorkspacePatchDocument document;
        try
        {
            document = WorkspacePatchDocument.Parse(input.PatchText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Admission<AdmittedWorkspacePatch>.Reject(new Rejection(
                Code: "patch-malformed",
                Reason: ex.Message,
                Axis: "workspace-mutation"));
        }

        foreach (var command in document.Commands)
        {
            var primaryPath = command switch
            {
                AddFilePatchCommand add => add.Path,
                DeleteFilePatchCommand delete => delete.Path,
                UpdateFilePatchCommand update => update.Path,
                _ => null
            };

            if (primaryPath is null)
            {
                return Admission<AdmittedWorkspacePatch>.Reject(new Rejection(
                    Code: RejectionCodes.InvariantViolated,
                    Reason: $"Unsupported patch command '{command.GetType().Name}'.",
                    Axis: "workspace-mutation"));
            }

            if (CheckPath(input.WorkspaceRootPath, primaryPath) is { } primaryRejection)
            {
                return Admission<AdmittedWorkspacePatch>.Reject(primaryRejection);
            }

            // Update with MoveTo: the destination path is also a mutation target.
            if (command is UpdateFilePatchCommand { MoveToPath: { Length: > 0 } moveTo } updateCommand
                && !string.Equals(moveTo, updateCommand.Path, StringComparison.Ordinal)
                && CheckPath(input.WorkspaceRootPath, moveTo) is { } destinationRejection)
            {
                return Admission<AdmittedWorkspacePatch>.Reject(destinationRejection);
            }
        }

        return Admission<AdmittedWorkspacePatch>.Accept(new AdmittedWorkspacePatch(
            workspaceCorrelationId: input.WorkspaceCorrelationId,
            workspaceRootPath: input.WorkspaceRootPath,
            sourcePatchText: input.PatchText,
            admittedAt: nowProvider(),
            document: document));
    }

    private Rejection? CheckPath(string workspaceRoot, string relativePath)
    {
        try
        {
            PathConfinement.Resolve(workspaceRoot, relativePath);
        }
        catch (PathConfinementException ex)
        {
            return new Rejection(
                Code: "path-confinement",
                Reason: ex.Message,
                Axis: "workspace-mutation",
                Path: relativePath);
        }

        if (options.SymlinkPolicy == CodeFlow.Runtime.Workspace.WorkspaceSymlinkPolicy.RefuseForMutation
            && PathConfinement.ContainsSymlink(workspaceRoot, relativePath))
        {
            return new Rejection(
                Code: "symlink-refused",
                Reason: $"Path '{relativePath}' resolves through a symlink; symlink mutation is refused by workspace policy.",
                Axis: "workspace-mutation",
                Path: relativePath);
        }

        return null;
    }
}
