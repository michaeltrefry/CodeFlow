using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scriban.Runtime;

namespace CodeFlow.Runtime;

public sealed class ContextAssembler
{
    internal static TimeSpan RenderTimeout => ScribanTemplateRenderer.RenderTimeout;

    private readonly IScribanTemplateRenderer renderer;

    public ContextAssembler() : this(new ScribanTemplateRenderer())
    {
    }

    public ContextAssembler(IScribanTemplateRenderer renderer)
    {
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    // Matches the exact shape the legacy regex engine handled: bare `{{ name }}` or `{{ path.to.key }}`
    // references with no expressions, pipes, filters, or statement keywords. Anything outside this shape
    // is forwarded to Scriban verbatim.
    private static readonly Regex LegacyPlaceholderPattern = new(
        "{{\\s*(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z0-9_-]+)*)\\s*}}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // Finds loop-bound names (`{{ for x in y }}`, `{% for x in y %}`) so their usages inside the
    // loop don't get pre-escaped as "unresolved" — the engine supplies them at render time.
    private static readonly Regex LocalBindingPattern = new(
        "(?:\\{\\{-?|\\{%-?)\\s*(?:for|capture)\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // Scriban statement/expression keywords that can appear as the sole identifier inside `{{ ... }}`
    // (for example, `{{ end }}`). These must pass through to the engine rather than being treated as
    // unresolved variable references.
    private static readonly HashSet<string> ScribanKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "end", "for", "in", "do", "while", "break", "continue", "capture",
        "case", "when", "with", "ret", "import", "include", "wrap", "func", "tablerow",
        "cycle", "this", "null", "true", "false", "and", "or", "not", "empty", "blank",
        "nan", "as", "offset", "limit", "reversed", "by", "unless", "endcase", "endfor",
        "endif", "endunless", "endcapture", "endwhile"
    };

    public IReadOnlyList<ChatMessage> Assemble(ContextAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<ChatMessage>();

        var renderedSystemPrompt = RenderTemplate(request.SystemPrompt, request.Variables, request.Input);
        var skillsBlock = BuildSkillsBlock(request.Skills, request.Variables, request.Input);
        var outputFormatBlock = BuildOutputFormatBlock(request.DeclaredOutputs);

        var systemContent = Combine(renderedSystemPrompt, skillsBlock, outputFormatBlock);
        if (!string.IsNullOrWhiteSpace(systemContent))
        {
            messages.Add(new ChatMessage(ChatMessageRole.System, systemContent));
        }

        var retryNote = BuildRetryNote(request.RetryContext);
        if (retryNote is not null)
        {
            messages.Add(new ChatMessage(ChatMessageRole.System, retryNote));
        }

        if (request.History is { Count: > 0 })
        {
            messages.AddRange(request.History);
        }

        var nextUserMessage = BuildUserMessage(request);
        if (!string.IsNullOrWhiteSpace(nextUserMessage))
        {
            messages.Add(new ChatMessage(ChatMessageRole.User, nextUserMessage));
        }

        return messages;
    }

    private string? BuildUserMessage(ContextAssemblyRequest request)
    {
        var renderedPrompt = RenderTemplate(request.PromptTemplate, request.Variables, request.Input);
        var trimmedInput = string.IsNullOrWhiteSpace(request.Input) ? null : request.Input.Trim();

        if (string.IsNullOrWhiteSpace(renderedPrompt))
        {
            return trimmedInput;
        }

        if (trimmedInput is null || TemplateConsumesInput(request.PromptTemplate))
        {
            return renderedPrompt;
        }

        return $"{renderedPrompt}{Environment.NewLine}{Environment.NewLine}{trimmedInput}";
    }

    private string? RenderTemplate(
        string? template,
        IReadOnlyDictionary<string, string?>? variables,
        string? input)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var rendered = ApplyVariables(template, variables, input);
        var trimmed = rendered.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private string ApplyVariables(
        string template,
        IReadOnlyDictionary<string, string?>? variables,
        string? input)
    {
        var flat = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (variables is not null)
        {
            foreach (var entry in variables)
            {
                flat[entry.Key] = entry.Value;
            }
        }

        flat["input"] = input;

        var locallyBound = CollectLocallyBoundNames(template);

        // Pre-pass: any legacy-style `{{ name }}` that can't resolve stays as literal text,
        // matching the old regex substituter. Anything not matching the legacy shape (Scriban
        // expressions, conditionals, loops, filters) falls through to the engine untouched.
        var escaped = LegacyPlaceholderPattern.Replace(
            template,
            match =>
            {
                var name = match.Groups["name"].Value;
                if (ScribanKeywords.Contains(name))
                {
                    return match.Value;
                }

                var root = GetRootSegment(name);
                if (locallyBound.Contains(root))
                {
                    return match.Value;
                }

                return ResolvesInFlat(flat, name)
                    ? match.Value
                    : EscapeLegacyLiteral(match.Value);
            });

        return RenderWithScriban(escaped, flat);
    }

    private static HashSet<string> CollectLocallyBoundNames(string template)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in LocalBindingPattern.Matches(template))
        {
            names.Add(match.Groups["name"].Value);
        }
        return names;
    }

    private static string GetRootSegment(string dottedName)
    {
        var dot = dottedName.IndexOf('.');
        return dot < 0 ? dottedName : dottedName[..dot];
    }

    private string RenderWithScriban(string template, IReadOnlyDictionary<string, string?> flat)
    {
        var scriptObject = BuildScriptObject(flat);
        return renderer.Render(template, scriptObject);
    }

    private static string EscapeLegacyLiteral(string literal)
    {
        // Emit a Scriban expression that outputs the verbatim legacy token as a raw string literal.
        var escaped = literal.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "{{ \"" + escaped + "\" }}";
    }

    private static bool ResolvesInFlat(IReadOnlyDictionary<string, string?> flat, string name)
    {
        if (flat.TryGetValue(name, out var value))
        {
            return value is not null;
        }

        return false;
    }

    private static ScriptObject BuildScriptObject(IReadOnlyDictionary<string, string?> flat)
    {
        var root = new ScriptObject();

        // Populate shallow (single-segment) keys first so nested containers overwrite leaf scalars
        // rather than the other way around — e.g. `context` is often set as a bare key via configured
        // variables while `context.foo` and `context.foo.bar` arrive from the flattened JSON tree.
        foreach (var (key, value) in flat.OrderBy(e => e.Key.Count(c => c == '.')))
        {
            SetNestedPath(root, key.Split('.'), value);
        }

        return root;
    }

    private static void SetNestedPath(ScriptObject root, string[] parts, string? value)
    {
        if (parts.Length == 0)
        {
            return;
        }

        object current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            var nextPart = parts[i + 1];
            var nextIsIndex = IsArrayIndex(nextPart, out _);

            current = DescendOrCreate(current, part, nextIsIndex);
            if (current is not ScriptObject and not ScriptArray)
            {
                return;
            }
        }

        var leafKey = parts[^1];
        var coerced = CoerceValue(value);

        switch (current)
        {
            case ScriptObject obj:
                if (!(obj.TryGetValue(leafKey, out var existingLeaf)
                      && existingLeaf is ScriptObject or ScriptArray))
                {
                    obj[leafKey] = coerced;
                }
                break;

            case ScriptArray arr when IsArrayIndex(leafKey, out var leafIndex):
                EnsureArrayCapacity(arr, leafIndex + 1);
                if (!(arr[leafIndex] is ScriptObject or ScriptArray))
                {
                    arr[leafIndex] = coerced;
                }
                break;
        }
    }

    private static object DescendOrCreate(object current, string part, bool childIsIndex)
    {
        switch (current)
        {
            case ScriptObject obj:
                if (obj.TryGetValue(part, out var existing)
                    && existing is ScriptObject or ScriptArray)
                {
                    return existing;
                }

                object next = childIsIndex ? new ScriptArray() : new ScriptObject();
                obj[part] = next;
                return next;

            case ScriptArray arr when IsArrayIndex(part, out var index):
                EnsureArrayCapacity(arr, index + 1);
                var existingElement = arr[index];
                if (existingElement is ScriptObject or ScriptArray)
                {
                    return existingElement!;
                }

                object container = childIsIndex ? new ScriptArray() : new ScriptObject();
                arr[index] = container;
                return container;

            default:
                return current;
        }
    }

    private static void EnsureArrayCapacity(ScriptArray array, int size)
    {
        while (array.Count < size)
        {
            array.Add(null);
        }
    }

    private static bool IsArrayIndex(string part, out int index)
    {
        return int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
               && index >= 0;
    }

    private static object? CoerceValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Coerce plain integer strings so numeric comparisons (e.g. `round < maxRounds`) behave
        // intuitively. Preserve the string shape for anything else — zero-padded numbers, decimals,
        // version strings, etc.
        if (LooksLikePlainInteger(value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        return value;
    }

    private static bool LooksLikePlainInteger(string value)
    {
        if (value.Length == 0 || value.Length > 10)
        {
            return false;
        }

        var start = value[0] == '-' ? 1 : 0;
        if (start == value.Length)
        {
            return false;
        }

        // Reject leading zeros on multi-digit values so "007" keeps rendering as "007".
        if (value.Length - start > 1 && value[start] == '0')
        {
            return false;
        }

        for (var i = start; i < value.Length; i++)
        {
            if (value[i] < '0' || value[i] > '9')
            {
                return false;
            }
        }

        return true;
    }

    private string? BuildSkillsBlock(
        IReadOnlyList<ResolvedSkill>? skills,
        IReadOnlyDictionary<string, string?>? variables,
        string? input)
    {
        if (skills is null || skills.Count == 0)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("## Skills").Append(Environment.NewLine);
        builder.Append("You have access to the following skills. Use them when relevant.");

        foreach (var skill in skills)
        {
            builder.Append(Environment.NewLine).Append(Environment.NewLine);
            builder.Append("### ").Append(skill.Name);
            var renderedBody = ApplyVariables(skill.Body ?? string.Empty, variables, input).TrimEnd();
            if (!string.IsNullOrWhiteSpace(renderedBody))
            {
                builder.Append(Environment.NewLine).Append(renderedBody);
            }
        }

        return builder.ToString();
    }

    private static string? Combine(params string?[] sections)
    {
        var nonEmpty = sections
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToArray();

        if (nonEmpty.Length == 0)
        {
            return null;
        }

        var separator = $"{Environment.NewLine}{Environment.NewLine}";
        return string.Join(separator, nonEmpty);
    }

    private static string? BuildOutputFormatBlock(IReadOnlyList<AgentOutputDeclaration>? declaredOutputs)
    {
        if (declaredOutputs is null || declaredOutputs.Count == 0)
        {
            return null;
        }

        var withExamples = declaredOutputs
            .Where(output => output.PayloadExample is not null
                && !string.IsNullOrWhiteSpace(output.Kind))
            .ToArray();

        if (withExamples.Length == 0)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("## Response format").Append(Environment.NewLine);
        builder.Append(
            "Your final response must be valid JSON. Match the shape listed below for the decision "
            + "kind you emit. Kinds not listed here have no required shape.");

        foreach (var output in withExamples)
        {
            var example = FormatPayloadExample(output.PayloadExample!.Value);
            builder.Append(Environment.NewLine).Append(Environment.NewLine);
            builder.Append("### ").Append(output.Kind.Trim());
            if (!string.IsNullOrWhiteSpace(output.Description))
            {
                builder.Append(" — ").Append(output.Description!.Trim());
            }
            builder.Append(Environment.NewLine);
            builder.Append("```json").Append(Environment.NewLine);
            builder.Append(example).Append(Environment.NewLine);
            builder.Append("```");
        }

        return builder.ToString();
    }

    private static string FormatPayloadExample(JsonElement example)
    {
        try
        {
            return JsonSerializer.Serialize(
                example,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return example.GetRawText();
        }
    }

    private static string? BuildRetryNote(RetryContext? retryContext)
    {
        if (retryContext is null)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        var priorAttemptNumber = Math.Max(1, retryContext.AttemptNumber - 1);
        builder.Append("This is attempt #").Append(retryContext.AttemptNumber).Append('.');
        builder.Append(' ').Append("Prior attempt #").Append(priorAttemptNumber).Append(" failed.");

        if (!string.IsNullOrWhiteSpace(retryContext.PriorFailureReason))
        {
            builder.Append(' ').Append("Reason: ").Append(retryContext.PriorFailureReason.Trim()).Append('.');
        }

        if (!string.IsNullOrWhiteSpace(retryContext.PriorAttemptSummary))
        {
            builder.Append(Environment.NewLine);
            builder.Append("Summary of prior attempt: ").Append(retryContext.PriorAttemptSummary.Trim());
        }

        builder.Append(Environment.NewLine);
        builder.Append("Address the prior failure before repeating the same approach.");

        return builder.ToString();
    }

    private static bool TemplateConsumesInput(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        return LegacyPlaceholderPattern
            .Matches(template)
            .Cast<Match>()
            .Any(static match =>
            {
                var name = match.Groups["name"].Value;
                return string.Equals(name, "input", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("input.", StringComparison.OrdinalIgnoreCase);
            });
    }
}

public sealed class PromptTemplateException : Exception
{
    public PromptTemplateException(string message) : base(message)
    {
    }

    public PromptTemplateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
