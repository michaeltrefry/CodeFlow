using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace CodeFlow.Runtime;

public interface IScribanTemplateRenderer
{
    string Render(string template, ScriptObject scriptObject, CancellationToken cancellationToken = default);
}

public sealed class ScribanTemplateRenderer : IScribanTemplateRenderer
{
    internal static readonly TimeSpan RenderTimeout = TimeSpan.FromMilliseconds(50);
    private const int DefaultLoopLimit = 1000;
    private const int DefaultRecursiveLimit = 64;
    private const int DefaultLimitToString = 1_000_000;

    public string Render(string template, ScriptObject scriptObject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(scriptObject);

        Template parsed;
        try
        {
            parsed = Template.Parse(template);
        }
        catch (Exception ex) when (ex is not PromptTemplateException)
        {
            throw new PromptTemplateException(
                $"Prompt template could not be parsed: {ex.Message}", ex);
        }

        if (parsed.HasErrors)
        {
            var details = string.Join(
                "; ",
                parsed.Messages.Where(m => m.Type == ParserMessageType.Error).Select(m => m.Message));
            throw new PromptTemplateException(
                string.IsNullOrWhiteSpace(details)
                    ? "Prompt template has syntax errors."
                    : $"Prompt template has syntax errors: {details}");
        }

        using var timeoutCts = new CancellationTokenSource(RenderTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, cancellationToken);

        var context = new TemplateContext
        {
            TemplateLoader = null,
            LoopLimit = DefaultLoopLimit,
            RecursiveLimit = DefaultRecursiveLimit,
            LimitToString = DefaultLimitToString,
            StrictVariables = false,
            EnableRelaxedMemberAccess = true,
            CancellationToken = linkedCts.Token
        };
        context.PushGlobal(scriptObject);

        try
        {
            return parsed.Render(context);
        }
        catch (ScriptAbortException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            throw new PromptTemplateException(
                $"Prompt template render exceeded the {RenderTimeout.TotalMilliseconds:F0}ms sandbox budget.");
        }
        catch (ScriptRuntimeException ex)
        {
            throw new PromptTemplateException(
                $"Prompt template render failed: {ex.OriginalMessage}", ex);
        }
    }
}
