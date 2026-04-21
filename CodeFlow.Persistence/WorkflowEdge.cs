namespace CodeFlow.Persistence;

public sealed record WorkflowEdge(
    Guid FromNodeId,
    string FromPort,
    Guid ToNodeId,
    string ToPort,
    bool RotatesRound,
    int SortOrder)
{
    public const string DefaultInputPort = "in";
}
