namespace CodeFlow.Runtime;

public sealed record ContextAssemblyRequest(
    string? SystemPrompt,
    string? PromptTemplate,
    string? Input,
    IReadOnlyList<ChatMessage>? History = null,
    IReadOnlyDictionary<string, string?>? Variables = null,
    RetryContext? RetryContext = null,
    IReadOnlyList<ResolvedSkill>? Skills = null,
    IReadOnlyList<AgentOutputDeclaration>? DeclaredOutputs = null);
