namespace CodeFlow.Persistence;

public sealed record WorkflowEdge(
    Guid FromNodeId,
    string FromPort,
    Guid ToNodeId,
    string ToPort,
    bool RotatesRound,
    int SortOrder,
    bool IntentionalBackedge = false)
{
    public const string DefaultInputPort = "in";
}
