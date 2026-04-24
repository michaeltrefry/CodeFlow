using System.Text.Json.Serialization;

namespace CodeFlow.Runtime;

public sealed record AgentInvocationConfiguration(
    string Provider,
    string Model,
    string? SystemPrompt = null,
    string? PromptTemplate = null,
    IReadOnlyDictionary<string, string?>? Variables = null,
    IReadOnlyList<ChatMessage>? History = null,
    ToolAccessPolicy? ToolAccessPolicy = null,
    InvocationLoopBudget? Budget = null,
    int? MaxTokens = null,
    double? Temperature = null,
    IReadOnlyDictionary<string, AgentInvocationConfiguration>? SubAgents = null,
    RetryContext? RetryContext = null,
    // Runtime-only: populated by AgentInvocationConsumer from AgentConfig.Outputs. Not persisted
    // to avoid duplicate storage with the top-level `outputs` field in the stored config JSON.
    [property: JsonIgnore] IReadOnlyList<AgentOutputDeclaration>? DeclaredOutputs = null,
    IReadOnlyDictionary<string, string>? DecisionOutputTemplates = null);
