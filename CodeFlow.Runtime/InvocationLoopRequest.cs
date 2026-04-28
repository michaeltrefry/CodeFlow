namespace CodeFlow.Runtime;

/// <param name="Provider">Free-form provider label (e.g., "openai", "anthropic", "lmstudio")
/// resolved from <c>AgentInvocationConfiguration.Provider</c>. Threaded into the observer surface
/// so token-usage capture can attribute each record without a separate lookup.</param>
public sealed record InvocationLoopRequest(
    IReadOnlyList<ChatMessage> Messages,
    string Model,
    ToolAccessPolicy? ToolAccessPolicy = null,
    InvocationLoopBudget? Budget = null,
    int? MaxTokens = null,
    double? Temperature = null,
    ToolExecutionContext? ToolExecutionContext = null,
    IReadOnlyList<AgentOutputDeclaration>? DeclaredOutputs = null,
    string Provider = "");
