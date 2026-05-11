using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Authority.Admission;

namespace CodeFlow.Runtime.Workspace;

public sealed class WorkspaceHostToolService
{
    private readonly WorkspaceOptions options;
    private readonly WorkspacePatchValidator patchValidator;

    public WorkspaceHostToolService(
        WorkspaceOptions? options = null,
        WorkspacePatchValidator? patchValidator = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.options = options ?? new WorkspaceOptions();
        this.patchValidator = patchValidator ?? new WorkspacePatchValidator(this.options, nowProvider);
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

        // sc-272 PR1: validate up-front. The validator catches malformed patches, paths
        // that escape the workspace root, and symlink-policy violations before any
        // filesystem mutation runs — so a single bad path can't leave a partially-applied
        // patch on disk. Filesystem-state-dependent checks (preimage, destination-exists)
        // stay in the apply loop where they belong.
        var admission = patchValidator.Validate(new WorkspacePatchAdmissionRequest(
            WorkspaceCorrelationId: workspace.CorrelationId,
            WorkspaceRootPath: workspace.RootPath,
            PatchText: patchText));

        if (admission is Rejected<AdmittedWorkspacePatch> rejected)
        {
            return RejectionResult(toolCall.Id, rejected.Reason);
        }

        var admitted = ((Accepted<AdmittedWorkspacePatch>)admission).Value;
        return await ApplyAdmittedPatchAsync(toolCall.Id, admitted, cancellationToken);
    }

    private async Task<ToolResult> ApplyAdmittedPatchAsync(
        string callId,
        AdmittedWorkspacePatch admitted,
        CancellationToken cancellationToken)
    {
        var changedFiles = new List<string>();

        try
        {
            foreach (var command in admitted.Document.Commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (command)
                {
                    case AddFilePatchCommand add:
                        changedFiles.Add(ApplyAdd(admitted.WorkspaceRootPath, add, options));
                        break;
                    case DeleteFilePatchCommand delete:
                        changedFiles.Add(ApplyDelete(admitted.WorkspaceRootPath, delete, options));
                        break;
                    case UpdateFilePatchCommand update:
                        changedFiles.AddRange(ApplyUpdate(admitted.WorkspaceRootPath, update, options));
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported patch command '{command.GetType().Name}'.");
                }
            }
        }
        catch (WorkspaceMutationRefusal refusal)
        {
            return RefusalResult(callId, refusal);
        }
        catch (PathConfinementException ex)
        {
            // Defence-in-depth: validator already path-confined every command, but a TOCTOU
            // symlink swap between admission and execution could still surface here.
            return RefusalResult(callId, new WorkspaceMutationRefusal(
                code: "path-confinement",
                reason: ex.Message,
                path: null));
        }

        await Task.CompletedTask;

        return new ToolResult(
            callId,
            new JsonObject
            {
                ["ok"] = true,
                ["changedFiles"] = new JsonArray(changedFiles
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(static path => (JsonNode?)JsonValue.Create(path))
                    .ToArray())
            }.ToJsonString());
    }

    public async Task<ToolResult> BulkReplaceAsync(
        ToolCall toolCall,
        ToolExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var workspace = RequireWorkspace(context);
        var pattern = GetRequiredString(toolCall.Arguments, "pattern");
        var replacement = GetOptionalRawString(toolCall.Arguments, "replacement") ?? string.Empty;
        var explicitPaths = GetOptionalStringArray(toolCall.Arguments, "paths");
        var pathGlob = GetOptionalString(toolCall.Arguments, "pathGlob");
        var useRegex = GetOptionalBool(toolCall.Arguments, "regex") ?? false;
        var dryRun = GetOptionalBool(toolCall.Arguments, "dryRun") ?? false;

        if (explicitPaths.Count == 0 && string.IsNullOrWhiteSpace(pathGlob))
        {
            return RefusalResult(toolCall.Id, new WorkspaceMutationRefusal(
                code: "scope-required",
                reason: "Provide at least one of 'paths' or 'pathGlob' to scope the replacement; "
                    + "workspace-wide unscoped scans are not allowed.",
                path: null));
        }

        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                regex = new Regex(
                    pattern,
                    RegexOptions.NonBacktracking | RegexOptions.Multiline,
                    options.BulkReplaceRegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return RefusalResult(toolCall.Id, new WorkspaceMutationRefusal(
                    code: "pattern-invalid",
                    reason: $"Regex pattern did not compile: {ex.Message}",
                    path: null));
            }
        }

        Regex? globRegex = null;
        if (!string.IsNullOrWhiteSpace(pathGlob))
        {
            globRegex = TryCompileGlob(pathGlob);
            if (globRegex is null)
            {
                return RefusalResult(toolCall.Id, new WorkspaceMutationRefusal(
                    code: "pattern-invalid",
                    reason: $"Glob '{pathGlob}' is not a supported pattern. Use *, **, ?, or literal segments.",
                    path: null));
            }
        }

        var enumeration = EnumerateBulkReplaceCandidates(
            workspace.RootPath,
            explicitPaths,
            globRegex,
            options.BulkReplaceMaxFiles,
            cancellationToken);

        if (enumeration.Refusal is { } enumRefusal)
        {
            return RefusalResult(toolCall.Id, enumRefusal);
        }

        var changes = new List<(string Path, int Count)>();
        var skipped = new List<(string Path, string Reason)>();
        var totalReplacements = 0;

        foreach (var (relativePath, absolutePath) in enumeration.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.SymlinkPolicy != WorkspaceSymlinkPolicy.AllowAll
                && PathConfinement.ContainsSymlink(workspace.RootPath, relativePath))
            {
                skipped.Add((relativePath, "symlink"));
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(absolutePath);
            }
            catch
            {
                skipped.Add((relativePath, "stat_failed"));
                continue;
            }

            if (!info.Exists)
            {
                skipped.Add((relativePath, "missing"));
                continue;
            }

            if (info.Length > options.ReadMaxBytes)
            {
                skipped.Add((relativePath, "file_too_large"));
                continue;
            }

            byte[] originalBytes;
            try
            {
                originalBytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken);
            }
            catch (IOException)
            {
                skipped.Add((relativePath, "read_failed"));
                continue;
            }

            if (LooksLikeBinary(originalBytes))
            {
                skipped.Add((relativePath, "binary"));
                continue;
            }

            string originalContent;
            try
            {
                originalContent = Encoding.UTF8.GetString(originalBytes);
            }
            catch
            {
                skipped.Add((relativePath, "decode_failed"));
                continue;
            }

            int replacementCount;
            string newContent;
            try
            {
                if (regex is not null)
                {
                    var localCount = 0;
                    newContent = regex.Replace(
                        originalContent,
                        match =>
                        {
                            localCount += 1;
                            return match.Result(replacement);
                        });
                    replacementCount = localCount;
                }
                else
                {
                    newContent = ReplaceLiteralCounted(originalContent, pattern, replacement, out replacementCount);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                skipped.Add((relativePath, "regex_timeout"));
                continue;
            }

            if (replacementCount == 0)
            {
                continue;
            }

            if (!dryRun)
            {
                try
                {
                    await File.WriteAllBytesAsync(absolutePath, Encoding.UTF8.GetBytes(newContent), cancellationToken);
                }
                catch (IOException)
                {
                    skipped.Add((relativePath, "write_failed"));
                    continue;
                }
            }

            changes.Add((relativePath, replacementCount));
            totalReplacements += replacementCount;
        }

        var payload = new JsonObject
        {
            ["ok"] = true,
            ["dryRun"] = dryRun,
            ["filesScanned"] = enumeration.Files.Count,
            ["filesChanged"] = changes.Count,
            ["totalReplacements"] = totalReplacements,
            ["changes"] = new JsonArray(changes
                .Select(c => (JsonNode?)new JsonObject
                {
                    ["path"] = c.Path,
                    ["count"] = c.Count
                })
                .ToArray()),
            ["skipped"] = new JsonArray(skipped
                .Select(s => (JsonNode?)new JsonObject
                {
                    ["path"] = s.Path,
                    ["reason"] = s.Reason
                })
                .ToArray())
        };

        return new ToolResult(toolCall.Id, payload.ToJsonString());
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

        var allowlistDecision = ResolveCommandAllowlist(context?.Envelope, options.CommandAllowlist);
        if (!CommandIsAllowed(command, allowlistDecision.Allowlist, allowlistDecision.StrictEmpty))
        {
            var allowedNames = allowlistDecision.Allowlist is null
                ? Array.Empty<string>()
                : allowlistDecision.Allowlist
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .ToArray();
            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["ok"] = false,
                    ["refusal"] = new JsonObject
                    {
                        ["code"] = allowlistDecision.RefusalCode,
                        ["reason"] = allowlistDecision.RefusalReason(command),
                        ["axis"] = allowlistDecision.RefusalAxis,
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

        // sc-661: per-trace credential env (epic 658). Set on every spawned process so the
        // common case `run_command "git", [...]` picks up the store-helper credentials
        // automatically. Non-git commands silently inherit the env vars too — they're inert
        // anywhere else.
        var credentialEnv = GitCredentialEnv.Build(options.GitCredentialRoot, workspace.CorrelationId);

        var result = await RunProcessAsync(
            command,
            args,
            resolvedWorkingDirectory,
            effectiveTimeout,
            credentialEnv,
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

    private static bool CommandIsAllowed(string command, IReadOnlyList<string>? allowlist, bool strictEmpty)
    {
        if (allowlist is null)
        {
            // null = no opinion expressed → no enforcement (preserves the sc-270 default).
            return true;
        }

        var trimmed = allowlist.Where(static name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (trimmed.Length == 0)
        {
            // Static-config back-compat: an all-whitespace IList<string> in WorkspaceOptions has
            // always been treated as "no enforcement". For envelope-derived allowlists we honour
            // explicit empty ("intersection denied everything") as a deny-all.
            return !strictEmpty;
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

    /// <summary>
    /// sc-269 PR3: pick the active <c>run_command</c> allowlist + refusal taxonomy for the
    /// invocation. Priority order: envelope <c>ExecuteGrants</c> → envelope
    /// <c>Workspace.CommandAllowlist</c> → static <see cref="WorkspaceOptions.CommandAllowlist"/>.
    /// Each layer reports a distinct refusal code/axis so trace evidence shows where the deny
    /// originated; <c>StrictEmpty</c> distinguishes envelope intent (an explicit empty intersection
    /// denies everything) from static back-compat (an all-whitespace list in config means
    /// "no enforcement").
    /// </summary>
    private static CommandAllowlistDecision ResolveCommandAllowlist(
        WorkflowExecutionEnvelope? envelope,
        IList<string>? staticAllowlist)
    {
        if (envelope?.ExecuteGrants is { } grants)
        {
            return new CommandAllowlistDecision(
                Allowlist: grants.Select(g => g.Command).ToArray(),
                StrictEmpty: true,
                RefusalCode: "envelope-execute-grants",
                RefusalAxis: BlockedBy.Axes.ExecuteGrants,
                RefusalReason: command =>
                    $"Command '{command}' is not authorised by the run's ExecuteGrants envelope axis.");
        }

        if (envelope?.Workspace?.CommandAllowlist is { } envAllow)
        {
            return new CommandAllowlistDecision(
                Allowlist: envAllow,
                StrictEmpty: true,
                RefusalCode: "envelope-workspace-allowlist",
                RefusalAxis: BlockedBy.Axes.Workspace,
                RefusalReason: command =>
                    $"Command '{command}' is not authorised by the run's Workspace.CommandAllowlist envelope axis.");
        }

        return new CommandAllowlistDecision(
            Allowlist: staticAllowlist?.ToArray(),
            StrictEmpty: false,
            RefusalCode: "command-allowlist",
            RefusalAxis: "command-allowlist",
            RefusalReason: command =>
                $"Command '{command}' is not in the workspace command allowlist.");
    }

    private sealed record CommandAllowlistDecision(
        IReadOnlyList<string>? Allowlist,
        bool StrictEmpty,
        string RefusalCode,
        string RefusalAxis,
        Func<string, string> RefusalReason);

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

    /// <summary>
    /// sc-272 PR1: builds a tool-result refusal payload from a <see cref="Rejection"/>
    /// minted by an admission validator. Shape matches the workspace mutation refusal
    /// payload so <see cref="RefusalPayloadParser"/> picks it up as a Stage = Tool
    /// <see cref="RefusalEvent"/> on the existing <see cref="ToolRegistry"/> path.
    /// </summary>
    private static ToolResult RejectionResult(string callId, Rejection rejection)
    {
        var refusalJson = new JsonObject
        {
            ["code"] = rejection.Code,
            ["reason"] = rejection.Reason,
            ["axis"] = rejection.Axis
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
                reason: $"Patch for '{filePath}' references a line past the end of the file "
                    + $"(line {index + 1} requested, file has {sourceLines.Count} lines). "
                    + "Re-read the file with `read_file` and reconstruct the hunk against the "
                    + "current content — the file likely shrank since you read it, or a prior "
                    + "hunk in this same patch changed the line count.",
                path: filePath);
        }

        var actual = sourceLines[index];
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new WorkspaceMutationRefusal(
                code: "context-mismatch",
                reason: BuildContextMismatchMessage(filePath, index, expected, actual),
                path: filePath);
        }
    }

    /// <summary>
    /// Renders an exact byte-by-byte diff of the two strings so the model can spot
    /// invisible-whitespace causes (tab vs space, trailing spaces, paraphrased text). Common
    /// failure modes that look identical in normal printing but differ by one byte are the
    /// hardest for an agent to debug from a generic "expected X, got Y" message — show the
    /// whitespace explicitly + suggest the recovery path.
    /// </summary>
    private static string BuildContextMismatchMessage(string filePath, int index, string expected, string actual)
    {
        var renderedExpected = RenderLineForDiff(expected);
        var renderedActual = RenderLineForDiff(actual);
        var firstDiff = FirstDifferenceDescription(expected, actual);

        return $"Patch context mismatch for '{filePath}' at line {index + 1}. "
            + $"Expected (len {expected.Length}): {renderedExpected}. "
            + $"Actual (len {actual.Length}): {renderedActual}. "
            + (firstDiff is null ? "" : firstDiff + " ")
            + "Whitespace is rendered explicitly: · = space, → = tab, ␍ = CR. Common causes: "
            + "paraphrased context lines (the model wrote what it expected, not what's on disk); "
            + "trailing-whitespace drift; tab vs space confusion; or sequential hunks in the same "
            + "patch — a prior hunk modified the file and a later hunk's context now refers to "
            + "the pre-edit state. Re-read with `read_file` and reconstruct the hunk against "
            + "current content. apply_patch is strict by design — paraphrase, even one byte, fails.";
    }

    private static string RenderLineForDiff(string line)
    {
        var sb = new StringBuilder(line.Length + 4);
        sb.Append('\'');
        foreach (var ch in line)
        {
            switch (ch)
            {
                case ' ': sb.Append('·'); break;
                case '\t': sb.Append('→'); break;
                case '\r': sb.Append('␍'); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    private static string? FirstDifferenceDescription(string expected, string actual)
    {
        var bound = Math.Min(expected.Length, actual.Length);
        for (var i = 0; i < bound; i++)
        {
            if (expected[i] != actual[i])
            {
                return $"First difference at char {i + 1}: "
                    + $"expected {DescribeChar(expected[i])}, actual {DescribeChar(actual[i])}.";
            }
        }
        if (expected.Length != actual.Length)
        {
            var (longer, longerLen, shorterLen) = expected.Length > actual.Length
                ? ("expected", expected.Length, actual.Length)
                : ("actual", actual.Length, expected.Length);
            return $"Lines match for the first {shorterLen} chars; {longer} string is "
                + $"{longerLen - shorterLen} char(s) longer (often trailing whitespace).";
        }
        return null;
    }

    private static string DescribeChar(char ch)
    {
        return ch switch
        {
            ' ' => "space (0x20)",
            '\t' => "tab (0x09)",
            '\r' => "CR (0x0D)",
            _ when char.IsControl(ch) => $"control char (0x{(int)ch:X2})",
            _ => $"'{ch}' (0x{(int)ch:X2})",
        };
    }

    private async Task<ProcessExecutionResult> RunProcessAsync(
        string command,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environmentVariables,
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

        if (environmentVariables is { Count: > 0 })
        {
            foreach (var kv in environmentVariables)
            {
                startInfo.Environment[kv.Key] = kv.Value;
            }
        }

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

    private static string? GetOptionalRawString(JsonNode? node, string propertyName)
    {
        return node?[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            ? result
            : null;
    }

    private static bool? GetOptionalBool(JsonNode? node, string propertyName)
    {
        return node?[propertyName] is JsonValue value
            && value.TryGetValue<bool>(out var result)
            ? result
            : null;
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

    private sealed record ProcessExecutionResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool OutputTruncated,
        bool TimedOut);

    private static readonly HashSet<string> BulkReplaceExcludedDirs = new(StringComparer.Ordinal)
    {
        ".git",
        "node_modules",
        "bin",
        "obj",
        "target",
        "dist",
        "__pycache__",
        ".venv",
        "venv"
    };

    private static BulkReplaceEnumeration EnumerateBulkReplaceCandidates(
        string workspaceRoot,
        IReadOnlyList<string> explicitPaths,
        Regex? globRegex,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        if (explicitPaths.Count == 0)
        {
            roots.Add(workspaceRoot);
        }
        else
        {
            foreach (var declared in explicitPaths)
            {
                string resolved;
                try
                {
                    resolved = PathConfinement.Resolve(workspaceRoot, declared);
                }
                catch (PathConfinementException ex)
                {
                    return BulkReplaceEnumeration.OfRefusal(new WorkspaceMutationRefusal(
                        code: "path-confinement",
                        reason: ex.Message,
                        path: declared));
                }

                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    return BulkReplaceEnumeration.OfRefusal(new WorkspaceMutationRefusal(
                        code: "source-missing",
                        reason: $"Path '{declared}' does not exist in the active workspace.",
                        path: declared));
                }

                roots.Add(resolved);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<(string Relative, string Absolute)>();

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(root))
            {
                AddCandidate(workspaceRoot, root, globRegex, files, seen);
                if (files.Count > maxFiles)
                {
                    return BulkReplaceEnumeration.OfRefusal(TooManyFilesRefusal(maxFiles));
                }

                continue;
            }

            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in EnumerateFilesRespectingExclusions(root, cancellationToken))
            {
                AddCandidate(workspaceRoot, path, globRegex, files, seen);
                if (files.Count > maxFiles)
                {
                    return BulkReplaceEnumeration.OfRefusal(TooManyFilesRefusal(maxFiles));
                }
            }
        }

        return BulkReplaceEnumeration.OfFiles(files);
    }

    private static void AddCandidate(
        string workspaceRoot,
        string absolutePath,
        Regex? globRegex,
        List<(string Relative, string Absolute)> files,
        HashSet<string> seen)
    {
        if (!seen.Add(absolutePath))
        {
            return;
        }

        var relative = MakeWorkspaceRelativePath(workspaceRoot, absolutePath);
        if (globRegex is not null && !globRegex.IsMatch(relative))
        {
            return;
        }

        files.Add((relative, absolutePath));
    }

    private static IEnumerable<string> EnumerateFilesRespectingExclusions(
        string root,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            IEnumerable<string> subdirs;
            IEnumerable<string> entries;
            try
            {
                subdirs = Directory.EnumerateDirectories(current);
                entries = Directory.EnumerateFiles(current);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdir in subdirs)
            {
                var name = Path.GetFileName(subdir);
                if (BulkReplaceExcludedDirs.Contains(name))
                {
                    continue;
                }

                stack.Push(subdir);
            }

            foreach (var entry in entries)
            {
                yield return entry;
            }
        }
    }

    private static WorkspaceMutationRefusal TooManyFilesRefusal(int maxFiles) =>
        new(
            code: "too_many_files",
            reason: $"bulk_replace matched more than {maxFiles} files. The tool refused before "
                + "touching any of them, so nothing was written. Retry with a narrower scope: "
                + "use a more specific pathGlob (e.g. `**/*.cs` instead of `**/*`, or "
                + "`src/**/*.ts` instead of `**/*.ts`), pass explicit `paths` rooted at the "
                + "subtrees that actually need the rename, or split the rename across several "
                + "calls keyed by top-level directory. Same pattern, smaller scope each call.",
            path: null);

    private static bool LooksLikeBinary(byte[] bytes)
    {
        var sample = Math.Min(bytes.Length, 8 * 1024);
        for (var i = 0; i < sample; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string ReplaceLiteralCounted(string input, string pattern, string replacement, out int count)
    {
        if (pattern.Length == 0)
        {
            count = 0;
            return input;
        }

        count = 0;
        var builder = new StringBuilder(input.Length);
        var index = 0;
        while (index < input.Length)
        {
            var hit = input.IndexOf(pattern, index, StringComparison.Ordinal);
            if (hit < 0)
            {
                builder.Append(input, index, input.Length - index);
                break;
            }

            builder.Append(input, index, hit - index);
            builder.Append(replacement);
            count += 1;
            index = hit + pattern.Length;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Translates a minimal glob pattern into a regex anchored against workspace-relative paths
    /// (forward-slash separators). Supports: <c>*</c> matches any chars except <c>/</c>;
    /// <c>**</c> matches any chars including <c>/</c>; <c>?</c> matches one non-<c>/</c> char;
    /// other characters are matched literally. Returns null on a malformed pattern.
    /// </summary>
    private static Regex? TryCompileGlob(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < normalized.Length)
        {
            var c = normalized[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                    {
                        if (i + 2 < normalized.Length && normalized[i + 2] == '/')
                        {
                            sb.Append("(?:.*/)?");
                            i += 3;
                        }
                        else
                        {
                            sb.Append(".*");
                            i += 2;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                        i += 1;
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    i += 1;
                    break;
                case '.':
                case '+':
                case '(':
                case ')':
                case '{':
                case '}':
                case '[':
                case ']':
                case '^':
                case '$':
                case '|':
                case '\\':
                    sb.Append('\\').Append(c);
                    i += 1;
                    break;
                default:
                    sb.Append(c);
                    i += 1;
                    break;
            }
        }

        sb.Append('$');

        try
        {
            return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private readonly record struct BulkReplaceEnumeration(
        IReadOnlyList<(string Relative, string Absolute)> Files,
        WorkspaceMutationRefusal? Refusal)
    {
        public static BulkReplaceEnumeration OfFiles(IReadOnlyList<(string Relative, string Absolute)> files) =>
            new(files, null);

        public static BulkReplaceEnumeration OfRefusal(WorkspaceMutationRefusal refusal) =>
            new(Array.Empty<(string Relative, string Absolute)>(), refusal);
    }
}
