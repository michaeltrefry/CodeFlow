namespace CodeFlow.Persistence;

public sealed class AgentRoleNotFoundException : Exception
{
    public AgentRoleNotFoundException(long id)
        : base($"No agent role with id {id}.")
    {
    }

    public AgentRoleNotFoundException(string key)
        : base($"No agent role with key '{key}'.")
    {
    }
}
