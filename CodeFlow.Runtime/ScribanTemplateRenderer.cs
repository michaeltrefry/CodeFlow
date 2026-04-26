using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace CodeFlow.Runtime;

public interface IScribanTemplateRenderer
{
    string Render(string template, ScriptObject scriptObject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Render with a set of partials available to <c>{{ include "key" }}</c>. The partials map
    /// is keyed by the include name as it appears in the template. Pass <c>null</c> or an empty
    /// dict for the legacy no-partials behavior.
    /// </summary>
    string Render(
        string template,
        ScriptObject scriptObject,
        IReadOnlyDictionary<string, string>? partials,
        CancellationToken cancellationToken = default);
}

public sealed class ScribanTemplateRenderer : IScribanTemplateRenderer
{
    internal static readonly TimeSpan RenderTimeout = TimeSpan.FromMilliseconds(50);
    private const int DefaultLoopLimit = 1000;
    private const int DefaultRecursiveLimit = 64;
    private const int DefaultLimitToString = 1_000_000;

    // Built-in helpers exposed to every template. Pushed before the caller's scope so user
    // values can shadow them on a per-render basis if absolutely required, but in practice
    // these are stable framework-managed names (mirrors `ProtectedVariables` for scripts).
    private static readonly ScriptObject Builtins = CreateBuiltins();

    private static ScriptObject CreateBuiltins()
    {
        var builtins = new ScriptObject();
        builtins.Import(
            "branch_name",
            new Func<string?, string?, string>(BranchNameHelper.BranchName));
        return builtins;
    }

    public string Render(string template, ScriptObject scriptObject, CancellationToken cancellationToken = default) =>
        Render(template, scriptObject, partials: null, cancellationToken);

    public string Render(
        string template,
        ScriptObject scriptObject,
        IReadOnlyDictionary<string, string>? partials,
        CancellationToken cancellationToken = default)
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
            TemplateLoader = partials is { Count: > 0 } ? new PartialTemplateLoader(partials) : null,
            LoopLimit = DefaultLoopLimit,
            RecursiveLimit = DefaultRecursiveLimit,
            LimitToString = DefaultLimitToString,
            StrictVariables = false,
            EnableRelaxedMemberAccess = true,
            CancellationToken = linkedCts.Token
        };
        context.PushGlobal(Builtins);
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

    /// <summary>
    /// Resolves <c>{{ include "key" }}</c> against an in-memory dictionary of pre-fetched partial
    /// bodies. The renderer never touches the DB; callers (<see cref="ContextAssembler"/>) resolve
    /// pinned partials before render time and pass the resulting map.
    ///
    /// Unknown keys raise a Scriban runtime error, which the renderer surfaces as a
    /// <see cref="PromptTemplateException"/> — the author sees the offending include name.
    /// </summary>
    private sealed class PartialTemplateLoader : ITemplateLoader
    {
        private readonly IReadOnlyDictionary<string, string> partials;

        public PartialTemplateLoader(IReadOnlyDictionary<string, string> partials)
        {
            this.partials = partials;
        }

        public string GetPath(TemplateContext context, SourceSpan callerSpan, string templateName) => templateName;

        public string Load(TemplateContext context, SourceSpan callerSpan, string templatePath)
        {
            if (partials.TryGetValue(templatePath, out var body))
            {
                return body;
            }
            throw new ScriptRuntimeException(
                callerSpan,
                $"Unknown partial '{templatePath}'. Available partials: [{string.Join(", ", partials.Keys)}].");
        }

        public ValueTask<string> LoadAsync(TemplateContext context, SourceSpan callerSpan, string templatePath) =>
            new(Load(context, callerSpan, templatePath));
    }
}
