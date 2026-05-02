namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Maintains an at-most-one invariant on full workflow-package payloads in the in-turn assistant
/// transcript. The carrier tracker is per-AskAsync (per-conversation-call): each time the dispatch
/// loop registers a new package payload (an emission via <c>set_workflow_package_draft</c> /
/// <c>save_workflow_package</c>, or a fetch result from <c>get_workflow_package_draft</c> /
/// <c>get_workflow_package</c>), the prior carrier — regardless of which provider or which
/// direction — is demoted in-place to its redacted form. The new carrier stays full.
/// <para/>
/// This realises the "N=1 buffer" policy described in
/// <see cref="WorkflowPackageRedaction"/>: at any point in the for-loop the messages list
/// carries exactly one full package payload (the most recent one), and every older copy is the
/// redaction placeholder. Symmetric across input-side (tool_use.input) and result-side
/// (tool_result.content) carriers, and symmetric across the Anthropic and OpenAI provider paths.
/// </summary>
internal sealed class WorkflowPackageCarrierTracker
{
    private Action? demoteCurrent;

    /// <summary>
    /// Replace the current carrier (if any) with a freshly-registered one. Demotes the previous
    /// carrier to its redacted form first. Both arms of the dispatch path (Anthropic / OpenAI)
    /// call into <see cref="Replace(Action)"/> with a closure that knows how to rebuild the
    /// specific block it owns; the tracker itself is provider-agnostic.
    /// </summary>
    public void Replace(Action demote)
    {
        ArgumentNullException.ThrowIfNull(demote);
        demoteCurrent?.Invoke();
        demoteCurrent = demote;
    }
}
