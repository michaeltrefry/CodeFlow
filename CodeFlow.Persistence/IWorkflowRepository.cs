namespace CodeFlow.Persistence;

public interface IWorkflowRepository
{
    /// <summary>
    /// Get a specific workflow version. Throws <see cref="WorkflowNotFoundException"/>
    /// when absent — use this overload only when the absence is genuinely an exceptional condition
    /// (e.g. saga routing for a previously-resolved version). For "absence is expected and the
    /// caller wants to react to it" lookups, prefer <see cref="TryGetAsync"/>
    /// (F-015 in the 2026-04-28 backend review).
    /// </summary>
    Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific workflow version, returning <c>null</c> when absent. Avoids the
    /// exception-as-flow cost on the request hot path for endpoints that treat "not found"
    /// as an ordinary 404. The default implementation falls back to the exception-throwing
    /// <see cref="GetAsync"/> so existing test fakes don't have to be updated; production
    /// implementations override it for the perf win.
    /// </summary>
    async Task<Workflow?> TryGetAsync(string key, int version, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync(key, version, cancellationToken);
        }
        catch (WorkflowNotFoundException)
        {
            return null;
        }
    }

    Task<Workflow?> GetLatestAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workflow>> ListLatestAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workflow>> ListVersionsAsync(string key, CancellationToken cancellationToken = default);

    Task<WorkflowEdge?> FindNextAsync(
        string key,
        int version,
        Guid fromNodeId,
        string outputPortName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the set of port names by which the given workflow version exits — used by the
    /// validator and editor to populate Subflow/ReviewLoop parent ports without hauling the full
    /// graph across every save. Computed by <see cref="Workflow.TerminalPorts"/>.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetTerminalPortsAsync(
        string key,
        int version,
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
    IReadOnlyList<WorkflowInputDraft> Inputs,
    WorkflowCategory Category = WorkflowCategory.Workflow,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? WorkflowVarsReads = null,
    IReadOnlyList<string>? WorkflowVarsWrites = null);

public sealed record WorkflowNodeDraft(
    Guid Id,
    WorkflowNodeKind Kind,
    string? AgentKey,
    int? AgentVersion,
    string? OutputScript,
    IReadOnlyList<string> OutputPorts,
    double LayoutX,
    double LayoutY,
    string? SubflowKey = null,
    int? SubflowVersion = null,
    int? ReviewMaxRounds = null,
    string? LoopDecision = null,
    string? InputScript = null,
    bool OptOutLastRoundReminder = false,
    RejectionHistoryConfig? RejectionHistory = null,
    string? MirrorOutputToWorkflowVar = null,
    IReadOnlyDictionary<string, string>? OutputPortReplacements = null,
    string? Template = null,
    string OutputType = "string",
    string? SwarmProtocol = null,
    int? SwarmN = null,
    string? ContributorAgentKey = null,
    int? ContributorAgentVersion = null,
    string? SynthesizerAgentKey = null,
    int? SynthesizerAgentVersion = null,
    string? CoordinatorAgentKey = null,
    int? CoordinatorAgentVersion = null,
    int? SwarmTokenBudget = null);

public sealed record WorkflowEdgeDraft(
    Guid FromNodeId,
    string FromPort,
    Guid ToNodeId,
    string ToPort,
    bool RotatesRound,
    int SortOrder,
    bool IntentionalBackedge = false);

public sealed record WorkflowInputDraft(
    string Key,
    string DisplayName,
    WorkflowInputKind Kind,
    bool Required,
    string? DefaultValueJson,
    string? Description,
    int Ordinal);
