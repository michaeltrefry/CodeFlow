using System.Text.Json.Serialization;

namespace CodeFlow.Runtime;

/// <summary>
/// Pin from an agent to a specific version of a Scriban partial. Persists alongside the rest of
/// the agent configuration so that bumping a partial does not silently change pinned agents'
/// behavior — they continue to render against the version they were saved with.
/// </summary>
public sealed record PromptPartialPin(string Key, int Version);

public sealed record AgentInvocationConfiguration(
    string Provider,
    string Model,
    string? SystemPrompt = null,
    string? PromptTemplate = null,
    IReadOnlyDictionary<string, string?>? Variables = null,
    IReadOnlyList<ChatMessage>? History = null,
    InvocationLoopBudget? Budget = null,
    int? MaxTokens = null,
    double? Temperature = null,
    IReadOnlyDictionary<string, AgentInvocationConfiguration>? SubAgents = null,
    RetryContext? RetryContext = null,
    // Runtime-only: populated by AgentInvocationConsumer from AgentConfig.Outputs. Not persisted
    // to avoid duplicate storage with the top-level `outputs` field in the stored config JSON.
    [property: JsonIgnore] IReadOnlyList<AgentOutputDeclaration>? DeclaredOutputs = null,
    IReadOnlyDictionary<string, string>? DecisionOutputTemplates = null,
    // Persisted: which partials this agent pins. Resolved to bodies at runtime.
    IReadOnlyList<PromptPartialPin>? PartialPins = null,
    // Runtime-only: pre-resolved partial bodies, populated by AgentInvocationConsumer from
    // PartialPins via IPromptPartialRepository before ContextAssembler runs.
    [property: JsonIgnore] IReadOnlyDictionary<string, string>? ResolvedPartials = null);
