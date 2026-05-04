using System.Text.Json.Nodes;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Authority.Admission;

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
    private readonly DeliveryRequestValidator deliveryValidator;
    private readonly IGitCli? gitCli;
    private readonly IRepoUrlHostGuard? hostGuard;
    private readonly WorkspaceOptions? workspaceOptions;

    public VcsHostToolService(
        IVcsProviderFactory factory,
        DeliveryRequestValidator? deliveryValidator = null,
        Func<DateTimeOffset>? nowProvider = null,
        IGitCli? gitCli = null,
        IRepoUrlHostGuard? hostGuard = null,
        WorkspaceOptions? workspaceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.factory = factory;
        this.deliveryValidator = deliveryValidator ?? new DeliveryRequestValidator(nowProvider);
        this.gitCli = gitCli;
        this.hostGuard = hostGuard;
        this.workspaceOptions = workspaceOptions;
    }

    public async Task<ToolResult> OpenPullRequestAsync(
        ToolCall toolCall,
        ToolExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        // sc-272 PR2: vcs.open_pr is the canonical delivery boundary. Admission consolidates
        // the trace-context check, the envelope RepoScopes check, and the new envelope
        // Delivery target check; the executor here only sees an AuthorizedDeliveryRequest.
        var admission = deliveryValidator.Validate(new DeliveryAdmissionRequest(
            Owner: GetRequiredString(toolCall.Arguments, "owner"),
            Name: GetRequiredString(toolCall.Arguments, "name"),
            Head: GetRequiredString(toolCall.Arguments, "head"),
            BaseBranch: GetRequiredString(toolCall.Arguments, "base"),
            Title: GetRequiredString(toolCall.Arguments, "title"),
            Body: GetOptionalString(toolCall.Arguments, "body"),
            Context: context));

        if (admission is Rejected<AuthorizedDeliveryRequest> rejected)
        {
            return RejectionResult(toolCall.Id, rejected.Reason);
        }

        var admitted = ((Accepted<AuthorizedDeliveryRequest>)admission).Value;

        try
        {
            var provider = await factory.CreateAsync(cancellationToken);
            var pr = await provider.OpenPullRequestAsync(
                admitted.Owner,
                admitted.Name,
                admitted.Head,
                admitted.BaseBranch,
                admitted.Title,
                admitted.Body,
                cancellationToken);

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

    public async Task<ToolResult> CloneAsync(
        ToolCall toolCall,
        ToolExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (gitCli is null)
        {
            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["error"] = "vcs_error",
                    ["message"] = "vcs.clone is not configured: VcsHostToolService was constructed without an IGitCli.",
                }.ToJsonString(),
                IsError: true);
        }

        var workspace = context?.Workspace;
        if (workspace is null)
        {
            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["error"] = "workspace_required",
                    ["message"] = "vcs.clone requires an active workspace.",
                }.ToJsonString(),
                IsError: true);
        }

        var url = GetOptionalString(toolCall.Arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["error"] = "url_required",
                    ["message"] = "vcs.clone requires a non-empty 'url' argument.",
                }.ToJsonString(),
                IsError: true);
        }

        RepoReference repo;
        try
        {
            repo = RepoReference.Parse(url);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["error"] = "url_invalid",
                    ["message"] = ex.Message,
                }.ToJsonString(),
                IsError: true);
        }

        if (hostGuard is not null)
        {
            try
            {
                await hostGuard.AssertAllowedAsync(repo, cancellationToken);
            }
            catch (RepoUrlHostMismatchException ex)
            {
                return new ToolResult(
                    toolCall.Id,
                    new JsonObject
                    {
                        ["error"] = "host_mismatch",
                        ["message"] = ex.Message,
                    }.ToJsonString(),
                    IsError: true);
            }
        }

        var requestedPath = GetOptionalString(toolCall.Arguments, "path");
        var relativePath = string.IsNullOrWhiteSpace(requestedPath) ? repo.Name : requestedPath!.Trim();
        string destination;
        try
        {
            destination = PathConfinement.Resolve(workspace.RootPath, relativePath);
        }
        catch (PathConfinementException ex)
        {
            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["error"] = "path_confined",
                    ["message"] = ex.Message,
                    ["path"] = relativePath,
                }.ToJsonString(),
                IsError: true);
        }

        // Refuse if destination already exists. The check is intentionally strict: even an empty
        // directory at the destination is a refusal (callers may have just created it for
        // another purpose). If you actually want to update an existing checkout, use
        // run_command("git", ["fetch","..."]) instead — vcs.clone is for fresh materialization.
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["error"] = "destination_exists",
                    ["message"] = $"Cannot clone to '{relativePath}': destination already exists. Use run_command for git fetch/pull on an existing checkout.",
                    ["path"] = relativePath,
                }.ToJsonString(),
                IsError: true);
        }

        var branch = GetOptionalString(toolCall.Arguments, "branch");
        var depth = GetOptionalPositiveInt(toolCall.Arguments, "depth");

        try
        {
            // sc-662: clone with the clean URL. Auth flows through the per-trace credential
            // helper set up by sc-660 / sc-661 — git invokes `credential.helper = store --file=...`
            // and gets the right cred for the URL's host. No token in `.git/config`, no token
            // in process argv, no embed-then-scrub dance.
            var credentialEnv = GitCredentialEnv.Build(workspaceOptions?.GitCredentialRoot, workspace.CorrelationId);
            var result = await gitCli.CloneAsync(
                url,
                destination,
                branch,
                depth,
                credentialEnv,
                cancellationToken);

            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["path"] = MakeWorkspaceRelativePath(workspace.RootPath, destination),
                    ["branch"] = result.Branch,
                    ["head"] = result.HeadCommit,
                    ["defaultBranch"] = result.DefaultBranch,
                }.ToJsonString());
        }
        catch (GitCommandException ex)
        {
            // The clone command itself failed (e.g. bad credentials, branch doesn't exist, host
            // unreachable). Clean up any partial directory git left behind so subsequent retries
            // don't trip the destination-exists guard.
            TryDeleteDirectory(destination);
            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["error"] = "vcs_error",
                    ["message"] = ex.Message,
                }.ToJsonString(),
                IsError: true);
        }
    }

    private static string MakeWorkspaceRelativePath(string workspaceRoot, string absolutePath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), absolutePath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; orphan sweep will retry.
        }
    }

    private static int? GetOptionalPositiveInt(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is JsonValue value
            && value.TryGetValue<int>(out var i)
            && i > 0)
        {
            return i;
        }
        return null;
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
    /// sc-272 PR2: builds a tool-result refusal payload from a <see cref="Rejection"/>
    /// minted by an admission validator. Shape matches the workspace-mutation refusal
    /// payload shape so <see cref="RefusalPayloadParser"/> picks it up as a Stage = Tool
    /// <see cref="RefusalEvent"/> on the existing <see cref="ToolRegistry"/> path.
    /// </summary>
    private static ToolResult RejectionResult(string callId, Rejection rejection)
    {
        var refusalJson = new JsonObject
        {
            ["code"] = rejection.Code,
            ["reason"] = rejection.Reason,
            ["axis"] = rejection.Axis,
        };
        if (rejection.Path is not null)
        {
            refusalJson["path"] = rejection.Path;
        }
        if (rejection.Detail is not null)
        {
            refusalJson["detail"] = rejection.Detail.DeepClone();
        }

        return new ToolResult(
            callId,
            new JsonObject
            {
                ["ok"] = false,
                ["refusal"] = refusalJson,
            }.ToJsonString(),
            IsError: true);
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
