namespace CodeFlow.Contracts;

/// <summary>
/// Per-trace declaration that a workflow is operating on the given repository. Threaded through
/// <see cref="AgentInvokeRequested.Repositories"/> and <see cref="SubflowInvokeRequested.Repositories"/>
/// so subflows inherit the parent saga's allowlist without each child having to redeclare it.
/// The orchestration layer stores the parsed array on <c>workflow_sagas.repositories_json</c>;
/// the runtime <c>vcs_*</c> tools translate <see cref="Url"/> via <c>RepoReference.Parse</c> at
/// the gate.
/// </summary>
/// <param name="Url">Canonical clone URL (e.g. <c>https://github.com/owner/name.git</c>). The
/// owner/name pair derived from this URL is what the vcs allowlist matches against.</param>
/// <param name="Branch">Optional default branch the workflow expects to operate on. Hint only —
/// not enforced by the allowlist; consumed by setup agents that pre-clone the workspace.</param>
public sealed record RepositoryDeclaration(string Url, string? Branch = null);
