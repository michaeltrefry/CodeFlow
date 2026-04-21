namespace CodeFlow.Persistence;

public sealed class AgentRoleSkillGrantEntity
{
    public long Id { get; set; }

    public long RoleId { get; set; }

    public AgentRoleEntity Role { get; set; } = null!;

    public long SkillId { get; set; }

    public SkillEntity Skill { get; set; } = null!;
}
