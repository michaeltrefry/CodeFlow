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
                Decision: "Completed",
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
                .Include(s => s.Decisions)
                .Include(s => s.LogicEvaluations)
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
            history[0].Decision.Should().Be("Completed");
        }

        // Saga must implement MassTransit's state-machine interface for EF saga repository binding.
        typeof(SagaStateMachineInstance).IsAssignableFrom(typeof(WorkflowSagaStateEntity)).Should().BeTrue();
    }

    [Fact]
    public async Task Saga_ShouldRoundTripSubflowParentLinkageAndWorkflowContext()
    {
        // Covers Slice S1 of the Subworkflow Composition epic: child-saga linkage columns
        // (parent_trace_id / parent_node_id / parent_round_id), the subflow_depth counter, and
        // the workflow_inputs_json bag persist on the saga row alongside the existing local
        // inputs_json column.
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(options, container.GetConnectionString());

        await using (var migrationContext = new CodeFlowDbContext(options.Options))
        {
            await migrationContext.Database.MigrateAsync();
        }

        var correlationId = Guid.NewGuid();
        var traceId = Guid.NewGuid();
        var parentTraceId = Guid.NewGuid();
        var parentNodeId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var currentRoundId = Guid.NewGuid();

        const string workflowJson = """{"sharedKey":"sharedValue"}""";
        const string localJson = """{"localKey":"localValue"}""";

        await using (var writeContext = new CodeFlowDbContext(options.Options))
        {
            writeContext.WorkflowSagas.Add(new WorkflowSagaStateEntity
            {
                CorrelationId = correlationId,
                TraceId = traceId,
                CurrentState = "Running",
                CurrentAgentKey = "child-agent",
                CurrentRoundId = currentRoundId,
                RoundCount = 0,
                WorkflowKey = "shared-utility",
                WorkflowVersion = 1,
                InputsJson = localJson,
                ParentTraceId = parentTraceId,
                ParentNodeId = parentNodeId,
                ParentRoundId = parentRoundId,
                SubflowDepth = 1,
                WorkflowInputsJson = workflowJson,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            await writeContext.SaveChangesAsync();
        }

        await using (var readContext = new CodeFlowDbContext(options.Options))
        {
            var loaded = await readContext.WorkflowSagas
                .AsNoTracking()
                .SingleAsync(s => s.CorrelationId == correlationId);

            loaded.ParentTraceId.Should().Be(parentTraceId);
            loaded.ParentNodeId.Should().Be(parentNodeId);
            loaded.ParentRoundId.Should().Be(parentRoundId);
            loaded.SubflowDepth.Should().Be(1);
            loaded.WorkflowInputsJson.Should().Be(workflowJson);
            loaded.InputsJson.Should().Be(localJson, "workflow_inputs_json and inputs_json must remain distinct columns");
        }

        // A top-level saga (no parent linkage) should default subflow_depth=0 and may leave the
        // workflow bag NULL — the read code in S6 will treat that as `{}`.
        var topLevelCorrelationId = Guid.NewGuid();
        await using (var writeContext = new CodeFlowDbContext(options.Options))
        {
            writeContext.WorkflowSagas.Add(new WorkflowSagaStateEntity
            {
                CorrelationId = topLevelCorrelationId,
                TraceId = Guid.NewGuid(),
                CurrentState = "Running",
                CurrentAgentKey = "top-level",
                CurrentRoundId = Guid.NewGuid(),
                WorkflowKey = "parent-flow",
                WorkflowVersion = 1,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await writeContext.SaveChangesAsync();
        }

        await using (var readContext = new CodeFlowDbContext(options.Options))
        {
            var topLevel = await readContext.WorkflowSagas
                .AsNoTracking()
                .SingleAsync(s => s.CorrelationId == topLevelCorrelationId);

            topLevel.ParentTraceId.Should().BeNull();
            topLevel.ParentNodeId.Should().BeNull();
            topLevel.ParentRoundId.Should().BeNull();
            topLevel.SubflowDepth.Should().Be(0);
            topLevel.WorkflowInputsJson.Should().BeNull();
        }
    }

    [Fact]
    public async Task Saga_ShouldRoundTripCurrentRoundEnteredAtAndDecisionNodeEnteredAt()
    {
        // sc-80: per-node round-start timestamp lands on the saga state and threads onto each
        // appended decision. Validates the migration's two new columns
        // (workflow_sagas.current_round_entered_at, workflow_saga_decisions.node_entered_at)
        // round-trip cleanly, and that an appended decision with no NodeEnteredAtUtc still
        // persists as NULL so legacy rows continue to render.
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(options, container.GetConnectionString());

        await using (var migrationContext = new CodeFlowDbContext(options.Options))
        {
            await migrationContext.Database.MigrateAsync();
        }

        var correlationId = Guid.NewGuid();
        var traceId = Guid.NewGuid();
        var roundOne = Guid.NewGuid();
        var roundTwo = Guid.NewGuid();
        var enteredRoundOne = new DateTime(2026, 4, 28, 10, 0, 0, DateTimeKind.Utc);
        var recordedRoundOne = enteredRoundOne.AddSeconds(15);
        var enteredRoundTwo = recordedRoundOne.AddMilliseconds(50);
        var recordedRoundTwo = enteredRoundTwo.AddSeconds(8);

        await using (var writeContext = new CodeFlowDbContext(options.Options))
        {
            var saga = new WorkflowSagaStateEntity
            {
                CorrelationId = correlationId,
                TraceId = traceId,
                CurrentState = "Running",
                CurrentAgentKey = "second",
                CurrentRoundId = roundTwo,
                CurrentRoundEnteredAtUtc = enteredRoundTwo,
                RoundCount = 1,
                WorkflowKey = "swarm-bench-sequential",
                WorkflowVersion = 1,
                CreatedAtUtc = enteredRoundOne,
                UpdatedAtUtc = recordedRoundTwo
            };

            // First decision: explicit NodeEnteredAtUtc threaded onto the record.
            saga.AppendDecision(new DecisionRecord(
                AgentKey: "first",
                AgentVersion: 1,
                Decision: "Out",
                DecisionPayload: null,
                RoundId: roundOne,
                RecordedAtUtc: recordedRoundOne,
                NodeEnteredAtUtc: enteredRoundOne));

            // Second decision: NodeEnteredAtUtc omitted — must persist as NULL so the schema
            // gracefully accommodates pre-migration / synthesised decisions that don't carry
            // a round-entry timestamp.
            saga.AppendDecision(new DecisionRecord(
                AgentKey: "second",
                AgentVersion: 1,
                Decision: "Out",
                DecisionPayload: null,
                RoundId: roundTwo,
                RecordedAtUtc: recordedRoundTwo));

            writeContext.WorkflowSagas.Add(saga);
            await writeContext.SaveChangesAsync();
        }

        await using (var readContext = new CodeFlowDbContext(options.Options))
        {
            var loaded = await readContext.WorkflowSagas
                .AsNoTracking()
                .Include(s => s.Decisions)
                .SingleAsync(s => s.CorrelationId == correlationId);

            loaded.CurrentRoundEnteredAtUtc.Should().BeCloseTo(enteredRoundTwo, TimeSpan.FromMilliseconds(1));

            var history = loaded.GetDecisionHistory();
            history.Should().HaveCount(2);

            history[0].NodeEnteredAtUtc.Should().NotBeNull();
            history[0].NodeEnteredAtUtc!.Value.Should().BeCloseTo(enteredRoundOne, TimeSpan.FromMilliseconds(1));
            history[0].RecordedAtUtc.Should().BeAfter(history[0].NodeEnteredAtUtc!.Value);

            history[1].NodeEnteredAtUtc.Should().BeNull();
        }
    }
}
