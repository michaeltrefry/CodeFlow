namespace CodeFlow.Runtime;

public sealed record InvocationRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolSchema>? Tools,
    string Model,
    int? MaxTokens = null,
    double? Temperature = null);
