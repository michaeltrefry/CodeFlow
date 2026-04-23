using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeFlow.Runtime;

public sealed class ContextAssembler
{
    private static readonly Regex VariablePattern = new(
        "{{\\s*(?<name>[A-Za-z0-9_.-]+)\\s*}}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

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

    private static string? BuildUserMessage(ContextAssemblyRequest request)
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

    private static string? RenderTemplate(
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

    private static string ApplyVariables(
        string template,
        IReadOnlyDictionary<string, string?>? variables,
        string? input)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (variables is not null)
        {
            foreach (var entry in variables)
            {
                values[entry.Key] = entry.Value;
            }
        }

        values["input"] = input;

        return VariablePattern.Replace(
            template,
            match =>
            {
                var variableName = match.Groups["name"].Value;

                return values.TryGetValue(variableName, out var value) && value is not null
                    ? value
                    : match.Value;
            });
    }

    private static string? BuildSkillsBlock(
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

        return VariablePattern
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
