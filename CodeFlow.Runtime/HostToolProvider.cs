using System.Text.Json.Nodes;
using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Runtime;

public sealed class HostToolProvider : IToolProvider
{
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly WorkspaceHostToolService workspaceTools;
    private readonly VcsHostToolService? vcsTools;

    public HostToolProvider(
        Func<DateTimeOffset>? nowProvider = null,
        WorkspaceHostToolService? workspaceTools = null,
        VcsHostToolService? vcsTools = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        this.workspaceTools = workspaceTools ?? new WorkspaceHostToolService();
        this.vcsTools = vcsTools;
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

    public Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var content = toolCall.Name switch
        {
            "echo" => Task.FromResult(new ToolResult(toolCall.Id, GetEchoText(toolCall.Arguments))),
            "now" => Task.FromResult(new ToolResult(toolCall.Id, nowProvider().ToString("O"))),
            "read_file" => workspaceTools.ReadFileAsync(toolCall, context, cancellationToken),
            "apply_patch" => workspaceTools.ApplyPatchAsync(toolCall, context, cancellationToken),
            "run_command" => workspaceTools.RunCommandAsync(toolCall, context, cancellationToken),
            "vcs.open_pr" => RequireVcs().OpenPullRequestAsync(toolCall, cancellationToken),
            "vcs.get_repo" => RequireVcs().GetRepoMetadataAsync(toolCall, cancellationToken),
            _ => throw new UnknownToolException(toolCall.Name)
        };

        return content;
    }

    private VcsHostToolService RequireVcs() =>
        vcsTools ?? throw new InvalidOperationException(
            "vcs.* tools are not configured: HostToolProvider was constructed without a VcsHostToolService.");

    public static IReadOnlyList<ToolSchema> GetCatalog()
    {
        return
        [
            new ToolSchema(
                "echo",
                "Returns the supplied text unchanged.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["text"] = new JsonObject
                        {
                            ["type"] = "string"
                        }
                    }
                }),
            new ToolSchema(
                "now",
                "Returns the current UTC timestamp in ISO 8601 format.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }),
            new ToolSchema(
                "read_file",
                "Reads a file from the active workspace and returns its contents.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string"
                        }
                    },
                    ["required"] = new JsonArray("path")
                }),
            new ToolSchema(
                "apply_patch",
                "Applies a structured patch to files inside the active workspace.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["patch"] = new JsonObject
                        {
                            ["type"] = "string"
                        }
                    },
                    ["required"] = new JsonArray("patch")
                },
                IsMutating: true),
            new ToolSchema(
                "run_command",
                "Runs a command inside the active workspace without invoking a shell.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = new JsonObject
                        {
                            ["type"] = "string"
                        },
                        ["args"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "string"
                            }
                        },
                        ["workingDirectory"] = new JsonObject
                        {
                            ["type"] = "string"
                        },
                        ["timeoutSeconds"] = new JsonObject
                        {
                            ["type"] = "integer"
                        }
                    },
                    ["required"] = new JsonArray("command")
                },
                IsMutating: true),
            new ToolSchema(
                "vcs.open_pr",
                "Opens a pull request (or merge request, on GitLab) on the configured Git host. "
                + "Returns the URL and number of the created request. Auth is platform-managed via "
                + "GitHostSettings; agents do not handle the token themselves.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["owner"] = new JsonObject { ["type"] = "string" },
                        ["name"] = new JsonObject { ["type"] = "string" },
                        ["head"] = new JsonObject { ["type"] = "string" },
                        ["base"] = new JsonObject { ["type"] = "string" },
                        ["title"] = new JsonObject { ["type"] = "string" },
                        ["body"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("owner", "name", "head", "base", "title")
                },
                IsMutating: true),
            new ToolSchema(
                "vcs.get_repo",
                "Reads basic metadata for a repository on the configured Git host (default branch, "
                + "clone URL, visibility). Useful before opening a PR to confirm the upstream "
                + "default branch.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["owner"] = new JsonObject { ["type"] = "string" },
                        ["name"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("owner", "name")
                })
        ];
    }

    private static string GetEchoText(JsonNode? arguments)
    {
        if (arguments?["text"] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return string.Empty;
    }
}
