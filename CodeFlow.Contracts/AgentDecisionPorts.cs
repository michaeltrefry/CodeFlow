namespace CodeFlow.Contracts;

/// <summary>
/// Canonical mapping from an agent's <see cref="AgentDecisionKind"/> to the named output port
/// used on the workflow edge. Fine-grained payload-based routing (previously done via edge
/// discriminators) is now expressed by placing a <c>Logic</c> node after the agent.
/// </summary>
public static class AgentDecisionPorts
{
    public const string FailedPort = "Failed";

    public static string ToPortName(AgentDecisionKind kind) => kind.ToString();
}
