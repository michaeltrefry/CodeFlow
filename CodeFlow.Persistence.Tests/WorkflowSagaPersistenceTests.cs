using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class WorkflowSagaPersistenceTests : IAsyncLifetime
{
    private readonly MariaDbContainer container = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_saga_persistence")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await container.DisposeAsync();
    }

    [Fact]
    public async Task Saga_ShouldPersistAndReloadWithTypedAccessors()
    {
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(options, container.GetConnectionString());

        await using (var migrationContext = new CodeFlowDbContext(options.Options))
        {
            await migrationContext.Database.MigrateAsync();
        }

        var correlationId = Guid.NewGuid();
        var traceId = Guid.NewGuid();
        var currentRoundId = Guid.NewGuid();

        await using (var writeContext = new CodeFlowDbContext(options.Options))
        {
            var saga = new WorkflowSagaStateEntity
            {
                CorrelationId = correlationId,
                TraceId = traceId,
                CurrentState = "Running",
                CurrentAgentKey = "reviewer",
                CurrentRoundId = currentRoundId,
                RoundCount = 2,
                WorkflowKey = "article-flow",
                WorkflowVersion = 3,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            saga.PinAgentVersion("evaluator", 1);
            saga.PinAgentVersion("reviewer", 2);
            saga.AppendDecision(new DecisionRecord(
                AgentKey: "evaluator",
                AgentVersion: 1,
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: null,
                RoundId: currentRoundId,
                RecordedAtUtc: DateTime.UtcNow));

            writeContext.WorkflowSagas.Add(saga);
            await writeContext.SaveChangesAsync();
        }

        await using (var readContext = new CodeFlowDbContext(options.Options))
        {
            var loaded = await readContext.WorkflowSagas
                .AsNoTracking()
                .SingleAsync(s => s.CorrelationId == correlationId);

            loaded.TraceId.Should().Be(traceId);
            loaded.CurrentState.Should().Be("Running");
            loaded.CurrentAgentKey.Should().Be("reviewer");
            loaded.CurrentRoundId.Should().Be(currentRoundId);
            loaded.RoundCount.Should().Be(2);
            loaded.WorkflowKey.Should().Be("article-flow");
            loaded.WorkflowVersion.Should().Be(3);

            var versions = loaded.GetPinnedAgentVersions();
            versions.Should().ContainKey("evaluator").WhoseValue.Should().Be(1);
            versions.Should().ContainKey("reviewer").WhoseValue.Should().Be(2);

            var history = loaded.GetDecisionHistory();
            history.Should().ContainSingle();
            history[0].AgentKey.Should().Be("evaluator");
            history[0].Decision.Should().Be(AgentDecisionKind.Completed);
        }

        // Saga must implement MassTransit's state-machine interface for EF saga repository binding.
        typeof(SagaStateMachineInstance).IsAssignableFrom(typeof(WorkflowSagaStateEntity)).Should().BeTrue();
    }
}
