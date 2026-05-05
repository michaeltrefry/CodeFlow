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

public sealed record ToolWorkspaceContext(
    Guid CorrelationId,
    string RootPath,
    string? RepoUrl = null,
    string? RepoIdentityKey = null,
    string? RepoSlug = null);

public sealed record ToolRepositoryContext(
    string Owner,
    string Name,
    string? Url = null,
    string? RepoIdentityKey = null,
    string? RepoSlug = null);
