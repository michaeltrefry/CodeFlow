namespace CodeFlow.Persistence;

public sealed class AgentRoleToolGrantEntity
{
    public long Id { get; set; }

    public long RoleId { get; set; }

    public AgentRoleEntity Role { get; set; } = null!;

    public AgentRoleToolCategory Category { get; set; }

    public string ToolIdentifier { get; set; } = null!;
}
