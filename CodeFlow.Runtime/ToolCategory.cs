namespace CodeFlow.Runtime;

public enum ToolCategory
{
    Host,
    Execution,
    Mcp,
    SubAgent,
    // Epic 978: tools the Goal-node executor injects into a goal-runner agent's surface
    // (currently `goal.get` + `goal.update`). Scoped per-invocation via
    // <see cref="AgentInvocationConfiguration.GoalState"/> — never appears on Agent / Hitl /
    // Subflow / ReviewLoop / Swarm / Transform / ForEach invocations or the homepage assistant.
    Goal
}
