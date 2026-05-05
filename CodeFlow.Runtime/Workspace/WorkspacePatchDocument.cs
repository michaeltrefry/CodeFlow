using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Parsed representation of a V4A-style workspace patch (`*** Begin Patch` / `*** End Patch`).
/// Used by `WorkspaceHostToolService.ApplyPatchAsync` to apply the patch to the active
/// workspace and by `WorkspacePatchValidator` (Authority/Admission) to enforce path
/// confinement and per-command allowances before the patch is admitted.
/// </summary>
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
