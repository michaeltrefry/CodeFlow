namespace CodeFlow.Runtime.Authority.Admission;

/// <summary>
/// Witness that a <c>vcs.open_pr</c> request has been authorized for the active run: the
/// target repository is declared in the trace's repo context, the resolved authority
/// envelope (when present) grants <see cref="RepoAccess.Write"/> on the matched repo
/// scope, and the envelope's <c>Delivery</c> axis (when present) targets the same repo
/// + base branch. Produced only by <see cref="DeliveryRequestValidator"/>; consumers
/// — i.e. <see cref="WorkspaceHostToolService"/>'s VCS dispatch — accept this type rather
/// than raw arguments so the no-validation path is gone at compile time.
///
/// Re-mint discipline: the source request shape (owner, name, head, base, title, body)
/// + the resolved envelope ref are sufficient for replay through the validator on a
/// fresh process. Body is pinned at admission time so a subsequent admission against the
/// same trace context produces an equivalent admitted value.
/// </summary>
public sealed class AuthorizedDeliveryRequest
{
    /// <summary>Validator-only constructor.</summary>
    internal AuthorizedDeliveryRequest(
        string owner,
        string name,
        string head,
        string baseBranch,
        string title,
        string body,
        string? repoIdentityKey,
        DateTimeOffset admittedAt)
    {
        Owner = owner;
        Name = name;
        Head = head;
        BaseBranch = baseBranch;
        Title = title;
        Body = body;
        RepoIdentityKey = repoIdentityKey;
        AdmittedAt = admittedAt;
    }

    public string Owner { get; }
    public string Name { get; }
    public string Head { get; }
    public string BaseBranch { get; }
    public string Title { get; }
    public string Body { get; }

    /// <summary>Identity key of the matched repo context entry, when one was discovered. Null when
    /// the trace's repo context only has an owner/name match without a canonical identity key.</summary>
    public string? RepoIdentityKey { get; }

    public DateTimeOffset AdmittedAt { get; }
}

/// <summary>
/// Raw request the delivery validator turns into an <see cref="AuthorizedDeliveryRequest"/>.
/// Captures the inputs that need to round-trip for re-mint: the PR shape, the active
/// tool execution context (which carries the trace's repo context + the resolved envelope).
/// </summary>
public sealed record DeliveryAdmissionRequest(
    string Owner,
    string Name,
    string Head,
    string BaseBranch,
    string Title,
    string? Body,
    ToolExecutionContext? Context);
