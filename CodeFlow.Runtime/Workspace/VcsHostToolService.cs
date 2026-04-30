using System.Text.Json.Nodes;
using CodeFlow.Runtime.Authority;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Host-tool dispatch for the <c>vcs.*</c> verbs. Resolves an <see cref="IVcsProvider"/> via
/// <see cref="IVcsProviderFactory"/> on every call so the configured Git host (mode + token) is
/// read fresh and not cached. Translates <see cref="VcsException"/>s into structured tool-error
/// payloads so agents see the failure taxonomy without needing to parse exception text.
/// </summary>
public sealed class VcsHostToolService
{
    private readonly IVcsProviderFactory factory;

    public VcsHostToolService(IVcsProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.factory = factory;
    }

    public async Task<ToolResult> OpenPullRequestAsync(
        ToolCall toolCall,
        ToolExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var owner = GetRequiredString(toolCall.Arguments, "owner");
        var name = GetRequiredString(toolCall.Arguments, "name");
        var head = GetRequiredString(toolCall.Arguments, "head");
        var baseRef = GetRequiredString(toolCall.Arguments, "base");
        var title = GetRequiredString(toolCall.Arguments, "title");
        var body = GetOptionalString(toolCall.Arguments, "body") ?? string.Empty;
        if (!IsAllowedRepository(context, owner, name))
        {
            return BuildRepoNotAllowed(toolCall.Id, owner, name);
        }
        if (CheckEnvelopeRepoScope(context, owner, name, RepoAccess.Write) is { } envelopeRefusal)
        {
            return envelopeRefusal(toolCall.Id);
        }

        try
        {
            var provider = await factory.CreateAsync(cancellationToken);
            var pr = await provider.OpenPullRequestAsync(
                owner, name, head, baseRef, title, body, cancellationToken);

            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["url"] = pr.Url,
                    ["number"] = pr.Number,
                }.ToJsonString());
        }
        catch (Exception ex) when (ex is VcsException or GitHostNotConfiguredException)
        {
            return BuildError(toolCall.Id, ex);
        }
    }

    public async Task<ToolResult> GetRepoMetadataAsync(
        ToolCall toolCall,
        ToolExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var owner = GetRequiredString(toolCall.Arguments, "owner");
        var name = GetRequiredString(toolCall.Arguments, "name");
        if (!IsAllowedRepository(context, owner, name))
        {
            return BuildRepoNotAllowed(toolCall.Id, owner, name);
        }
        if (CheckEnvelopeRepoScope(context, owner, name, RepoAccess.Read) is { } envelopeRefusal)
        {
            return envelopeRefusal(toolCall.Id);
        }

        try
        {
            var provider = await factory.CreateAsync(cancellationToken);
            var meta = await provider.GetRepoMetadataAsync(owner, name, cancellationToken);

            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["defaultBranch"] = meta.DefaultBranch,
                    ["cloneUrl"] = meta.CloneUrl,
                    ["visibility"] = meta.Visibility.ToString(),
                }.ToJsonString());
        }
        catch (Exception ex) when (ex is VcsException or GitHostNotConfiguredException)
        {
            return BuildError(toolCall.Id, ex);
        }
    }

    private static bool IsAllowedRepository(ToolExecutionContext? context, string owner, string name)
    {
        if (context?.Repositories is { Count: > 0 } repositories
            && repositories.Any(repo =>
                string.Equals(repo.Owner, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(repo.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(context?.Workspace?.RepoUrl))
        {
            try
            {
                var repo = RepoReference.Parse(context.Workspace.RepoUrl);
                return string.Equals(repo.Owner, owner, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(repo.Name, name, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return false;
    }

    private static ToolResult BuildRepoNotAllowed(string callId, string owner, string name)
    {
        var payload = new JsonObject
        {
            ["error"] = "repo_not_allowed",
            ["message"] = $"Repository '{owner}/{name}' is not declared for this trace.",
        };
        return new ToolResult(callId, payload.ToJsonString(), IsError: true);
    }

    /// <summary>
    /// sc-269 PR3: when the resolved envelope expresses <c>RepoScopes</c>, require the
    /// requested <c>(owner, name)</c> to map to a scope grant of at least the requested
    /// access level. Returns <c>null</c> when the envelope is silent (no opinion expressed)
    /// or when a matching scope grant is found; otherwise returns a refusal-builder closure
    /// so the caller can inject the tool-call id into the structured payload.
    /// </summary>
    private static Func<string, ToolResult>? CheckEnvelopeRepoScope(
        ToolExecutionContext? context,
        string owner,
        string name,
        RepoAccess required)
    {
        var scopes = context?.Envelope?.RepoScopes;
        if (scopes is null)
        {
            return null;
        }

        var identityKey = ResolveIdentityKey(context, owner, name);

        foreach (var scope in scopes)
        {
            // Scope grants compare by identity key when one is known. If the caller doesn't have
            // an identity in context (no repos[] declared, no workspace.RepoUrl), fall back to a
            // case-insensitive owner/name suffix match against the scope's path so an envelope
            // tier that names "owner/name" still gates the operation. Access is satisfied when
            // the scope grant's access level is >= the required level (Write covers Read).
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

        return callId => new ToolResult(
            callId,
            new JsonObject
            {
                ["ok"] = false,
                ["refusal"] = new JsonObject
                {
                    ["code"] = "envelope-repo-scope",
                    ["reason"] =
                        $"Repository '{owner}/{name}' is not granted '{required}' access by the run's RepoScopes envelope axis.",
                    ["axis"] = BlockedBy.Axes.RepoScopes,
                    ["repo"] = $"{owner}/{name}",
                    ["requiredAccess"] = required.ToString()
                }
            }.ToJsonString(),
            IsError: true);
    }

    private static string? ResolveIdentityKey(ToolExecutionContext? context, string owner, string name)
    {
        if (context?.Repositories is { Count: > 0 } repos)
        {
            foreach (var repo in repos)
            {
                if (string.Equals(repo.Owner, owner, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(repo.Name, name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(repo.RepoIdentityKey))
                {
                    return repo.RepoIdentityKey;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(context?.Workspace?.RepoIdentityKey))
        {
            return context.Workspace.RepoIdentityKey;
        }

        if (!string.IsNullOrWhiteSpace(context?.Workspace?.RepoUrl))
        {
            try
            {
                return RepoReference.Parse(context.Workspace.RepoUrl).IdentityKey;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static ToolResult BuildError(string callId, Exception ex)
    {
        var kind = ex switch
        {
            VcsRepoNotFoundException => "repo_not_found",
            VcsUnauthorizedException => "unauthorized",
            VcsRateLimitedException => "rate_limited",
            VcsConflictException => "conflict",
            GitHostNotConfiguredException => "not_configured",
            _ => "vcs_error",
        };

        var payload = new JsonObject
        {
            ["error"] = kind,
            ["message"] = ex.Message,
        };
        return new ToolResult(callId, payload.ToJsonString(), IsError: true);
    }

    private static string GetRequiredString(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is JsonValue value && value.TryGetValue<string>(out var str)
            && !string.IsNullOrWhiteSpace(str))
        {
            return str;
        }

        throw new InvalidOperationException(
            $"vcs tool requires a non-empty string '{propertyName}' argument.");
    }

    private static string? GetOptionalString(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is JsonValue value && value.TryGetValue<string>(out var str))
        {
            return str;
        }

        return null;
    }
}
