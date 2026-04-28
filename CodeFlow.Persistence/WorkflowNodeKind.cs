namespace CodeFlow.Persistence;

public enum WorkflowNodeKind
{
    Start = 0,
    Agent = 1,
    Logic = 2,
    Hitl = 3,
    Subflow = 4,
    ReviewLoop = 5,
    Transform = 6,
    Swarm = 7
}
