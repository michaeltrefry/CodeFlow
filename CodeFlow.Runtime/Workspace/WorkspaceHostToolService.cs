using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Workspace;

public sealed class WorkspaceHostToolService
{
    private readonly WorkspaceOptions options;

    public WorkspaceHostToolService(WorkspaceOptions? options = null)
    {
        this.options = options ?? new WorkspaceOptions();
    }

    public Task<ToolResult> ReadFileAsync(
        ToolCall toolCall,
        ToolExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var workspace = RequireWorkspace(context);
        var relativePath = GetRequiredString(toolCall.Arguments, "path");
        var resolvedPath = PathConfinement.Resolve(workspace.RootPath, relativePath);

        if (!File.Exists(resolvedPath))
        {
            throw new InvalidOperationException($"File '{relativePath}' does not exist in the active workspace.");
        }

        var bytes = ReadFileBounded(resolvedPath, options.ReadMaxBytes, cancellationToken);
        var content = Encoding.UTF8.GetString(bytes.Buffer, 0, bytes.Length);

        return Task.FromResult(new ToolResult(
            toolCall.Id,
            new JsonObject
            {
                ["path"] = relativePath,
                ["content"] = content,
                ["truncated"] = bytes.Truncated
            }.ToJsonString()));
    }

    public async Task<ToolResult> ApplyPatchAsync(
        ToolCall toolCall,
        ToolExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var workspace = RequireWorkspace(context);
        var patchText = GetRequiredString(toolCall.Arguments, "patch");
        var commands = WorkspacePatchDocument.Parse(patchText);
        var changedFiles = new List<string>();

        try
        {
            foreach (var command in commands.Commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (command)
                {
                    case AddFilePatchCommand add:
                        changedFiles.Add(ApplyAdd(workspace.RootPath, add, options));
                        break;
                    case DeleteFilePatchCommand delete:
                        changedFiles.Add(ApplyDelete(workspace.RootPath, delete, options));
                        break;
                    case UpdateFilePatchCommand update:
                        changedFiles.AddRange(ApplyUpdate(workspace.RootPath, update, options));
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported patch command '{command.GetType().Name}'.");
                }
            }
        }
        catch (WorkspaceMutationRefusal refusal)
        {
            return RefusalResult(toolCall.Id, refusal);
        }
        catch (PathConfinementException ex)
        {
            return RefusalResult(toolCall.Id, new WorkspaceMutationRefusal(
                code: "path-confinement",
                reason: ex.Message,
                path: null));
        }

        await Task.CompletedTask;

        return new ToolResult(
            toolCall.Id,
            new JsonObject
            {
                ["ok"] = true,
                ["changedFiles"] = new JsonArray(changedFiles
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(static path => (JsonNode?)JsonValue.Create(path))
                    .ToArray())
            }.ToJsonString());
    }

    public async Task<ToolResult> RunCommandAsync(
        ToolCall toolCall,
        ToolExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var workspace = RequireWorkspace(context);
        var command = GetRequiredString(toolCall.Arguments, "command");
        var args = GetOptionalStringArray(toolCall.Arguments, "args");

        if (!CommandIsAllowed(command, options.CommandAllowlist))
        {
            var allowedNames = options.CommandAllowlist is null
                ? Array.Empty<string>()
                : options.CommandAllowlist.Where(static name => !string.IsNullOrWhiteSpace(name)).ToArray();
            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["ok"] = false,
                    ["refusal"] = new JsonObject
                    {
                        ["code"] = "command-allowlist",
                        ["reason"] = $"Command '{command}' is not in the workspace command allowlist.",
                        ["axis"] = "command-allowlist",
                        ["command"] = command,
                        ["allowed"] = new JsonArray(allowedNames
                            .Select(static name => (JsonNode?)JsonValue.Create(name))
                            .ToArray())
                    }
                }.ToJsonString(),
                IsError: true);
        }

        var workingDirectory = GetOptionalString(toolCall.Arguments, "workingDirectory");
        var timeoutSeconds = GetOptionalPositiveInt(toolCall.Arguments, "timeoutSeconds");
        var effectiveTimeout = TimeSpan.FromSeconds(Math.Min(
            timeoutSeconds ?? options.ExecTimeoutSeconds,
            options.ExecTimeoutSeconds));
        var resolvedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? workspace.RootPath
            : PathConfinement.Resolve(workspace.RootPath, workingDirectory);

        var result = await RunProcessAsync(
            command,
            args,
            resolvedWorkingDirectory,
            effectiveTimeout,
            cancellationToken);

        var payload = new JsonObject
        {
            ["command"] = command,
            ["args"] = new JsonArray(args.Select(static arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
            ["workingDirectory"] = MakeWorkspaceRelativePath(workspace.RootPath, resolvedWorkingDirectory),
            ["exitCode"] = result.ExitCode,
            ["stdout"] = result.StandardOutput,
            ["stderr"] = result.StandardError,
            ["outputTruncated"] = result.OutputTruncated,
            ["timedOut"] = result.TimedOut
        };

        return new ToolResult(
            toolCall.Id,
            payload.ToJsonString(),
            IsError: result.ExitCode != 0 || result.TimedOut);
    }

    private static string ApplyAdd(string workspaceRoot, AddFilePatchCommand command, WorkspaceOptions options)
    {
        var resolvedPath = PathConfinement.Resolve(workspaceRoot, command.Path);
        RefuseIfSymlinkChain(workspaceRoot, command.Path, options);

        if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
        {
            throw new WorkspaceMutationRefusal(
                code: "destination-exists",
                reason: $"Cannot add '{command.Path}' because it already exists.",
                path: command.Path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);
        WriteAllText(resolvedPath, command.Lines, Environment.NewLine);
        return command.Path;
    }

    private static string ApplyDelete(string workspaceRoot, DeleteFilePatchCommand command, WorkspaceOptions options)
    {
        var resolvedPath = PathConfinement.Resolve(workspaceRoot, command.Path);
        RefuseIfSymlinkChain(workspaceRoot, command.Path, options);

        if (!File.Exists(resolvedPath))
        {
            throw new WorkspaceMutationRefusal(
                code: "source-missing",
                reason: $"Cannot delete '{command.Path}' because it does not exist.",
                path: command.Path);
        }

        VerifyPreimage(resolvedPath, command.Path, command.PreimageSha256);

        File.Delete(resolvedPath);
        return command.Path;
    }

    private static IReadOnlyList<string> ApplyUpdate(string workspaceRoot, UpdateFilePatchCommand command, WorkspaceOptions options)
    {
        var sourcePath = PathConfinement.Resolve(workspaceRoot, command.Path);
        RefuseIfSymlinkChain(workspaceRoot, command.Path, options);

        if (!File.Exists(sourcePath))
        {
            throw new WorkspaceMutationRefusal(
                code: "source-missing",
                reason: $"Cannot update '{command.Path}' because it does not exist.",
                path: command.Path);
        }

        VerifyPreimage(sourcePath, command.Path, command.PreimageSha256);

        var destinationRelativePath = command.MoveToPath ?? command.Path;
        var destinationPath = PathConfinement.Resolve(workspaceRoot, destinationRelativePath);
        if (!string.Equals(destinationRelativePath, command.Path, StringComparison.Ordinal))
        {
            RefuseIfSymlinkChain(workspaceRoot, destinationRelativePath, options);
        }

        var originalText = File.ReadAllText(sourcePath);
        var newline = DetectNewline(originalText);
        var hadTrailingNewline = HasTrailingNewline(originalText);
        var originalLines = SplitLines(originalText);
        var updatedLines = ApplyLineChanges(originalLines, command.Path, command.ChangeLines);
        var moving = !string.Equals(sourcePath, destinationPath, StringComparison.Ordinal);

        if (moving)
        {
            if (File.Exists(destinationPath))
            {
                throw new WorkspaceMutationRefusal(
                    code: "destination-exists",
                    reason: $"Cannot move '{command.Path}' to '{destinationRelativePath}' because the destination already exists.",
                    path: destinationRelativePath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        }

        WriteAllText(destinationPath, updatedLines, newline, hadTrailingNewline);
        if (moving)
        {
            File.Delete(sourcePath);
        }

        return string.Equals(command.Path, destinationRelativePath, StringComparison.Ordinal)
            ? [destinationRelativePath]
            : [command.Path, destinationRelativePath];
    }

    private static void RefuseIfSymlinkChain(string workspaceRoot, string relativePath, WorkspaceOptions options)
    {
        if (options.SymlinkPolicy == WorkspaceSymlinkPolicy.AllowAll)
        {
            return;
        }

        if (PathConfinement.ContainsSymlink(workspaceRoot, relativePath))
        {
            throw new WorkspaceMutationRefusal(
                code: "symlink-refused",
                reason: $"Path '{relativePath}' resolves through a symlink; symlink mutation is refused by workspace policy.",
                path: relativePath);
        }
    }

    private static void VerifyPreimage(string filePath, string declaredRelativePath, string? expectedSha256)
    {
        if (expectedSha256 is null)
        {
            return;
        }

        var expected = expectedSha256.Trim().ToLowerInvariant();
        if (!IsHexSha256(expected))
        {
            throw new WorkspaceMutationRefusal(
                code: "preimage-malformed",
                reason: $"Preimage SHA-256 for '{declaredRelativePath}' must be a 64-character hex string.",
                path: declaredRelativePath);
        }

        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        var actual = Convert.ToHexString(hashBytes).ToLowerInvariant();

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new WorkspaceMutationRefusal(
                code: "preimage-mismatch",
                reason: $"Preimage SHA-256 for '{declaredRelativePath}' does not match the file on disk.",
                path: declaredRelativePath,
                detail: new JsonObject
                {
                    ["expected"] = expected,
                    ["actual"] = actual
                });
        }
    }

    private static bool CommandIsAllowed(string command, IList<string>? allowlist)
    {
        if (allowlist is null)
        {
            return true;
        }

        var trimmed = allowlist.Where(static name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (trimmed.Length == 0)
        {
            return true;
        }

        var requested = NormalizeCommandForMatch(command);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var allowed in trimmed)
        {
            if (string.Equals(NormalizeCommandForMatch(allowed), requested, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeCommandForMatch(string command)
    {
        var trimmed = command.Trim();
        var basename = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(basename))
        {
            return trimmed;
        }

        if (OperatingSystem.IsWindows())
        {
            var ext = Path.GetExtension(basename);
            if (!string.IsNullOrEmpty(ext))
            {
                return basename[..^ext.Length];
            }
        }

        return basename;
    }

    private static bool IsHexSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var isHex = ch is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static ToolResult RefusalResult(string callId, WorkspaceMutationRefusal refusal)
    {
        var refusalJson = new JsonObject
        {
            ["code"] = refusal.Code,
            ["reason"] = refusal.Reason,
            ["axis"] = "workspace-mutation"
        };
        if (refusal.Path is not null)
        {
            refusalJson["path"] = refusal.Path;
        }
        if (refusal.Detail is not null)
        {
            refusalJson["detail"] = refusal.Detail.DeepClone();
        }

        return new ToolResult(
            callId,
            new JsonObject
            {
                ["ok"] = false,
                ["refusal"] = refusalJson
            }.ToJsonString(),
            IsError: true);
    }

    private static IReadOnlyList<string> ApplyLineChanges(
        IReadOnlyList<string> originalLines,
        string filePath,
        IReadOnlyList<string> changeLines)
    {
        if (changeLines.Count == 0)
        {
            return originalLines.ToArray();
        }

        var result = new List<string>();
        var sourceIndex = 0;

        foreach (var line in changeLines)
        {
            if (line.Length == 0)
            {
                throw new InvalidOperationException($"Patch for '{filePath}' contains an empty change line.");
            }

            var marker = line[0];
            var content = line[1..];

            switch (marker)
            {
                case ' ':
                    ExpectSourceLine(originalLines, sourceIndex, content, filePath);
                    result.Add(content);
                    sourceIndex += 1;
                    break;
                case '-':
                    ExpectSourceLine(originalLines, sourceIndex, content, filePath);
                    sourceIndex += 1;
                    break;
                case '+':
                    result.Add(content);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Patch for '{filePath}' contains unsupported change line prefix '{marker}'.");
            }
        }

        for (var i = sourceIndex; i < originalLines.Count; i++)
        {
            result.Add(originalLines[i]);
        }

        return result;
    }

    private static void ExpectSourceLine(
        IReadOnlyList<string> sourceLines,
        int index,
        string expected,
        string filePath)
    {
        if (index >= sourceLines.Count)
        {
            throw new WorkspaceMutationRefusal(
                code: "context-mismatch",
                reason: $"Patch for '{filePath}' references line '{expected}' past the end of the file.",
                path: filePath);
        }

        if (!string.Equals(sourceLines[index], expected, StringComparison.Ordinal))
        {
            throw new WorkspaceMutationRefusal(
                code: "context-mismatch",
                reason: $"Patch context mismatch for '{filePath}'. Expected '{expected}' but found '{sourceLines[index]}'.",
                path: filePath);
        }
    }

    private async Task<ProcessExecutionResult> RunProcessAsync(
        string command,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new BoundedOutputBuffer(options.ExecOutputMaxBytes);
        var stderr = new BoundedOutputBuffer(options.ExecOutputMaxBytes);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to start command '{command}'. Ensure it is installed and available on PATH.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessExecutionResult(
                ExitCode: -1,
                StandardOutput: stdout.ToString(),
                StandardError: stderr.ToString(),
                OutputTruncated: stdout.Truncated || stderr.Truncated,
                TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await process.WaitForExitAsync(CancellationToken.None);

        return new ProcessExecutionResult(
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            stdout.Truncated || stderr.Truncated,
            TimedOut: false);
    }

    private static (byte[] Buffer, int Length, bool Truncated) ReadFileBounded(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var effectiveMax = checked((int)Math.Min(maxBytes, int.MaxValue - 1));
        var buffer = new byte[effectiveMax + 1];

        using var stream = File.OpenRead(path);
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        var truncated = totalRead > effectiveMax;
        return (buffer, Math.Min(totalRead, effectiveMax), truncated);
    }

    private static ToolWorkspaceContext RequireWorkspace(ToolExecutionContext? context)
    {
        if (context?.Workspace is null)
        {
            throw new InvalidOperationException(
                "This tool requires an active workspace context for the current invocation.");
        }

        return context.Workspace;
    }

    private static string GetRequiredString(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new InvalidOperationException($"The '{propertyName}' argument is required.");
    }

    private static string? GetOptionalString(JsonNode? node, string propertyName)
    {
        return node?[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;
    }

    private static IReadOnlyList<string> GetOptionalStringArray(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        return array
            .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .ToArray();
    }

    private static int? GetOptionalPositiveInt(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is JsonValue value && value.TryGetValue<int>(out var result))
        {
            if (result <= 0)
            {
                throw new InvalidOperationException($"The '{propertyName}' argument must be greater than zero.");
            }

            return result;
        }

        return null;
    }

    private static string DetectNewline(string content)
    {
        if (content.Contains("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        return "\n";
    }

    private static IReadOnlyList<string> SplitLines(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n').ToList();
        if (normalized.EndsWith('\n'))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static void WriteAllText(
        string path,
        IReadOnlyList<string> lines,
        string newline,
        bool trailingNewline = true)
    {
        var content = lines.Count == 0
            ? string.Empty
            : string.Join(newline, lines) + (trailingNewline ? newline : string.Empty);
        File.WriteAllText(path, content);
    }

    private static bool HasTrailingNewline(string content)
    {
        return content.EndsWith('\n');
    }

    private static string MakeWorkspaceRelativePath(string workspaceRoot, string path)
    {
        return Path.GetRelativePath(workspaceRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed class BoundedOutputBuffer(long maxBytes)
    {
        private readonly StringBuilder builder = new();
        private readonly long maxBytes = maxBytes;
        private long bytesWritten;

        public bool Truncated { get; private set; }

        public void AppendLine(string line)
        {
            Append(line + Environment.NewLine);
        }

        public void Append(string value)
        {
            if (Truncated || string.IsNullOrEmpty(value))
            {
                return;
            }

            var remaining = maxBytes - bytesWritten;
            if (remaining <= 0)
            {
                Truncated = true;
                return;
            }

            var encoded = Encoding.UTF8.GetBytes(value);
            if (encoded.LongLength <= remaining)
            {
                builder.Append(value);
                bytesWritten += encoded.LongLength;
                return;
            }

            var prefix = Encoding.UTF8.GetString(encoded, 0, (int)Math.Max(0, remaining));
            builder.Append(prefix);
            bytesWritten = maxBytes;
            Truncated = true;
        }

        public override string ToString() => builder.ToString();
    }

    private sealed record ProcessExecutionResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool OutputTruncated,
        bool TimedOut);
}

internal sealed class WorkspacePatchDocument
{
    private WorkspacePatchDocument(IReadOnlyList<WorkspacePatchCommand> commands)
    {
        Commands = commands;
    }

    public IReadOnlyList<WorkspacePatchCommand> Commands { get; }

    public static WorkspacePatchDocument Parse(string patchText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchText);

        var normalized = patchText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var index = 0;

        if (lines.Length == 0 || !string.Equals(lines[index], "*** Begin Patch", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Patch text must start with '*** Begin Patch'.");
        }

        index += 1;
        var commands = new List<WorkspacePatchCommand>();

        while (index < lines.Length)
        {
            var line = lines[index];

            if (string.Equals(line, "*** End Patch", StringComparison.Ordinal))
            {
                return new WorkspacePatchDocument(commands);
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                commands.Add(ParseAdd(lines, ref index));
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                commands.Add(ParseDelete(lines, ref index));
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                commands.Add(ParseUpdate(lines, ref index));
                continue;
            }

            throw new InvalidOperationException($"Unexpected patch line '{line}'.");
        }

        throw new InvalidOperationException("Patch text must end with '*** End Patch'.");
    }

    private static AddFilePatchCommand ParseAdd(string[] lines, ref int index)
    {
        var path = lines[index]["*** Add File: ".Length..];
        index += 1;

        var content = new List<string>();
        while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
        {
            if (!lines[index].StartsWith('+'))
            {
                throw new InvalidOperationException(
                    $"Add-file patch for '{path}' must contain only '+' lines.");
            }

            content.Add(lines[index][1..]);
            index += 1;
        }

        return new AddFilePatchCommand(path, content);
    }

    private static DeleteFilePatchCommand ParseDelete(string[] lines, ref int index)
    {
        var path = lines[index]["*** Delete File: ".Length..];
        index += 1;

        var preimage = TryReadPreimageLine(lines, ref index);
        return new DeleteFilePatchCommand(path, preimage);
    }

    private static UpdateFilePatchCommand ParseUpdate(string[] lines, ref int index)
    {
        var path = lines[index]["*** Update File: ".Length..];
        index += 1;

        string? moveTo = null;
        if (index < lines.Length && lines[index].StartsWith("*** Move to: ", StringComparison.Ordinal))
        {
            moveTo = lines[index]["*** Move to: ".Length..];
            index += 1;
        }

        var preimage = TryReadPreimageLine(lines, ref index);

        var changeLines = new List<string>();
        while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
        {
            var line = lines[index];
            if (line.StartsWith("@@", StringComparison.Ordinal) || string.Equals(line, "*** End of File", StringComparison.Ordinal))
            {
                index += 1;
                continue;
            }

            if (line.Length == 0 || " +-".IndexOf(line[0]) < 0)
            {
                throw new InvalidOperationException(
                    $"Update patch for '{path}' contains invalid line '{line}'.");
            }

            changeLines.Add(line);
            index += 1;
        }

        return new UpdateFilePatchCommand(path, moveTo, changeLines, preimage);
    }

    private static string? TryReadPreimageLine(string[] lines, ref int index)
    {
        const string Header = "*** Preimage SHA-256: ";
        if (index >= lines.Length || !lines[index].StartsWith(Header, StringComparison.Ordinal))
        {
            return null;
        }

        var value = lines[index][Header.Length..].Trim();
        index += 1;
        return value;
    }
}

internal abstract record WorkspacePatchCommand;

internal sealed record AddFilePatchCommand(string Path, IReadOnlyList<string> Lines) : WorkspacePatchCommand;

internal sealed record DeleteFilePatchCommand(string Path, string? PreimageSha256) : WorkspacePatchCommand;

internal sealed record UpdateFilePatchCommand(
    string Path,
    string? MoveToPath,
    IReadOnlyList<string> ChangeLines,
    string? PreimageSha256) : WorkspacePatchCommand;

internal sealed class WorkspaceMutationRefusal : Exception
{
    public WorkspaceMutationRefusal(string code, string reason, string? path = null, JsonObject? detail = null)
        : base(reason)
    {
        Code = code;
        Reason = reason;
        Path = path;
        Detail = detail;
    }

    public string Code { get; }
    public string Reason { get; }
    public string? Path { get; }
    public JsonObject? Detail { get; }
}
