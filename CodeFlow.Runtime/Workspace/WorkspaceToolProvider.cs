using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Runtime.Workspace;

public sealed class WorkspaceToolProvider : IToolProvider
{
    public const string OpenToolName = "workspace.open";
    public const string ListFilesToolName = "workspace.list_files";
    public const string ReadFileToolName = "workspace.read_file";
    public const string WriteFileToolName = "workspace.write_file";
    public const string DeleteFileToolName = "workspace.delete_file";

    private readonly IWorkspaceService workspaceService;
    private readonly WorkspaceOptions options;
    private readonly ILogger logger;

    public WorkspaceToolProvider(
        IWorkspaceService workspaceService,
        WorkspaceOptions options,
        ILogger<WorkspaceToolProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceService);
        ArgumentNullException.ThrowIfNull(options);

        this.workspaceService = workspaceService;
        this.options = options;
        this.logger = logger ?? NullLogger<WorkspaceToolProvider>.Instance;
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
            OpenToolName => await OpenAsync(toolCall, context, cancellationToken),
            ListFilesToolName => ListFiles(toolCall, context),
            ReadFileToolName => await ReadFileAsync(toolCall, context, cancellationToken),
            WriteFileToolName => await WriteFileAsync(toolCall, context, cancellationToken),
            DeleteFileToolName => DeleteFile(toolCall, context),
            _ => throw new UnknownToolException(toolCall.Name)
        };
    }

    public static IReadOnlyList<ToolSchema> GetCatalog() =>
    [
        new ToolSchema(
            OpenToolName,
            "Open (or reuse) an isolated workspace for a git repository inside the current workflow. Returns a repoSlug handle used by other workspace tools.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repoUrl"] = new JsonObject { ["type"] = "string", ["description"] = "HTTPS URL of the repository to clone/fetch." },
                    ["baseBranch"] = new JsonObject { ["type"] = "string", ["description"] = "Optional branch to base the workspace off. Defaults to the repo's default branch." }
                },
                ["required"] = new JsonArray("repoUrl")
            }),
        new ToolSchema(
            ListFilesToolName,
            "List tracked and untracked files under the workspace, optionally scoped to a subpath.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repoSlug"] = new JsonObject { ["type"] = "string" },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Optional workspace-relative subdirectory." }
                },
                ["required"] = new JsonArray("repoSlug")
            }),
        new ToolSchema(
            ReadFileToolName,
            "Read a workspace file as UTF-8 text. Rejects files larger than the configured read cap.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repoSlug"] = new JsonObject { ["type"] = "string" },
                    ["path"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("repoSlug", "path")
            }),
        new ToolSchema(
            WriteFileToolName,
            "Create or overwrite a workspace file with UTF-8 text content. Creates parent directories as needed.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repoSlug"] = new JsonObject { ["type"] = "string" },
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["content"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("repoSlug", "path", "content")
            },
            IsMutating: true),
        new ToolSchema(
            DeleteFileToolName,
            "Delete a workspace file. Returns an error if the file does not exist.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repoSlug"] = new JsonObject { ["type"] = "string" },
                    ["path"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("repoSlug", "path")
            },
            IsMutating: true),
    ];

    private async Task<ToolResult> OpenAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken)
    {
        var repoUrl = GetRequiredString(toolCall.Arguments, "repoUrl");
        var baseBranch = GetOptionalString(toolCall.Arguments, "baseBranch");

        try
        {
            var workspace = await workspaceService.OpenAsync(
                context.CorrelationId,
                repoUrl,
                baseBranch,
                cancellationToken);

            var payload = new JsonObject
            {
                ["repoSlug"] = workspace.RepoSlug,
                ["defaultBranch"] = workspace.DefaultBranch,
                ["currentBranch"] = workspace.CurrentBranch,
            };
            return new ToolResult(toolCall.Id, payload.ToJsonString());
        }
        catch (Exception ex) when (ex is ArgumentException or RepoUrlHostMismatchException)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }
    }

    private ToolResult ListFiles(ToolCall toolCall, AgentInvocationContext context)
    {
        var repoSlug = GetRequiredString(toolCall.Arguments, "repoSlug");
        var subPath = GetOptionalString(toolCall.Arguments, "path");

        var workspace = workspaceService.Get(context.CorrelationId, repoSlug);
        if (workspace is null)
        {
            return new ToolResult(toolCall.Id, $"Workspace '{repoSlug}' is not open in this correlation.", IsError: true);
        }

        string scope;
        try
        {
            scope = string.IsNullOrWhiteSpace(subPath)
                ? workspace.RootPath
                : PathConfinement.Resolve(workspace.RootPath, subPath);
        }
        catch (PathConfinementException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }

        if (!Directory.Exists(scope))
        {
            return new ToolResult(toolCall.Id, $"Path '{subPath}' does not exist or is not a directory.", IsError: true);
        }

        var rootPath = workspace.RootPath;
        var files = Directory
            .EnumerateFiles(scope, "*", SearchOption.AllDirectories)
            .Where(path => !IsUnderGitDir(path, rootPath))
            .Select(path => Path.GetRelativePath(rootPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var array = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        return new ToolResult(toolCall.Id, array.ToJsonString());
    }

    private async Task<ToolResult> ReadFileAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken)
    {
        var repoSlug = GetRequiredString(toolCall.Arguments, "repoSlug");
        var path = GetRequiredString(toolCall.Arguments, "path");

        var workspace = workspaceService.Get(context.CorrelationId, repoSlug);
        if (workspace is null)
        {
            return new ToolResult(toolCall.Id, $"Workspace '{repoSlug}' is not open in this correlation.", IsError: true);
        }

        string resolved;
        try
        {
            resolved = PathConfinement.Resolve(workspace.RootPath, path);
        }
        catch (PathConfinementException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }

        if (!File.Exists(resolved))
        {
            return new ToolResult(toolCall.Id, $"File '{path}' does not exist in workspace.", IsError: true);
        }

        var length = new FileInfo(resolved).Length;
        if (length > options.ReadMaxBytes)
        {
            return new ToolResult(
                toolCall.Id,
                $"File '{path}' is {length} bytes which exceeds the read cap of {options.ReadMaxBytes} bytes.",
                IsError: true);
        }

        var content = await File.ReadAllTextAsync(resolved, Encoding.UTF8, cancellationToken);
        return new ToolResult(toolCall.Id, content);
    }

    private async Task<ToolResult> WriteFileAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken)
    {
        var repoSlug = GetRequiredString(toolCall.Arguments, "repoSlug");
        var path = GetRequiredString(toolCall.Arguments, "path");
        var content = GetRequiredContent(toolCall.Arguments, "content");

        var workspace = workspaceService.Get(context.CorrelationId, repoSlug);
        if (workspace is null)
        {
            return new ToolResult(toolCall.Id, $"Workspace '{repoSlug}' is not open in this correlation.", IsError: true);
        }

        string resolved;
        try
        {
            resolved = PathConfinement.Resolve(workspace.RootPath, path);
        }
        catch (PathConfinementException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }

        var parent = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(resolved, bytes, cancellationToken);

        logger.LogInformation(
            "workspace.file.write {CorrelationId} {RepoSlug} {Path} {ByteCount}",
            context.CorrelationId,
            repoSlug,
            path,
            bytes.Length);

        return new ToolResult(toolCall.Id, new JsonObject
        {
            ["path"] = path,
            ["byteCount"] = bytes.Length,
        }.ToJsonString());
    }

    private ToolResult DeleteFile(ToolCall toolCall, AgentInvocationContext context)
    {
        var repoSlug = GetRequiredString(toolCall.Arguments, "repoSlug");
        var path = GetRequiredString(toolCall.Arguments, "path");

        var workspace = workspaceService.Get(context.CorrelationId, repoSlug);
        if (workspace is null)
        {
            return new ToolResult(toolCall.Id, $"Workspace '{repoSlug}' is not open in this correlation.", IsError: true);
        }

        string resolved;
        try
        {
            resolved = PathConfinement.Resolve(workspace.RootPath, path);
        }
        catch (PathConfinementException ex)
        {
            return new ToolResult(toolCall.Id, ex.Message, IsError: true);
        }

        if (!File.Exists(resolved))
        {
            return new ToolResult(toolCall.Id, $"File '{path}' does not exist in workspace.", IsError: true);
        }

        File.Delete(resolved);

        logger.LogInformation(
            "workspace.file.delete {CorrelationId} {RepoSlug} {Path}",
            context.CorrelationId,
            repoSlug,
            path);

        return new ToolResult(toolCall.Id, new JsonObject { ["path"] = path }.ToJsonString());
    }

    private static string GetRequiredContent(JsonNode? arguments, string name)
    {
        if (arguments?[name] is JsonValue value
            && value.TryGetValue<string>(out var result))
        {
            return result ?? string.Empty;
        }

        throw new InvalidOperationException($"The '{name}' argument is required.");
    }

    private static bool IsUnderGitDir(string fullPath, string rootPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
        return relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal);
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
