using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Runtime.Workspace;

public sealed class VcsToolProvider : IToolProvider
{
    public const string CreateBranchToolName = "vcs.create_branch";
    public const string CommitToolName = "vcs.commit";
    public const string PushToolName = "vcs.push";
    public const string OpenPrToolName = "vcs.open_pr";

    private readonly IWorkspaceService workspaceService;
    private readonly IGitCli git;
    private readonly IVcsProviderFactory vcsProviderFactory;
    private readonly IGitHostTokenProvider tokenProvider;
    private readonly ILogger logger;

    public VcsToolProvider(
        IWorkspaceService workspaceService,
        IGitCli git,
        IVcsProviderFactory vcsProviderFactory,
        IGitHostTokenProvider tokenProvider,
        ILogger<VcsToolProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceService);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(vcsProviderFactory);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        this.workspaceService = workspaceService;
        this.git = git;
        this.vcsProviderFactory = vcsProviderFactory;
        this.tokenProvider = tokenProvider;
        this.logger = logger ?? NullLogger<VcsToolProvider>.Instance;
    }

    public ToolCategory Category => ToolCategory.Host;

    public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var limit = policy.GetCategoryLimit(Category);
        if (limit <= 0)
        {
            return [];
        }

        return GetCatalog().Take(limit).ToArray();
    }

    public async Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        return toolCall.Name switch
        {
            CreateBranchToolName => await CreateBranchAsync(toolCall, context, cancellationToken),
            CommitToolName => await CommitAsync(toolCall, context, cancellationToken),
            PushToolName => await PushAsync(toolCall, context, cancellationToken),
            OpenPrToolName => await OpenPullRequestAsync(toolCall, context, cancellationToken),
            _ => throw new UnknownToolException(toolCall.Name)
        };
    }

    public static IReadOnlyList<ToolSchema> GetCatalog() =>
    [
        new ToolSchema(
            CreateBranchToolName,
            "Create and check out a new branch in the workspace. If name is omitted, a codeflow/wt/<correlation>/<slug> branch is generated.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repoSlug"] = new JsonObject { ["type"] = "string" },
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Optional explicit branch name." },
                    ["baseRef"] = new JsonObject { ["type"] = "string", ["description"] = "Optional branch or ref to base off; defaults to the current HEAD." }
                },
                ["required"] = new JsonArray("repoSlug")
            },
            IsMutating: true),
        new ToolSchema(
            CommitToolName,
            "Stage all changes and commit with the supplied message. Returns committed=false when there is nothing to commit.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repoSlug"] = new JsonObject { ["type"] = "string" },
                    ["message"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("repoSlug", "message")
            },
            IsMutating: true),
        new ToolSchema(
            PushToolName,
            "Push the current branch to origin. Rejects pushes that target the repository's default branch. Force-push is not supported.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repoSlug"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("repoSlug")
            },
            IsMutating: true),
        new ToolSchema(
            OpenPrToolName,
            "Open a pull request (GitHub) or merge request (GitLab) from the current branch to the base branch.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repoSlug"] = new JsonObject { ["type"] = "string" },
                    ["title"] = new JsonObject { ["type"] = "string" },
                    ["body"] = new JsonObject { ["type"] = "string" },
                    ["base"] = new JsonObject { ["type"] = "string", ["description"] = "Optional target branch. Defaults to the repo's default branch." }
                },
                ["required"] = new JsonArray("repoSlug", "title")
            },
            IsMutating: true),
    ];

    private async Task<ToolResult> CreateBranchAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken)
    {
        var repoSlug = GetRequiredString(toolCall.Arguments, "repoSlug");
        var workspace = workspaceService.Get(context.CorrelationId, repoSlug);
        if (workspace is null)
        {
            return NotOpen(toolCall, repoSlug);
        }

        var explicitName = GetOptionalString(toolCall.Arguments, "name");
        var baseRef = GetOptionalString(toolCall.Arguments, "baseRef");
        var branchName = explicitName ?? BuildDefaultBranchName(context.CorrelationId, workspace.Repo);

        try
        {
            await git.CreateBranchAsync(workspace.RootPath, branchName, baseRef, cancellationToken);
        }
        catch (GitCommandException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }

        workspace.CurrentBranch = branchName;
        return new ToolResult(toolCall.Id, new JsonObject
        {
            ["branch"] = branchName,
        }.ToJsonString());
    }

    private async Task<ToolResult> CommitAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken)
    {
        var repoSlug = GetRequiredString(toolCall.Arguments, "repoSlug");
        var message = GetRequiredString(toolCall.Arguments, "message");

        var workspace = workspaceService.Get(context.CorrelationId, repoSlug);
        if (workspace is null)
        {
            return NotOpen(toolCall, repoSlug);
        }

        try
        {
            await git.AddAsync(workspace.RootPath, paths: null, cancellationToken);
            var committed = await git.CommitAsync(workspace.RootPath, message, cancellationToken);

            if (!committed)
            {
                return new ToolResult(toolCall.Id, new JsonObject
                {
                    ["committed"] = false,
                    ["message"] = "No changes to commit.",
                }.ToJsonString());
            }

            var sha = await git.RevParseAsync(workspace.RootPath, "HEAD", cancellationToken);
            return new ToolResult(toolCall.Id, new JsonObject
            {
                ["committed"] = true,
                ["sha"] = sha,
            }.ToJsonString());
        }
        catch (GitCommandException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }
    }

    private async Task<ToolResult> PushAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken)
    {
        var repoSlug = GetRequiredString(toolCall.Arguments, "repoSlug");
        var workspace = workspaceService.Get(context.CorrelationId, repoSlug);
        if (workspace is null)
        {
            return NotOpen(toolCall, repoSlug);
        }

        var currentBranch = await ResolveCurrentBranchAsync(workspace, cancellationToken);
        if (string.Equals(currentBranch, workspace.DefaultBranch, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult(
                toolCall.Id,
                $"Refusing to push to the default branch '{workspace.DefaultBranch}'. Create a work branch first via vcs.create_branch.",
                IsError: true);
        }

        try
        {
            using var lease = await tokenProvider.AcquireAsync(cancellationToken);
            await git.PushWithBearerAsync(
                workspace.RootPath,
                lease.Token,
                remote: "origin",
                branch: currentBranch,
                cancellationToken);
        }
        catch (GitCommandException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }
        catch (GitHostNotConfiguredException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }

        logger.LogInformation(
            "vcs.push {CorrelationId} {RepoSlug} {Branch}",
            context.CorrelationId,
            repoSlug,
            currentBranch);

        workspace.CurrentBranch = currentBranch;
        return new ToolResult(toolCall.Id, new JsonObject
        {
            ["pushed"] = true,
            ["branch"] = currentBranch,
        }.ToJsonString());
    }

    private async Task<ToolResult> OpenPullRequestAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken)
    {
        var repoSlug = GetRequiredString(toolCall.Arguments, "repoSlug");
        var title = GetRequiredString(toolCall.Arguments, "title");
        var body = GetOptionalString(toolCall.Arguments, "body") ?? string.Empty;
        var baseRef = GetOptionalString(toolCall.Arguments, "base");

        var workspace = workspaceService.Get(context.CorrelationId, repoSlug);
        if (workspace is null)
        {
            return NotOpen(toolCall, repoSlug);
        }

        var head = await ResolveCurrentBranchAsync(workspace, cancellationToken);
        var targetBase = string.IsNullOrWhiteSpace(baseRef) ? workspace.DefaultBranch : baseRef!;

        try
        {
            var provider = await vcsProviderFactory.CreateAsync(cancellationToken);
            var pr = await provider.OpenPullRequestAsync(
                workspace.Repo.Owner,
                workspace.Repo.Name,
                head,
                targetBase,
                title,
                body,
                cancellationToken);

            return new ToolResult(toolCall.Id, new JsonObject
            {
                ["url"] = pr.Url,
                ["number"] = pr.Number,
                ["head"] = head,
                ["base"] = targetBase,
            }.ToJsonString());
        }
        catch (VcsException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }
        catch (GitHostNotConfiguredException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }
    }

    private async Task<string> ResolveCurrentBranchAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var branch = await git.GetSymbolicHeadAsync(workspace.RootPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(branch))
            {
                workspace.CurrentBranch = branch;
                return branch;
            }
        }
        catch (GitCommandException)
        {
        }

        return workspace.CurrentBranch;
    }

    private static ToolResult NotOpen(ToolCall toolCall, string repoSlug)
        => new(toolCall.Id, $"Workspace '{repoSlug}' is not open in this correlation.", IsError: true);

    private static string BuildDefaultBranchName(Guid correlationId, RepoReference repo)
    {
        var shortCorrelation = correlationId.ToString("N")[..8];
        var shortSlug = repo.Slug.Length > 40 ? repo.Slug[..40] : repo.Slug;
        return $"codeflow/{shortCorrelation}/{shortSlug}";
    }

    private static string GetRequiredString(JsonNode? arguments, string name)
    {
        if (arguments?[name] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new InvalidOperationException($"The '{name}' argument is required.");
    }

    private static string? GetOptionalString(JsonNode? arguments, string name)
    {
        if (arguments?[name] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        return null;
    }
}
