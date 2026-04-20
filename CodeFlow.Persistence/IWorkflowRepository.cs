using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Persistence;

public interface IWorkflowRepository
{
    Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default);

    Task<Workflow?> GetLatestAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workflow>> ListLatestAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workflow>> ListVersionsAsync(string key, CancellationToken cancellationToken = default);

    Task<WorkflowEdge?> FindNextAsync(
        string key,
        int version,
        string fromAgentKey,
        AgentDecision decision,
        JsonElement? discriminator = null,
        CancellationToken cancellationToken = default);

    Task<int> CreateNewVersionAsync(
        WorkflowDraft draft,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowDraft(
    string Key,
    string Name,
    string StartAgentKey,
    string? EscalationAgentKey,
    int MaxRoundsPerRound,
    IReadOnlyList<WorkflowEdgeDraft> Edges);

public sealed record WorkflowEdgeDraft(
    string FromAgentKey,
    AgentDecisionKind Decision,
    JsonElement? Discriminator,
    string ToAgentKey,
    bool RotatesRound,
    int SortOrder);
