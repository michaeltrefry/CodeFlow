using System.Text.RegularExpressions;

namespace CodeFlow.Api.Assistant.Skills;

/// <summary>
/// Parses a single skill markdown source into an <see cref="AssistantSkill"/>. Source format:
///
/// <code>
/// ---
/// key: workflow-authoring
/// name: Workflow authoring
/// description: Use when drafting / saving / editing workflows.
/// trigger: user wants to author or edit a workflow.
/// ---
///
/// Body markdown...
/// </code>
///
/// Frontmatter values are single-line. The body is everything after the closing <c>---</c>, with
/// leading whitespace trimmed so a skill author can leave a blank line between frontmatter and
/// body without it riding into the transcript.
/// </summary>
internal static class AssistantSkillParser
{
    private static readonly Regex KeyPattern = new(
        "^[a-z][a-z0-9-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AssistantSkill Parse(string fileName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        // Normalize line endings up front so the rest of the parser doesn't care whether the
        // resource was committed with CRLF (Windows) or LF (everything else).
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        // Find the opening `---`. Skip any leading blank lines so an editor's stray newline at
        // the top of the file doesn't trip the parser.
        var openIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (lines[i].Trim() == "---")
            {
                openIndex = i;
            }
            break;
        }

        if (openIndex < 0)
        {
            throw new InvalidSkillSourceException(
                fileName,
                "Skill must start with a `---` frontmatter delimiter.");
        }

        // Find the closing `---` after the opener.
        var closeIndex = -1;
        for (var i = openIndex + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                closeIndex = i;
                break;
            }
        }

        if (closeIndex < 0)
        {
            throw new InvalidSkillSourceException(
                fileName,
                "Skill frontmatter is missing its closing `---` delimiter.");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = openIndex + 1; i < closeIndex; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new InvalidSkillSourceException(
                    fileName,
                    $"Frontmatter line {i + 1} must be `key: value` — got '{line}'.");
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0)
            {
                throw new InvalidSkillSourceException(
                    fileName,
                    $"Frontmatter line {i + 1} has an empty key.");
            }

            if (!fields.TryAdd(name, value))
            {
                throw new InvalidSkillSourceException(
                    fileName,
                    $"Frontmatter key '{name}' appears more than once.");
            }
        }

        var key = RequireField(fields, "key", fileName);
        var displayName = RequireField(fields, "name", fileName);
        var description = RequireField(fields, "description", fileName);
        var trigger = RequireField(fields, "trigger", fileName);

        if (!KeyPattern.IsMatch(key))
        {
            throw new InvalidSkillSourceException(
                fileName,
                $"Skill key '{key}' must match {KeyPattern} (lowercase, digits, hyphen; starts with a letter).");
        }

        // Body is everything after the closing delimiter. Trim leading newlines so the customary
        // blank line between frontmatter and body doesn't ride into the loaded transcript content.
        var bodyStart = closeIndex + 1;
        var body = bodyStart < lines.Length
            ? string.Join('\n', lines, bodyStart, lines.Length - bodyStart).TrimStart('\n', ' ', '\t').TrimEnd()
            : string.Empty;

        if (body.Length == 0)
        {
            throw new InvalidSkillSourceException(
                fileName,
                "Skill body is empty — every skill must contribute markdown content the model can read.");
        }

        return new AssistantSkill(key, displayName, description, trigger, body);
    }

    private static string RequireField(Dictionary<string, string> fields, string name, string fileName)
    {
        if (!fields.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidSkillSourceException(
                fileName,
                $"Frontmatter is missing required field '{name}'.");
        }
        return value;
    }
}

/// <summary>
/// Thrown when a skill source cannot be parsed. <see cref="FileName"/> identifies the offending
/// resource so a fail-fast at startup points the developer at the right file.
/// </summary>
public sealed class InvalidSkillSourceException : Exception
{
    public InvalidSkillSourceException(string fileName, string message)
        : base($"Invalid skill source '{fileName}': {message}")
    {
        FileName = fileName;
    }

    public string FileName { get; }
}
