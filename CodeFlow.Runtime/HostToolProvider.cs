using System.Text.Json.Nodes;
using CodeFlow.Runtime.Container;
using CodeFlow.Runtime.Web;
using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Runtime;

public sealed class HostToolProvider : IToolProvider
{
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly WorkspaceHostToolService workspaceTools;
    private readonly VcsHostToolService? vcsTools;
    private readonly SetupWorkspaceHostToolService? setupWorkspaceTools;
    private readonly DockerHostToolService? containerTools;
    private readonly WebHostToolService? webTools;

    public HostToolProvider(
        Func<DateTimeOffset>? nowProvider = null,
        WorkspaceHostToolService? workspaceTools = null,
        VcsHostToolService? vcsTools = null,
        SetupWorkspaceHostToolService? setupWorkspaceTools = null,
        DockerHostToolService? containerTools = null,
        WebHostToolService? webTools = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        this.workspaceTools = workspaceTools ?? new WorkspaceHostToolService();
        this.vcsTools = vcsTools;
        this.setupWorkspaceTools = setupWorkspaceTools;
        this.containerTools = containerTools;
        this.webTools = webTools;
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
            "bulk_replace" => workspaceTools.BulkReplaceAsync(toolCall, context, cancellationToken),
            "run_command" => workspaceTools.RunCommandAsync(toolCall, context, cancellationToken),
            "vcs.open_pr" => RequireVcs().OpenPullRequestAsync(toolCall, context, cancellationToken),
            "vcs.get_repo" => RequireVcs().GetRepoMetadataAsync(toolCall, context, cancellationToken),
            "vcs.clone" => RequireVcs().CloneAsync(toolCall, context, cancellationToken),
            SetupWorkspaceHostToolService.ToolName => RequireSetupWorkspace().SetupWorkspaceAsync(toolCall, context, cancellationToken),
            DockerHostToolService.ContainerRunToolName => RequireContainer().RunContainerAsync(toolCall, context, cancellationToken),
            WebHostToolService.WebFetchToolName => RequireWeb().FetchAsync(toolCall, context, cancellationToken),
            WebHostToolService.WebSearchToolName => RequireWeb().SearchAsync(toolCall, context, cancellationToken),
            _ => throw new UnknownToolException(toolCall.Name)
        };

        return content;
    }

    private VcsHostToolService RequireVcs() =>
        vcsTools ?? throw new InvalidOperationException(
            "vcs.* tools are not configured: HostToolProvider was constructed without a VcsHostToolService.");

    private SetupWorkspaceHostToolService RequireSetupWorkspace() =>
        setupWorkspaceTools ?? throw new InvalidOperationException(
            "setup_workspace is not configured: HostToolProvider was constructed without a SetupWorkspaceHostToolService.");

    private DockerHostToolService RequireContainer() =>
        containerTools ?? throw new InvalidOperationException(
            "container.* tools are not configured: HostToolProvider was constructed without a DockerHostToolService.");

    private WebHostToolService RequireWeb() =>
        webTools ?? throw new InvalidOperationException(
            "web_* tools are not configured: HostToolProvider was constructed without a WebHostToolService.");

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
                "Applies a structured patch to files inside the active workspace.\n"
                + "\n"
                + "IMPORTANT: this tool uses the V4A patch format, NOT unified diff. Patches in "
                + "`diff -u`, `git diff`, or `--- a/file +++ b/file` format will be rejected with "
                + "`patch-malformed`. The V4A format is its own thing — read the examples below "
                + "before composing a patch.\n"
                + "\n"
                + "## Envelope\n"
                + "\n"
                + "Every patch starts with `*** Begin Patch` on its own line and ends with "
                + "`*** End Patch` on its own line. Between them goes one or more commands; each "
                + "command's body ends where the next `*** ` line starts.\n"
                + "\n"
                + "## Command 1: add a file\n"
                + "\n"
                + "Header `*** Add File: <workspace-relative-path>` followed by every line of "
                + "the new file's contents, each prefixed with `+`. Example:\n"
                + "\n"
                + "```\n"
                + "*** Begin Patch\n"
                + "*** Add File: scratch/hello.txt\n"
                + "+Hello-from-Goal-Node\n"
                + "*** End Patch\n"
                + "```\n"
                + "\n"
                + "Empty lines also need the `+` (a bare `+` on its own line means \"add an "
                + "empty line\"). Do NOT use `---` / `+++` headers.\n"
                + "\n"
                + "## Command 2: delete a file\n"
                + "\n"
                + "Header `*** Delete File: <path>` with no body. Optional preimage hash:\n"
                + "\n"
                + "```\n"
                + "*** Begin Patch\n"
                + "*** Delete File: scratch/obsolete.txt\n"
                + "*** End Patch\n"
                + "```\n"
                + "\n"
                + "## Command 3: update an existing file\n"
                + "\n"
                + "Header `*** Update File: <path>`. Body is a sequence of context, addition, "
                + "and deletion lines, ALL prefixed with one character: space for context, `+` "
                + "to add, `-` to remove. Optional `@@` hunk markers and `*** End of File` "
                + "sentinels are allowed. Example:\n"
                + "\n"
                + "```\n"
                + "*** Begin Patch\n"
                + "*** Update File: src/greeting.py\n"
                + " def greet(name):\n"
                + "-    return f\"Hello, {name}\"\n"
                + "+    return f\"Hello, {name}! Welcome.\"\n"
                + " \n"
                + " def farewell(name):\n"
                + "*** End Patch\n"
                + "```\n"
                + "\n"
                + "## Command 4: rename + update in one go\n"
                + "\n"
                + "After `*** Update File:` add `*** Move to: <new-path>` on its own line, then "
                + "the body. The body's context lines must match the OLD file's contents.\n"
                + "\n"
                + "## Common mistakes that get rejected\n"
                + "\n"
                + "- Unified-diff `--- a/file` / `+++ b/file` headers → not recognised; just use "
                + "`*** Update File: file`.\n"
                + "- Forgetting the `+`/`-`/space prefix on a body line → `invalid line` refusal.\n"
                + "- Body lines starting with `\\` (the unified-diff \"no newline\" marker) → not "
                + "supported; the tool handles trailing-newline semantics itself.\n"
                + "- Multiple `*** Begin Patch` / `*** End Patch` envelopes in one call → only "
                + "the first envelope is honoured.\n"
                + "\n"
                + "If you only know unified-diff format, use `run_command` with `sh -c` and a "
                + "heredoc to write the file directly instead of using this tool.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["patch"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "V4A patch text. Starts with `*** Begin Patch` and "
                                + "ends with `*** End Patch`. See the tool description for the "
                                + "command grammar and worked examples. Unified-diff format is "
                                + "NOT accepted."
                        }
                    },
                    ["required"] = new JsonArray("patch")
                },
                IsMutating: true),
            new ToolSchema(
                "bulk_replace",
                "Mechanical find-and-replace across many files in one call. Use this for broad "
                + "renames or other context-free substitutions where apply_patch would be N "
                + "tool calls; prefer apply_patch for context-aware edits and for creating or "
                + "deleting files. The replacement is plain by default; pass `regex: true` to "
                + "interpret `pattern` as a .NET regex (NonBacktracking, with a per-file timeout). "
                + "Skips binary files, symlinks, and well-known build/output dirs (.git, "
                + "node_modules, bin, obj, target, dist, __pycache__, venv). Refuses with "
                + "`too_many_files` rather than half-applying when scope exceeds the configured "
                + "ceiling. If you hit that refusal, narrow `pathGlob` to specific extensions "
                + "(e.g. `**/*.cs`) or pass explicit `paths` rooted at smaller subtrees, then "
                + "retry — splitting one too-broad call into a handful of scoped ones is the "
                + "expected pattern for repo-wide renames. Pass `dryRun: true` to preview "
                + "counts without writing.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["pattern"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Literal text to find, or a regex when `regex: true`."
                        },
                        ["replacement"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Replacement text. With `regex: true`, supports .NET "
                                + "$0/$1/${name} substitution semantics."
                        },
                        ["paths"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "Workspace-relative file or directory paths to scope the "
                                + "scan. Directories are walked recursively. At least one of `paths` "
                                + "or `pathGlob` is required."
                        },
                        ["pathGlob"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Glob filter applied to workspace-relative file paths "
                                + "(e.g. `**/*.cs`, `src/**/*.ts`). Combines with `paths` if both supplied."
                        },
                        ["regex"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "When true, `pattern` is parsed as a .NET regex. Default false."
                        },
                        ["dryRun"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "When true, returns counts without writing. Default false."
                        }
                    },
                    ["required"] = new JsonArray("pattern", "replacement")
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
                }),
            new ToolSchema(
                "vcs.clone",
                "DEPRECATED — use `setup_workspace` instead. setup_workspace handles clone + "
                + "base-branch resolution + feature-branch creation + first push atomically and "
                + "idempotently; vcs.clone is kept registered for back-compat with imported "
                + "workflow packages but new packages should not grant it. The seeded "
                + "code-worker / code-builder system roles no longer include this tool.\n\n"
                + "Original behaviour (still works): materializes a repository into the active "
                + "workspace using the configured Git host's auth. Refuses if the destination "
                + "path already exists. After the initial fetch the remote URL is scrubbed to a "
                + "clean form so the auth token never persists in .git/config.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["url"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "HTTPS repository URL on the configured Git host. "
                                + "Cross-host URLs are denied by the host guard."
                        },
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Workspace-relative destination directory. Defaults "
                                + "to the repository's basename (e.g. `myrepo` for "
                                + "`https://github.com/foo/myrepo`). Must stay confined to the "
                                + "active workspace."
                        },
                        ["branch"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional branch or tag to check out. Defaults to "
                                + "the repository's default branch."
                        },
                        ["depth"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional shallow-clone depth (--depth). Omit for a "
                                + "full clone."
                        }
                    },
                    ["required"] = new JsonArray("url")
                },
                IsMutating: true,
                IsDeprecated: true),
            new ToolSchema(
                SetupWorkspaceHostToolService.ToolName,
                "Atomic, idempotent code-aware bootstrap. Takes a list of repository URLs and, for "
                + "each: writes the per-trace credential file, clones into the workspace, discovers "
                + "the authoritative base branch via `git ls-remote --symref origin HEAD` (NEVER "
                + "defaults to 'main'), creates a per-repo feature branch, and pushes the empty "
                + "branch to validate auth at setup time. Returns rich per-repo state — verified "
                + "baseBranch, featureBranch, baseSha, localPath — that downstream agents read "
                + "directly via `workflow.repositories[i]`. The tool stages a "
                + "setWorkflow('repositories', …) update internally so the per-trace VCS allowlist "
                + "is in sync without the agent having to mirror the result. Idempotent: if a repo "
                + "is already cloned at the expected path it round-trips with `alreadyPresent: true`. "
                + "Use this when an architect or coding agent discovers a missing dependency "
                + "mid-flow — call setup_workspace again with the additional URL; existing repos "
                + "are unchanged, the new one goes through full setup. PREFER THIS over `vcs.clone` "
                + "(deprecated) for code-aware workflows. Structured error codes: auth_unavailable, "
                + "host_not_allowed, url_invalid, path_confined, clone_failed, branch_create_failed, "
                + "push_failed, base_branch_lookup_failed, base_branch_mismatch, "
                + "credential_file_write_failed, rev_parse_failed, stage_repositories_failed.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["repositories"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Non-empty array of `{url, branch?}` entries. `branch` is optional; "
                                + "when supplied it must match the remote's actual default branch (the tool "
                                + "refuses with `base_branch_mismatch` otherwise).",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["url"] = new JsonObject { ["type"] = "string" },
                                    ["branch"] = new JsonObject { ["type"] = "string" },
                                },
                                ["required"] = new JsonArray("url"),
                            },
                        },
                        ["featureBranchPrefix"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional. Defaults to `codeflow/trace` when omitted. "
                                + "The per-repo branch name becomes `<prefix>-<traceId>/<repo-name>`. "
                                + "The trace ID suffix is always appended to ensure uniqueness across concurrent traces.",
                        },
                    },
                    ["required"] = new JsonArray("repositories"),
                },
                IsMutating: true),
            new ToolSchema(
                DockerHostToolService.ContainerRunToolName,
                "Runs a one-shot build/test command inside a constrained Docker container. "
                + "v1 accepts only Docker Hub (docker.io) images. The trace's per-trace "
                + "workspace is bind-mounted at /workspace read-write, so edits the agent "
                + "made via apply_patch / run_command / vcs.clone are visible inside the "
                + "container, and build artifacts the container produces persist back into "
                + "the workspace tree. /workspace/.scratch is an additional fast tmpfs (size-"
                + "capped) for ephemeral build caches that don't need to survive the job. "
                + "CPU/memory/pids/timeout/output limits and a per-workflow container cap are "
                + "enforced. Repository Dockerfiles, docker build, docker compose, privileged "
                + "mode, host networking, published ports, and Docker socket mounts are "
                + "forbidden and surface a structured refusal. Prefer official images and the "
                + "smallest tag that provides the toolchain you need.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["image"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Docker Hub image reference (e.g. 'node:22-bookworm', "
                                + "'library/python:3.12-slim', 'docker.io/library/golang:1.22'). "
                                + "Non-docker.io registries are denied."
                        },
                        ["command"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Executable to run inside the container (e.g. 'npm', "
                                + "'pytest', 'cargo'). Invoking 'docker' or 'docker-compose' is denied."
                        },
                        ["args"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "Positional args passed to the command."
                        },
                        ["workingDirectory"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Workspace-relative directory to run from. Defaults to "
                                + "the workspace root. Paths that escape the workspace are denied."
                        },
                        ["timeoutSeconds"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Per-command timeout. Capped to the configured maximum."
                        }
                    },
                    ["required"] = new JsonArray("image", "command")
                },
                IsMutating: true),
            new ToolSchema(
                WebHostToolService.WebFetchToolName,
                "Fetches readable text from a public HTTP/HTTPS URL. Use this to read official "
                + "framework install/setup guides and Docker Hub image descriptions before "
                + "choosing an image for container.run. Loopback, private, link-local, and "
                + "metadata-service IPs are blocked both pre-flight and after every redirect; "
                + "no credentials, cookies, or auth headers are ever sent. Response size and "
                + "extracted-text length are capped — set 'maxResults' on web_search instead "
                + "of fetching every result.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["url"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Absolute http(s) URL to fetch. Must point at the "
                                + "public internet — internal/private hosts are denied."
                        }
                    },
                    ["required"] = new JsonArray("url")
                }),
            new ToolSchema(
                WebHostToolService.WebSearchToolName,
                "Searches the public web for build/test/install guidance. Prefer queries that "
                + "include the framework name plus 'official docs' or 'docker hub' so the "
                + "first hit is authoritative. Results are bounded by maxResults and re-checked "
                + "against the SSRF blocklist before they reach the agent — never trust the "
                + "snippet alone, always web_fetch the URL to read the actual page.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Search query string."
                        },
                        ["maxResults"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Cap on hit count. Capped to the configured maximum."
                        }
                    },
                    ["required"] = new JsonArray("query")
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
