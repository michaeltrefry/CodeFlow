using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class CodeFlowDbContext(DbContextOptions<CodeFlowDbContext> options) : DbContext(options)
{
    public DbSet<AgentConfigEntity> Agents => Set<AgentConfigEntity>();

    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();

    public DbSet<WorkflowEdgeEntity> WorkflowEdges => Set<WorkflowEdgeEntity>();

    public DbSet<WorkflowSagaStateEntity> WorkflowSagas => Set<WorkflowSagaStateEntity>();

    public DbSet<HitlTaskEntity> HitlTasks => Set<HitlTaskEntity>();

    public DbSet<McpServerEntity> McpServers => Set<McpServerEntity>();

    public DbSet<McpServerToolEntity> McpServerTools => Set<McpServerToolEntity>();

    public DbSet<AgentRoleEntity> AgentRoles => Set<AgentRoleEntity>();

    public DbSet<AgentRoleToolGrantEntity> AgentRoleToolGrants => Set<AgentRoleToolGrantEntity>();

    public DbSet<AgentRoleAssignmentEntity> AgentRoleAssignments => Set<AgentRoleAssignmentEntity>();

    public DbSet<SkillEntity> Skills => Set<SkillEntity>();

    public DbSet<AgentRoleSkillGrantEntity> AgentRoleSkillGrants => Set<AgentRoleSkillGrantEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasCharSet("utf8mb4");
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PersistenceAssemblyMarker).Assembly);
    }
}
