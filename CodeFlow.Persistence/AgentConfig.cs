using CodeFlow.Runtime;

namespace CodeFlow.Persistence;


public sealed record AgentConfig(
    string Key,
    int Version,
    AgentKind Kind,
    AgentInvocationConfiguration Configuration,
    string ConfigJson,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    IReadOnlyList<AgentOutputDeclaration>? Outputs = null,
    string? OwningWorkflowKey = null,
    string? ForkedFromKey = null,
    int? ForkedFromVersion = null)
{
    public IReadOnlyList<AgentOutputDeclaration> DeclaredOutputs =>
        Outputs ?? Array.Empty<AgentOutputDeclaration>();

    public bool IsWorkflowScoped => !string.IsNullOrEmpty(OwningWorkflowKey);

    public bool IsFork => !string.IsNullOrEmpty(ForkedFromKey) && ForkedFromVersion.HasValue;
}
