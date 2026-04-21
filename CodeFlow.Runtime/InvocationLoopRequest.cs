namespace CodeFlow.Runtime;

public sealed record InvocationLoopRequest(
    IReadOnlyList<ChatMessage> Messages,
    string Model,
    ToolAccessPolicy? ToolAccessPolicy = null,
    InvocationLoopBudget? Budget = null,
    int? MaxTokens = null,
    double? Temperature = null,
    AgentInvocationContext? Context = null);
