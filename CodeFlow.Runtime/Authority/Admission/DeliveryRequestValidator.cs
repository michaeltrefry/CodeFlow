using System.Text.Json.Nodes;
using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Runtime.Authority.Admission;

/// <summary>
/// Mints <see cref="AuthorizedDeliveryRequest"/> values from raw <c>vcs.open_pr</c>
/// requests. Consolidates the trace-context check (was <c>VcsHostToolService.IsAllowedRepository</c>)
/// + the envelope <c>RepoScopes</c> check (was <c>VcsHostToolService.CheckEnvelopeRepoScope</c>)
/// + the new envelope <c>Delivery</c> target check into a single boundary.
///
/// Refusal taxonomy:
/// <list type="bullet">
///   <item><description><c>delivery-repo-not-declared</c> — owner/name not in trace's repo context.</description></item>
///   <item><description><c>envelope-repo-scope</c> — envelope grants no Write access on the matched scope.</description></item>
///   <item><description><c>envelope-delivery</c> — envelope's Delivery target axis names a different repo or branch.</description></item>
///   <item><description><c>delivery-arg-missing</c> — required PR argument was empty/whitespace.</description></item>
/// </list>
/// </summary>
public sealed class DeliveryRequestValidator : IAdmissionValidator<DeliveryAdmissionRequest, AuthorizedDeliveryRequest>
{
    private readonly Func<DateTimeOffset> nowProvider;

    public DeliveryRequestValidator(Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public Admission<AuthorizedDeliveryRequest> Validate(DeliveryAdmissionRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (RequireField(input.Owner, "owner") is { } ownerRejection) return Admission<AuthorizedDeliveryRequest>.Reject(ownerRejection);
        if (RequireField(input.Name, "name") is { } nameRejection) return Admission<AuthorizedDeliveryRequest>.Reject(nameRejection);
        if (RequireField(input.Head, "head") is { } headRejection) return Admission<AuthorizedDeliveryRequest>.Reject(headRejection);
        if (RequireField(input.BaseBranch, "base") is { } baseRejection) return Admission<AuthorizedDeliveryRequest>.Reject(baseRejection);
        if (RequireField(input.Title, "title") is { } titleRejection) return Admission<AuthorizedDeliveryRequest>.Reject(titleRejection);

        // Trace-context check: the requested repo must be in the trace's declared repo set
        // (from workflow.repos[]) or match the active workspace's RepoUrl. Same shape as the
        // pre-PR2 IsAllowedRepository.
        var (allowed, identityKey) = ResolveTraceRepo(input);
        if (!allowed)
        {
            return Admission<AuthorizedDeliveryRequest>.Reject(new Rejection(
                Code: "delivery-repo-not-declared",
                Reason: $"Repository '{input.Owner}/{input.Name}' is not declared for this trace.",
                Axis: "delivery",
                Path: $"{input.Owner}/{input.Name}"));
        }

        // Envelope RepoScopes axis: envelope must grant Write access on the matched scope when
        // it expresses an opinion. Same logic the pre-PR2 CheckEnvelopeRepoScope ran inline.
        if (CheckEnvelopeRepoScope(input.Context, input.Owner, input.Name, identityKey, RepoAccess.Write) is { } scopeRejection)
        {
            return Admission<AuthorizedDeliveryRequest>.Reject(scopeRejection);
        }

        // Envelope Delivery target axis: when the resolver intersected to a single delivery
        // target, the request's owner/name + base branch must match. Other axes already gate
        // capability; Delivery gates *which* repo+branch a successful run is allowed to mutate.
        if (CheckEnvelopeDeliveryTarget(input.Context, input.Owner, input.Name, input.BaseBranch) is { } deliveryRejection)
        {
            return Admission<AuthorizedDeliveryRequest>.Reject(deliveryRejection);
        }

        return Admission<AuthorizedDeliveryRequest>.Accept(new AuthorizedDeliveryRequest(
            owner: input.Owner,
            name: input.Name,
            head: input.Head,
            baseBranch: input.BaseBranch,
            title: input.Title,
            body: input.Body ?? string.Empty,
            repoIdentityKey: identityKey,
            admittedAt: nowProvider()));
    }

    private static Rejection? RequireField(string? value, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return new Rejection(
            Code: "delivery-arg-missing",
            Reason: $"vcs.open_pr requires a non-empty string '{fieldName}' argument.",
            Axis: "delivery",
            Path: fieldName);
    }

    private static (bool Allowed, string? IdentityKey) ResolveTraceRepo(DeliveryAdmissionRequest input)
    {
        var context = input.Context;

        if (context?.Repositories is { Count: > 0 } repositories)
        {
            foreach (var repo in repositories)
            {
                if (string.Equals(repo.Owner, input.Owner, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(repo.Name, input.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, string.IsNullOrWhiteSpace(repo.RepoIdentityKey) ? null : repo.RepoIdentityKey);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(context?.Workspace?.RepoUrl))
        {
            try
            {
                var repo = RepoReference.Parse(context.Workspace.RepoUrl);
                if (string.Equals(repo.Owner, input.Owner, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(repo.Name, input.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, repo.IdentityKey);
                }
            }
            catch (ArgumentException)
            {
                // Malformed RepoUrl — fall through to "not allowed".
            }
        }

        return (false, null);
    }

    private static Rejection? CheckEnvelopeRepoScope(
        ToolExecutionContext? context,
        string owner,
        string name,
        string? identityKey,
        RepoAccess required)
    {
        var scopes = context?.Envelope?.RepoScopes;
        if (scopes is null)
        {
            return null;
        }

        foreach (var scope in scopes)
        {
            var identityMatch = identityKey is not null
                && string.Equals(scope.RepoIdentityKey, identityKey, StringComparison.OrdinalIgnoreCase);
            var pathMatch = scope.Path is { Length: > 0 }
                && scope.Path.EndsWith($"{owner}/{name}", StringComparison.OrdinalIgnoreCase);

            if (!identityMatch && !pathMatch)
            {
                continue;
            }

            if (scope.Access >= required)
            {
                return null;
            }
        }

        return new Rejection(
            Code: "envelope-repo-scope",
            Reason: $"Repository '{owner}/{name}' is not granted '{required}' access by the run's RepoScopes envelope axis.",
            Axis: BlockedBy.Axes.RepoScopes,
            Path: $"{owner}/{name}",
            Detail: new JsonObject
            {
                ["repo"] = $"{owner}/{name}",
                ["requiredAccess"] = required.ToString(),
            });
    }

    private static Rejection? CheckEnvelopeDeliveryTarget(
        ToolExecutionContext? context,
        string owner,
        string name,
        string baseBranch)
    {
        var delivery = context?.Envelope?.Delivery;
        if (delivery is null)
        {
            return null;
        }

        var ownerMatch = string.Equals(delivery.Owner, owner, StringComparison.OrdinalIgnoreCase);
        var nameMatch = string.Equals(delivery.Repo, name, StringComparison.OrdinalIgnoreCase);
        var branchMatch = string.Equals(delivery.BaseBranch, baseBranch, StringComparison.Ordinal);

        if (ownerMatch && nameMatch && branchMatch)
        {
            return null;
        }

        return new Rejection(
            Code: "envelope-delivery",
            Reason: $"Delivery target '{owner}/{name}@{baseBranch}' does not match the envelope's Delivery axis ('{delivery.Owner}/{delivery.Repo}@{delivery.BaseBranch}').",
            Axis: BlockedBy.Axes.Delivery,
            Path: $"{owner}/{name}@{baseBranch}");
    }
}
