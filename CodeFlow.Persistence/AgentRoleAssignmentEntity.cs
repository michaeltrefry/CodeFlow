namespace CodeFlow.Persistence;

public sealed class AgentRoleAssignmentEntity
{
    public long Id { get; set; }

    public string AgentKey { get; set; } = null!;

    public long RoleId { get; set; }

    public AgentRoleEntity Role { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; }
}
