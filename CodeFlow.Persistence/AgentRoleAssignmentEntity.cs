namespace CodeFlow.Persistence;

public sealed class AgentRoleAssignmentEntity
{
    public string AgentKey { get; set; } = null!;

    public int AgentVersion { get; set; }

    public long RoleId { get; set; }

    public AgentRoleEntity Role { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; }
}
