using System.Text.Json;
using CodeFlow.Runtime.Authority;

namespace CodeFlow.Runtime;

/// <summary>
/// Per-invocation execution context handed to host and MCP tools through
/// <see cref="ToolRegistry.InvokeAsync"/>. Carries the active workspace, the declared
/// repository scopes, and (sc-269 PR3) the resolved <see cref="WorkflowExecutionEnvelope"/>
/// so tools can self-enforce envelope axes (ExecuteGrants, RepoScopes, Network) without
/// the orchestration consumer needing to pre-filter every tool surface.
/// </summary>
/// <param name="Envelope">
/// Resolved authority envelope for this invocation, when an <c>IAuthorityResolver</c> is
/// wired (production path via <c>AgentInvocationConsumer</c>). Tools that need to enforce
/// an axis read this — <c>null</c> means "no envelope was resolved", which preserves the
/// pre-PR3 behaviour for legacy callers and standalone tests that don't go through the
/// consumer.
/// </param>
public sealed record ToolExecutionContext(
    ToolWorkspaceContext? Workspace = null,
    IReadOnlyList<ToolRepositoryContext>? Repositories = null,
    WorkflowExecutionEnvelope? Envelope = null)
{
    /// <summary>
    /// Optional sink for host tools that need to stage mid-turn workflow-bag writes —
    /// e.g. <c>setup_workspace</c> updating <c>workflow.repositories</c> after cloning.
    /// When non-null, the host tool calls this to append a key/value into the same pending
    /// pending-writes dictionary the <c>setWorkflow</c> built-in tool uses; the writes
    /// commit on successful submit and are discarded on failure, exactly like
    /// <c>setWorkflow</c>. <c>InvocationLoop</c> wires this when invoking external tools;
    /// other consumers (assistant tool factory, tests) leave it null and host tools that
    /// rely on it become no-ops in those contexts.
    /// </summary>
    public Action<string, JsonElement>? StageWorkflowBagWrite { get; init; }
}

/// <summary>
/// Workspace identity handed to host tools.
///
/// <para>
/// <see cref="CorrelationId"/> is the CURRENT saga's correlation id. Subflow sagas have a
/// fresh correlation id distinct from the root trace's; host tools that scope per-saga (e.g.
/// Docker container cleanup labels) should use this.
/// </para>
///
/// <para>
/// <see cref="RootTraceId"/> is the on-disk trace identifier — the directory name under the
/// configured workspace root that holds the trace's files. Host tools that need to RESOLVE
/// the workspace on disk (e.g. sandbox-controller workspace path validation) must use this,
/// because <c>workspace/{traceId}</c> only exists for the ROOT trace; subflow sagas inherit
/// the same on-disk path. Equals <see cref="CorrelationId"/> for the root saga; differs inside
/// subflows.
/// </para>
/// </summary>
public sealed record ToolWorkspaceContext(
    Guid CorrelationId,
    string RootPath,
    Guid? RootTraceId = null,
    string? RepoUrl = null,
    string? RepoIdentityKey = null,
    string? RepoSlug = null);

public sealed record ToolRepositoryContext(
    string Owner,
    string Name,
    string? Url = null,
    string? RepoIdentityKey = null,
    string? RepoSlug = null);
