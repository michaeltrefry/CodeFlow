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
    bool EnableHostTools = true,
    IReadOnlyDictionary<string, AgentInvocationConfiguration>? SubAgents = null,
    IReadOnlyList<McpToolDefinition>? McpTools = null);
