using System.Globalization;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Runtime.Container;

public sealed class DockerHostToolService
{
    public const string ContainerRunToolName = "container.run";

    private readonly ContainerToolOptions options;
    private readonly IDockerCommandRunner runner;
    private readonly Func<Guid> idProvider;

    public DockerHostToolService(
        ContainerToolOptions? options = null,
        IDockerCommandRunner? runner = null,
        Func<Guid>? idProvider = null)
    {
        this.options = options ?? new ContainerToolOptions();
        var errors = this.options.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Container tool options are invalid: " + string.Join(" ", errors));
        }

        this.runner = runner ?? new DockerCliCommandRunner();
        this.idProvider = idProvider ?? Guid.NewGuid;
    }

    public async Task<ToolResult> RunContainerAsync(
        ToolCall toolCall,
        ToolExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var workspace = context?.Workspace;
        if (workspace is null)
        {
            return RefusalResult(toolCall.Id, "workspace-required", "container.run requires an active workspace.");
        }

        var request = ParseRequest(toolCall.Arguments);
        if (request.Refusal is not null)
        {
            return RefusalResult(toolCall.Id, request.Refusal.Code, request.Refusal.Reason);
        }

        var admitted = request.Value!;
        var registry = GetRegistry(admitted.Image);
        if (!RegistryIsAllowed(registry))
        {
            return RefusalResult(
                toolCall.Id,
                "image-registry-denied",
                $"Container image '{admitted.Image}' resolves to registry '{registry}', which is not allowed.");
        }

        var commandRefusal = ValidateCommandPolicy(admitted);
        if (commandRefusal is not null)
        {
            return RefusalResult(toolCall.Id, commandRefusal.Code, commandRefusal.Reason);
        }

        string resolvedWorkingDirectory;
        try
        {
            resolvedWorkingDirectory = PathConfinement.Resolve(workspace.RootPath, admitted.WorkingDirectory);
        }
        catch (PathConfinementException ex)
        {
            return RefusalResult(toolCall.Id, "working-directory-confined", ex.Message, admitted.WorkingDirectory);
        }

        var containerName = BuildContainerName(workspace.CorrelationId, toolCall.Id);
        var dockerArguments = BuildDockerRunArguments(
            workspace,
            resolvedWorkingDirectory,
            containerName,
            admitted);

        var timeout = TimeSpan.FromSeconds(Math.Min(admitted.TimeoutSeconds ?? options.CommandTimeoutSeconds, options.CommandTimeoutSeconds));
        var result = await runner.RunAsync(
            dockerArguments,
            timeout,
            options.StdoutMaxBytes,
            options.StderrMaxBytes,
            cancellationToken);

        var payload = new JsonObject
        {
            ["ok"] = result.ExitCode == 0 && !result.TimedOut,
            ["image"] = admitted.Image,
            ["command"] = admitted.Command,
            ["args"] = new JsonArray(admitted.Args.Select(static arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
            ["workingDirectory"] = MakeWorkspaceRelativePath(workspace.RootPath, resolvedWorkingDirectory),
            ["containerName"] = containerName,
            ["exitCode"] = result.ExitCode,
            ["stdout"] = result.StandardOutput,
            ["stderr"] = result.StandardError,
            ["stdoutTruncated"] = result.StandardOutputTruncated,
            ["stderrTruncated"] = result.StandardErrorTruncated,
            ["outputTruncated"] = result.OutputTruncated,
            ["timedOut"] = result.TimedOut
        };

        return new ToolResult(toolCall.Id, payload.ToJsonString(), IsError: result.ExitCode != 0 || result.TimedOut);
    }

    private IReadOnlyList<string> BuildDockerRunArguments(
        ToolWorkspaceContext workspace,
        string resolvedWorkingDirectory,
        string containerName,
        ContainerRunRequest request)
    {
        var containerWorkingDirectory = ToContainerWorkingDirectory(workspace.RootPath, resolvedWorkingDirectory);
        var arguments = new List<string>
        {
            "run",
            "--rm",
            "--name",
            containerName,
            "--label",
            "codeflow.managed=true",
            "--label",
            $"codeflow.workflow={workspace.CorrelationId:N}",
            "--workdir",
            containerWorkingDirectory,
            "--cpus",
            options.CpuCount.ToString(CultureInfo.InvariantCulture),
            "--memory",
            options.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            "--pids-limit",
            options.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--network",
            options.AllowNetwork ? "bridge" : "none",
            "--mount",
            $"type=bind,source={workspace.RootPath},target={options.WorkspaceMountPath},readonly=false",
            request.Image,
            request.Command
        };

        arguments.AddRange(request.Args);
        return arguments;
    }

    private static ParsedRequest ParseRequest(JsonNode? arguments)
    {
        if (arguments is not JsonObject obj)
        {
            return ParsedRequest.Denied("arguments-required", "container.run requires an object argument payload.");
        }

        var image = GetRequiredString(obj, "image");
        if (string.IsNullOrWhiteSpace(image))
        {
            return ParsedRequest.Denied("image-required", "container.run requires a non-empty image.");
        }

        if (ContainsWhitespace(image) || image.StartsWith("-", StringComparison.Ordinal) || image.Contains("://", StringComparison.Ordinal))
        {
            return ParsedRequest.Denied("image-invalid", "Container image must be a Docker image reference, not a command or URL.");
        }

        var command = GetRequiredString(obj, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return ParsedRequest.Denied("command-required", "container.run requires a non-empty command.");
        }

        var args = GetOptionalStringArray(obj, "args");
        var workingDirectory = GetOptionalString(obj, "workingDirectory");
        var timeoutSeconds = GetOptionalPositiveInt(obj, "timeoutSeconds");

        return ParsedRequest.Accepted(new ContainerRunRequest(
            image.Trim(),
            command.Trim(),
            args,
            string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory.Trim(),
            timeoutSeconds));
    }

    private ContainerPolicyRefusal? ValidateCommandPolicy(ContainerRunRequest request)
    {
        var commandName = Path.GetFileName(request.Command.Trim());
        if (string.Equals(commandName, "docker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "docker-compose", StringComparison.OrdinalIgnoreCase))
        {
            return new ContainerPolicyRefusal(
                "docker-command-denied",
                "container.run may not invoke Docker or Docker Compose inside the build/test container.");
        }

        var combined = string.Join(" ", new[] { request.Command }.Concat(request.Args));
        if (ContainsForbiddenDockerBuildText(combined))
        {
            return new ContainerPolicyRefusal(
                "docker-build-denied",
                "container.run may not build repository Dockerfiles or invoke Docker Compose.");
        }

        return null;
    }

    private static bool ContainsForbiddenDockerBuildText(string text) =>
        text.Contains("docker build", StringComparison.OrdinalIgnoreCase)
        || text.Contains("docker compose", StringComparison.OrdinalIgnoreCase)
        || text.Contains("docker-compose", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Dockerfile", StringComparison.OrdinalIgnoreCase);

    private bool RegistryIsAllowed(string registry) =>
        options.AllowedImageRegistries.Any(allowed =>
            string.Equals(NormalizeRegistry(allowed), registry, StringComparison.OrdinalIgnoreCase));

    private static string GetRegistry(string image)
    {
        var firstSlash = image.IndexOf('/');
        if (firstSlash < 0)
        {
            return "docker.io";
        }

        var firstComponent = image[..firstSlash];
        if (firstComponent.Contains('.') || firstComponent.Contains(':') || string.Equals(firstComponent, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeRegistry(firstComponent);
        }

        return "docker.io";
    }

    private static string NormalizeRegistry(string registry) =>
        registry.Trim().ToLowerInvariant();

    private string ToContainerWorkingDirectory(string workspaceRoot, string resolvedWorkingDirectory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), resolvedWorkingDirectory);
        if (relative == ".")
        {
            return options.WorkspaceMountPath;
        }

        var normalized = relative
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return options.WorkspaceMountPath.TrimEnd('/') + "/" + normalized;
    }

    private static string MakeWorkspaceRelativePath(string workspaceRoot, string resolvedWorkingDirectory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), resolvedWorkingDirectory);
        return relative == "." ? "." : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private string BuildContainerName(Guid correlationId, string callId)
    {
        var suffix = idProvider().ToString("N")[..12];
        var safeCallId = new string(callId
            .Where(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            .Take(24)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeCallId))
        {
            safeCallId = "call";
        }

        return $"codeflow-{correlationId:N}-{safeCallId}-{suffix}";
    }

    private static ToolResult RefusalResult(string callId, string code, string reason, string? path = null)
    {
        var refusal = new JsonObject
        {
            ["code"] = code,
            ["reason"] = reason,
            ["axis"] = "container-policy"
        };
        if (path is not null)
        {
            refusal["path"] = path;
        }

        return new ToolResult(
            callId,
            new JsonObject
            {
                ["ok"] = false,
                ["refusal"] = refusal
            }.ToJsonString(),
            IsError: true);
    }

    private static string? GetRequiredString(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }

    private static string? GetOptionalString(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is null)
        {
            return null;
        }

        return GetRequiredString(obj, propertyName);
    }

    private static IReadOnlyList<string> GetOptionalStringArray(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(static node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .Where(static text => text is not null)
            .Select(static text => text!)
            .ToArray();
    }

    private static int? GetOptionalPositiveInt(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is not JsonValue value || !value.TryGetValue<int>(out var number))
        {
            return null;
        }

        return number > 0 ? number : null;
    }

    private static bool ContainsWhitespace(string value) =>
        value.Any(char.IsWhiteSpace);

    private sealed record ContainerRunRequest(
        string Image,
        string Command,
        IReadOnlyList<string> Args,
        string WorkingDirectory,
        int? TimeoutSeconds);

    private sealed record ContainerPolicyRefusal(string Code, string Reason);

    private sealed record ParsedRequest(ContainerRunRequest? Value, ContainerPolicyRefusal? Refusal)
    {
        public static ParsedRequest Accepted(ContainerRunRequest value) => new(value, null);

        public static ParsedRequest Denied(string code, string reason) => new(null, new ContainerPolicyRefusal(code, reason));
    }
}
