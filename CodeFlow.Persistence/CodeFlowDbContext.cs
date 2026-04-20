using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class CodeFlowDbContext(DbContextOptions<CodeFlowDbContext> options) : DbContext(options)
{
    public DbSet<AgentConfigEntity> Agents => Set<AgentConfigEntity>();

    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();

    public DbSet<WorkflowEdgeEntity> WorkflowEdges => Set<WorkflowEdgeEntity>();

    public DbSet<WorkflowSagaStateEntity> WorkflowSagas => Set<WorkflowSagaStateEntity>();

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
