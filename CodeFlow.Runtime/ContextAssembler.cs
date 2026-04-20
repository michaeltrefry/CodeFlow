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
        if (!string.IsNullOrWhiteSpace(renderedSystemPrompt))
        {
            messages.Add(new ChatMessage(ChatMessageRole.System, renderedSystemPrompt));
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

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (variables is not null)
        {
            foreach (var entry in variables)
            {
                values[entry.Key] = entry.Value;
            }
        }

        values["input"] = input;

        var rendered = VariablePattern.Replace(
            template,
            match =>
            {
                var variableName = match.Groups["name"].Value;

                return values.TryGetValue(variableName, out var value) && value is not null
                    ? value
                    : match.Value;
            });

        var trimmed = rendered.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
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
            .Any(static match => string.Equals(
                match.Groups["name"].Value,
                "input",
                StringComparison.OrdinalIgnoreCase));
    }
}
