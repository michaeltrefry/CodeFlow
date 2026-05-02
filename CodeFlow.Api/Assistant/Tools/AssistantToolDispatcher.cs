using System.Text.Json;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Looks up tools by name and invokes them. Wraps tool failures (unknown name, bad-arguments JSON,
/// thrown exceptions) into structured <see cref="AssistantToolResult"/>s with <c>IsError = true</c>
/// so the assistant chat loop can forward them to the LLM as recoverable tool errors instead of
/// crashing the turn.
/// </summary>
public sealed class AssistantToolDispatcher
{
    /// <summary>
    /// Maximum bytes a tool result may contribute to the LLM's context. Results that exceed the
    /// cap are replaced with an error payload pointing at the offending tool — the dispatcher
    /// will not silently truncate JSON because chopped JSON is invalid and the model will
    /// hallucinate around the tail.
    /// </summary>
    /// <remarks>
    /// Sized for the largest curated payload the assistant intentionally loads — the
    /// workflow-authoring skill body (AS-3) lands around 40 KB after JSON wrapping. The cap is
    /// still well below the provider's tool-result limit; arbitrary registry tools that approach
    /// it indicate a missing limit/filter on the tool side, not a budget shortage.
    /// </remarks>
    public const int MaxResultBytes = 96 * 1024;

    private readonly IReadOnlyDictionary<string, IAssistantTool> tools;

    public AssistantToolDispatcher(IEnumerable<IAssistantTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var dict = new Dictionary<string, IAssistantTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!dict.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException(
                    $"Duplicate assistant tool name '{tool.Name}'. Each IAssistantTool must have a unique Name.");
            }
        }

        this.tools = dict;
    }

    public IReadOnlyCollection<IAssistantTool> Tools => (IReadOnlyCollection<IAssistantTool>)tools.Values;

    public bool TryGet(string name, out IAssistantTool tool) => tools.TryGetValue(name, out tool!);

    public async Task<AssistantToolResult> InvokeAsync(
        string name,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!tools.TryGetValue(name, out var tool))
        {
            return ErrorResult($"Unknown tool '{name}'. Available tools: {string.Join(", ", tools.Keys.OrderBy(k => k))}.");
        }

        AssistantToolResult result;
        try
        {
            result = await tool.InvokeAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErrorResult($"Tool '{name}' threw {ex.GetType().Name}: {ex.Message}");
        }

        if (result.ResultJson.Length > MaxResultBytes)
        {
            return ErrorResult(
                $"Tool '{name}' returned {result.ResultJson.Length} bytes which exceeds the {MaxResultBytes}-byte cap. " +
                "Re-invoke with a tighter filter (smaller limit, more selective query) or fetch a single record by id.");
        }

        return result;
    }

    private static AssistantToolResult ErrorResult(string message)
    {
        var payload = JsonSerializer.Serialize(new { error = message });
        return new AssistantToolResult(payload, IsError: true);
    }
}
