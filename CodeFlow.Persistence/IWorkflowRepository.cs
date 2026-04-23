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
        Guid fromNodeId,
        string outputPortName,
        CancellationToken cancellationToken = default);

    Task<int> CreateNewVersionAsync(
        WorkflowDraft draft,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowDraft(
    string Key,
    string Name,
    int MaxRoundsPerRound,
    IReadOnlyList<WorkflowNodeDraft> Nodes,
    IReadOnlyList<WorkflowEdgeDraft> Edges,
    IReadOnlyList<WorkflowInputDraft> Inputs);

public sealed record WorkflowNodeDraft(
    Guid Id,
    WorkflowNodeKind Kind,
    string? AgentKey,
    int? AgentVersion,
    string? Script,
    IReadOnlyList<string> OutputPorts,
    double LayoutX,
    double LayoutY,
    string? SubflowKey = null,
    int? SubflowVersion = null,
    int? ReviewMaxRounds = null,
    string? LoopDecision = null);

public sealed record WorkflowEdgeDraft(
    Guid FromNodeId,
    string FromPort,
    Guid ToNodeId,
    string ToPort,
    bool RotatesRound,
    int SortOrder);

public sealed record WorkflowInputDraft(
    string Key,
    string DisplayName,
    WorkflowInputKind Kind,
    bool Required,
    string? DefaultValueJson,
    string? Description,
    int Ordinal);
