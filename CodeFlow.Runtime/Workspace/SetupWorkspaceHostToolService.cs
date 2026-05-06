using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Host-tool dispatch for the <c>setup_workspace</c> verb (sc-680). Atomically — and
/// idempotently — bootstraps a code-aware workspace from a list of repository URLs:
/// resolves credentials, writes the per-trace credential file (epic 658), clones each
/// repo, discovers the authoritative base branch via <c>git ls-remote --symref origin HEAD</c>,
/// creates the per-repo feature branch, and pushes the empty branch to validate auth at
/// setup time. Returns rich per-repo state (verified base branch, feature branch, base SHA,
/// local path) for downstream agents to consume directly — no parsing, no guessing,
/// no per-repo retry logic in agent prompts.
///
/// Idempotency: a repo whose clone already exists at the expected path with a matching
/// remote is reported back with <c>alreadyPresent: true</c> and verified (not re-cloned).
/// This is the path the architect / coding agent uses when a missing dependency is
/// discovered mid-flow — they call <c>setup_workspace</c> again with the additional URL,
/// existing repos round-trip unchanged, and the new one goes through the full setup pipeline.
///
/// Mid-turn workflow-bag write: when the tool succeeds, it stages a
/// <c>setWorkflow("repositories", […])</c> via
/// <see cref="ToolExecutionContext.StageWorkflowBagWrite"/> so the per-trace VCS allowlist
/// (<c>saga.RepositoriesJson</c>) updates on submit and the new repos become valid targets
/// for <c>vcs.open_pr</c> downstream — without the agent having to remember to mirror the
/// result via a separate setWorkflow call.
/// </summary>
public sealed class SetupWorkspaceHostToolService
{
    public const string ToolName = "setup_workspace";

    private readonly IPerTraceCredentialResolver credentialResolver;
    private readonly IGitCli gitCli;
    private readonly WorkspaceOptions workspaceOptions;
    private readonly IRepoUrlHostGuard? hostGuard;

    public SetupWorkspaceHostToolService(
        IPerTraceCredentialResolver credentialResolver,
        IGitCli gitCli,
        WorkspaceOptions workspaceOptions,
        IRepoUrlHostGuard? hostGuard = null)
    {
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(gitCli);
        ArgumentNullException.ThrowIfNull(workspaceOptions);
        this.credentialResolver = credentialResolver;
        this.gitCli = gitCli;
        this.workspaceOptions = workspaceOptions;
        this.hostGuard = hostGuard;
    }

    public async Task<ToolResult> SetupWorkspaceAsync(
        ToolCall toolCall,
        ToolExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var workspace = context?.Workspace;
        if (workspace is null)
        {
            return Error(toolCall.Id, "workspace_required", "setup_workspace requires an active workspace.");
        }

        var (request, parseError) = ParseRequest(toolCall.Arguments);
        if (parseError is not null)
        {
            return Error(toolCall.Id, parseError.Code, parseError.Message);
        }

        // Validate URLs + host guard up-front so we don't half-clone before discovering a
        // host violation on a later entry.
        var parsedRefs = new List<(RepositoryRequestEntry Entry, RepoReference Reference)>(request!.Repositories.Count);
        foreach (var entry in request.Repositories)
        {
            RepoReference parsedRef;
            try
            {
                parsedRef = RepoReference.Parse(entry.Url);
            }
            catch (ArgumentException ex)
            {
                return Error(toolCall.Id, "url_invalid", ex.Message, url: entry.Url);
            }

            if (hostGuard is not null)
            {
                try
                {
                    await hostGuard.AssertAllowedAsync(parsedRef, cancellationToken);
                }
                catch (RepoUrlHostMismatchException ex)
                {
                    return Error(toolCall.Id, "host_not_allowed", ex.Message, url: entry.Url);
                }
            }

            parsedRefs.Add((entry, parsedRef));
        }

        // Resolve credentials for the union of hosts. An empty result here means none of the
        // requested hosts maps to a configured GitHostSettings token — fail fast rather than
        // attempt clones that will hit `Authentication failed` at the network level.
        var requestedUrls = parsedRefs.Select(p => p.Entry.Url).ToArray();
        var credentials = await credentialResolver.ResolveAsync(requestedUrls, cancellationToken);
        if (credentials.Count == 0)
        {
            return Error(
                toolCall.Id,
                "auth_unavailable",
                "No GitHostSettings token is configured for any of the requested repository hosts. "
                + "Configure a token before launching code-aware workflows.");
        }

        // Write the per-trace cred file. The credential helper env (sc-661) already points
        // every spawned `git` at this file; writing here makes subsequent ops in this turn
        // and beyond authenticate transparently.
        try
        {
            await GitCredentialFile.WriteAsync(
                workspaceOptions.GitCredentialRoot,
                workspace.CorrelationId,
                credentials,
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error(
                toolCall.Id,
                "credential_file_write_failed",
                $"Could not write the per-trace credential file at {workspaceOptions.GitCredentialRoot}: {ex.Message}");
        }

        var credentialEnv = GitCredentialEnv.Build(workspaceOptions.GitCredentialRoot, workspace.CorrelationId);

        // Synthesize a feature-branch prefix when the caller didn't supply one. We use the
        // first 8 chars of the trace id so two concurrent traces in the same repo can't
        // accidentally collide on branch names.
        var featureBranchPrefix = string.IsNullOrWhiteSpace(request.FeatureBranchPrefix)
            ? $"codeflow/trace-{workspace.CorrelationId.ToString("N")[..8]}"
            : request.FeatureBranchPrefix.Trim();

        // sc-X (2026-05-06): one canonical bag key for repository state. The framework's
        // saga-state-machine lift only reads `{url, branch}` from each entry but ignores
        // extra fields, so the rich shape (localPath / featureBranch / baseSha / …) lives
        // under the same `repositories` key the saga + Authority + trace cleanup consume.
        // Keeping a separate `repos` array invited the "which key do I read" confusion the
        // assistant skill was repeating to authors.
        var resultRepos = new JsonArray();

        foreach (var (entry, parsedRef) in parsedRefs)
        {
            var localRelative = parsedRef.Name;
            string localPath;
            try
            {
                localPath = PathConfinement.Resolve(workspace.RootPath, localRelative);
            }
            catch (PathConfinementException ex)
            {
                return Error(toolCall.Id, "path_confined", ex.Message, url: entry.Url);
            }

            var alreadyPresent = Directory.Exists(Path.Combine(localPath, ".git"));

            if (!alreadyPresent)
            {
                try
                {
                    await gitCli.CloneAsync(
                        entry.Url,
                        localPath,
                        branch: null,
                        depth: null,
                        environmentVariables: credentialEnv,
                        cancellationToken: cancellationToken);
                }
                catch (GitCommandException ex)
                {
                    return Error(toolCall.Id, "clone_failed", ex.Message, url: entry.Url);
                }
            }

            // Resolve the upstream default branch. Always via ls-remote — never trust the
            // user-supplied hint silently. If a hint was supplied and disagrees with the
            // remote, refuse loudly so the caller can fix the input rather than open a PR
            // against the wrong base.
            string remoteHead;
            try
            {
                remoteHead = await gitCli.GetRemoteHeadBranchAsync(
                    localPath,
                    remote: "origin",
                    environmentVariables: credentialEnv,
                    cancellationToken: cancellationToken);
            }
            catch (GitCommandException ex)
            {
                return Error(toolCall.Id, "base_branch_lookup_failed", ex.Message, url: entry.Url);
            }
            catch (InvalidOperationException ex)
            {
                return Error(toolCall.Id, "base_branch_lookup_failed", ex.Message, url: entry.Url);
            }

            if (!string.IsNullOrWhiteSpace(entry.Branch)
                && !string.Equals(entry.Branch, remoteHead, StringComparison.Ordinal))
            {
                return Error(
                    toolCall.Id,
                    "base_branch_mismatch",
                    $"Caller-supplied branch '{entry.Branch}' does not match the remote default '{remoteHead}'. "
                    + "Either correct the input or omit `branch` to use the remote default.",
                    url: entry.Url,
                    extra: new JsonObject
                    {
                        ["expected"] = entry.Branch,
                        ["actual"] = remoteHead,
                    });
            }

            var baseBranch = remoteHead;
            var featureBranch = $"{featureBranchPrefix}/{parsedRef.Name}";

            if (!alreadyPresent)
            {
                try
                {
                    await gitCli.CreateBranchAsync(localPath, featureBranch, startPoint: baseBranch, cancellationToken);
                }
                catch (GitCommandException ex)
                {
                    return Error(toolCall.Id, "branch_create_failed", ex.Message, url: entry.Url);
                }

                try
                {
                    await gitCli.PushAsync(
                        localPath,
                        remote: "origin",
                        branch: featureBranch,
                        environmentVariables: credentialEnv,
                        cancellationToken: cancellationToken);
                }
                catch (GitCommandException ex)
                {
                    return Error(toolCall.Id, "push_failed", ex.Message, url: entry.Url);
                }
            }

            string baseSha;
            try
            {
                baseSha = await gitCli.RevParseAsync(localPath, baseBranch, cancellationToken);
            }
            catch (GitCommandException ex)
            {
                return Error(toolCall.Id, "rev_parse_failed", ex.Message, url: entry.Url);
            }

            // sc-X: `branch` is duplicated alongside `baseBranch` so the framework's slim
            // {url, branch} reader (ParseRepositoriesJson) sees the canonical field, while
            // downstream Scriban templates can keep using the descriptive `baseBranch`.
            resultRepos.Add(new JsonObject
            {
                ["url"] = entry.Url,
                ["branch"] = baseBranch,
                ["localPath"] = MakeWorkspaceRelativePath(workspace.RootPath, localPath),
                ["baseBranch"] = baseBranch,
                ["featureBranch"] = featureBranch,
                ["baseSha"] = baseSha,
                ["alreadyPresent"] = alreadyPresent,
            });
        }

        // Stage the workflow.repositories update so on submit, saga.RepositoriesJson is
        // updated and downstream `vcs.open_pr` calls pass the per-trace allowlist check.
        // Sink is null in test contexts that don't go through InvocationLoop — repos are
        // still set up on disk, the agent just needs to mirror via setWorkflow itself.
        if (context!.StageWorkflowBagWrite is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(resultRepos.ToJsonString());
                context.StageWorkflowBagWrite("repositories", doc.RootElement.Clone());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return Error(
                    toolCall.Id,
                    "stage_repositories_failed",
                    $"Could not stage workflow.repositories update: {ex.Message}");
            }
        }

        return new ToolResult(
            toolCall.Id,
            new JsonObject
            {
                ["repositories"] = resultRepos,
            }.ToJsonString());
    }

    private static (RepositoryRequest? Request, RequestParseError? Error) ParseRequest(JsonNode? arguments)
    {
        if (arguments is not JsonObject obj)
        {
            return (null, new RequestParseError("arguments_required", "setup_workspace requires an object argument payload."));
        }

        if (obj["repositories"] is not JsonArray array || array.Count == 0)
        {
            return (null, new RequestParseError(
                "repositories_required",
                "setup_workspace requires a non-empty 'repositories' array of {url, branch?} entries."));
        }

        var entries = new List<RepositoryRequestEntry>();
        var index = 0;
        foreach (var raw in array)
        {
            if (raw is not JsonObject repoObj)
            {
                return (null, new RequestParseError(
                    "repositories_invalid",
                    $"repositories[{index}] must be an object with at least a 'url' string."));
            }

            var url = (repoObj["url"] as JsonValue)?.TryGetValue<string>(out var u) == true ? u : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                return (null, new RequestParseError(
                    "repositories_invalid",
                    $"repositories[{index}] is missing a non-empty 'url' string."));
            }

            string? branch = null;
            if (repoObj["branch"] is JsonValue branchValue
                && branchValue.TryGetValue<string>(out var b)
                && !string.IsNullOrWhiteSpace(b))
            {
                branch = b.Trim();
            }

            entries.Add(new RepositoryRequestEntry(url.Trim(), branch));
            index++;
        }

        var prefix = (obj["featureBranchPrefix"] as JsonValue)?.TryGetValue<string>(out var p) == true ? p : null;

        return (new RepositoryRequest(entries, prefix), null);
    }

    private static ToolResult Error(string callId, string code, string message, string? url = null, JsonObject? extra = null)
    {
        var payload = new JsonObject
        {
            ["error"] = code,
            ["message"] = message,
        };
        if (!string.IsNullOrWhiteSpace(url))
        {
            payload["url"] = url;
        }
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                payload[key] = value?.DeepClone();
            }
        }
        return new ToolResult(callId, payload.ToJsonString(), IsError: true);
    }

    private static string MakeWorkspaceRelativePath(string workspaceRoot, string absolutePath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), absolutePath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private sealed record RepositoryRequest(IReadOnlyList<RepositoryRequestEntry> Repositories, string? FeatureBranchPrefix);

    private sealed record RepositoryRequestEntry(string Url, string? Branch);

    private sealed record RequestParseError(string Code, string Message);
}
