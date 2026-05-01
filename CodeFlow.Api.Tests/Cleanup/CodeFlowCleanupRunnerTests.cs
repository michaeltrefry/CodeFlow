using CodeFlow.Host.Cleanup;
using CodeFlow.Persistence;
using CodeFlow.Persistence.Authority;
using CodeFlow.Persistence.Replay;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Tests.Cleanup;

public sealed class CodeFlowCleanupRunnerTests
{
    [Fact]
    public async Task DeleteOldTerminalTracesAsync_RemovesOldTerminalTraceEvidenceAndWorkdir()
    {
        using var tempRoot = new TempDirectory();
        await using var dbContext = CreateDbContext();
        var oldTrace = Guid.NewGuid();
        var oldCorrelation = Guid.NewGuid();
        var newTrace = Guid.NewGuid();
        var runningTrace = Guid.NewGuid();
        var now = DateTime.UtcNow;

        dbContext.WorkflowSagas.AddRange(
            Saga(oldTrace, oldCorrelation, "Completed", now.AddDays(-3)),
            Saga(newTrace, Guid.NewGuid(), "Completed", now),
            Saga(runningTrace, Guid.NewGuid(), "Running", now.AddDays(-3)));
        dbContext.HitlTasks.Add(new HitlTaskEntity
        {
            TraceId = oldTrace,
            RoundId = Guid.NewGuid(),
            NodeId = Guid.NewGuid(),
            AgentKey = "agent-a",
            AgentVersion = 1,
            WorkflowKey = "workflow-a",
            WorkflowVersion = 1,
            InputRef = "artifact://input",
            State = HitlTaskState.Pending,
            CreatedAtUtc = now.AddDays(-3),
        });
        dbContext.WorkflowSagaDecisions.Add(new WorkflowSagaDecisionEntity
        {
            SagaCorrelationId = oldCorrelation,
            Ordinal = 1,
            TraceId = oldTrace,
            RoundId = Guid.NewGuid(),
            RecordedAtUtc = now.AddDays(-3),
        });
        dbContext.WorkflowSagaLogicEvaluations.Add(new WorkflowSagaLogicEvaluationEntity
        {
            SagaCorrelationId = oldCorrelation,
            Ordinal = 1,
            TraceId = oldTrace,
            NodeId = Guid.NewGuid(),
            RoundId = Guid.NewGuid(),
            RecordedAtUtc = now.AddDays(-3),
        });
        dbContext.TokenUsageRecords.Add(new TokenUsageRecordEntity
        {
            Id = Guid.NewGuid(),
            TraceId = oldTrace,
            NodeId = Guid.NewGuid(),
            InvocationId = Guid.NewGuid(),
            Provider = "openai",
            Model = "gpt-test",
            RecordedAtUtc = now.AddDays(-3),
        });
        dbContext.RefusalEvents.Add(new RefusalEventEntity
        {
            Id = Guid.NewGuid(),
            TraceId = oldTrace,
            Stage = "tool",
            Code = "denied",
            Reason = "No.",
            OccurredAtUtc = now.AddDays(-3),
        });
        dbContext.AgentInvocationAuthority.Add(new AgentInvocationAuthorityEntity
        {
            Id = Guid.NewGuid(),
            TraceId = oldTrace,
            RoundId = Guid.NewGuid(),
            AgentKey = "agent-a",
            EnvelopeJson = "{}",
            ResolvedAtUtc = now.AddDays(-3),
        });
        dbContext.ReplayAttempts.Add(new ReplayAttemptEntity
        {
            Id = Guid.NewGuid(),
            ParentTraceId = oldTrace,
            LineageId = Guid.NewGuid(),
            ContentHash = new string('a', 64),
            ReplayState = "Completed",
            CreatedAtUtc = now.AddDays(-3),
        });
        await dbContext.SaveChangesAsync();

        var workdir = Path.Combine(tempRoot.Path, oldTrace.ToString("N"));
        Directory.CreateDirectory(workdir);
        await File.WriteAllTextAsync(Path.Combine(workdir, "marker.txt"), "old trace");

        var runner = CreateRunner(dbContext, tempRoot.Path);
        var result = await runner.DeleteOldTerminalTracesAsync(olderThanDays: 1);

        result.Should().Be(new TraceCleanupResult(1, 1));
        Directory.Exists(workdir).Should().BeFalse();
        (await dbContext.WorkflowSagas.CountAsync()).Should().Be(2);
        (await dbContext.WorkflowSagas.AnyAsync(s => s.TraceId == newTrace)).Should().BeTrue();
        (await dbContext.WorkflowSagas.AnyAsync(s => s.TraceId == runningTrace)).Should().BeTrue();
        (await dbContext.TokenUsageRecords.AnyAsync(r => r.TraceId == oldTrace)).Should().BeFalse();
        (await dbContext.RefusalEvents.AnyAsync(r => r.TraceId == oldTrace)).Should().BeFalse();
        (await dbContext.AgentInvocationAuthority.AnyAsync(r => r.TraceId == oldTrace)).Should().BeFalse();
        (await dbContext.ReplayAttempts.AnyAsync(r => r.ParentTraceId == oldTrace)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteUnreferencedRetiredObjectsAsync_DeletesRetiredWorkflowsNotReachableFromActiveWorkflows()
    {
        await using var dbContext = CreateDbContext();

        dbContext.Workflows.AddRange(
            Workflow("active-root", isRetired: false, subflows: ["retired-protected"]),
            Workflow("retired-protected", isRetired: true, subflows: ["retired-descendant"]),
            Workflow("retired-descendant", isRetired: true),
            Workflow("retired-parent", isRetired: true, subflows: ["retired-child"]),
            Workflow("retired-child", isRetired: true),
            Workflow("retired-leaf", isRetired: true));
        await dbContext.SaveChangesAsync();

        var runner = CreateRunner(dbContext);
        var result = await runner.DeleteUnreferencedRetiredObjectsAsync();

        result.DeletedWorkflows.Should().Be(3);
        var remainingKeys = await dbContext.Workflows
            .Select(workflow => workflow.Key)
            .OrderBy(key => key)
            .ToListAsync();
        remainingKeys.Should().Equal("active-root", "retired-descendant", "retired-protected");
    }

    [Fact]
    public async Task DeleteUnreferencedRetiredObjectsAsync_DeletesAgentsBeforeRoles()
    {
        await using var dbContext = CreateDbContext();
        var referencedRole = Role(101, "referenced-role", isRetired: true);
        var orphanedRole = Role(102, "orphaned-role", isRetired: true);
        var neverAssignedRole = Role(103, "never-assigned-role", isRetired: true);
        var systemRole = Role(104, "system-role", isRetired: true, isSystemManaged: true);

        dbContext.Workflows.Add(Workflow("active-root", isRetired: false, agentKeys: ["referenced-agent"]));
        dbContext.Agents.AddRange(
            Agent("referenced-agent", isRetired: true),
            Agent("unreferenced-agent", isRetired: true),
            Agent("active-agent", isRetired: false));
        dbContext.AgentRoles.AddRange(referencedRole, orphanedRole, neverAssignedRole, systemRole);
        dbContext.AgentRoleAssignments.AddRange(
            Assignment("referenced-agent", referencedRole),
            Assignment("unreferenced-agent", orphanedRole));
        await dbContext.SaveChangesAsync();

        var runner = CreateRunner(dbContext);
        var result = await runner.DeleteUnreferencedRetiredObjectsAsync();

        result.DeletedAgents.Should().Be(1);
        result.DeletedRoles.Should().Be(2);
        (await dbContext.Agents.AnyAsync(agent => agent.Key == "unreferenced-agent")).Should().BeFalse();
        (await dbContext.Agents.AnyAsync(agent => agent.Key == "referenced-agent")).Should().BeTrue();
        (await dbContext.AgentRoles.AnyAsync(role => role.Key == "referenced-role")).Should().BeTrue();
        (await dbContext.AgentRoles.AnyAsync(role => role.Key == "orphaned-role")).Should().BeFalse();
        (await dbContext.AgentRoles.AnyAsync(role => role.Key == "never-assigned-role")).Should().BeFalse();
        (await dbContext.AgentRoles.AnyAsync(role => role.Key == "system-role")).Should().BeTrue();
    }

    private static CodeFlowCleanupRunner CreateRunner(
        CodeFlowDbContext dbContext,
        string? workingDirectoryRoot = null)
    {
        return new CodeFlowCleanupRunner(
            dbContext,
            Options.Create(new WorkspaceOptions
            {
                WorkingDirectoryRoot = workingDirectoryRoot ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            }),
            NullLogger<CodeFlowCleanupRunner>.Instance,
            NullLoggerFactory.Instance);
    }

    private static CodeFlowDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
            .UseInMemoryDatabase($"cleanup-runner-tests-{Guid.NewGuid():N}")
            .Options;
        return new CodeFlowDbContext(options);
    }

    private static WorkflowSagaStateEntity Saga(
        Guid traceId,
        Guid correlationId,
        string currentState,
        DateTime updatedAtUtc)
    {
        return new WorkflowSagaStateEntity
        {
            TraceId = traceId,
            CorrelationId = correlationId,
            CurrentState = currentState,
            CurrentNodeId = Guid.NewGuid(),
            CurrentAgentKey = "agent-a",
            CurrentRoundId = Guid.NewGuid(),
            CurrentRoundEnteredAtUtc = updatedAtUtc,
            WorkflowKey = "workflow-a",
            WorkflowVersion = 1,
            CreatedAtUtc = updatedAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };
    }

    private static WorkflowEntity Workflow(
        string key,
        bool isRetired,
        IReadOnlyList<string>? subflows = null,
        IReadOnlyList<string>? agentKeys = null)
    {
        var nodes = new List<WorkflowNodeEntity>();
        if (subflows is not null)
        {
            nodes.AddRange(subflows.Select(subflow => new WorkflowNodeEntity
            {
                NodeId = Guid.NewGuid(),
                Kind = WorkflowNodeKind.Subflow,
                SubflowKey = subflow,
                SubflowVersion = 1,
            }));
        }
        if (agentKeys is not null)
        {
            nodes.AddRange(agentKeys.Select(agentKey => new WorkflowNodeEntity
            {
                NodeId = Guid.NewGuid(),
                Kind = WorkflowNodeKind.Agent,
                AgentKey = agentKey,
                AgentVersion = 1,
            }));
        }

        return new WorkflowEntity
        {
            Key = key,
            Version = 1,
            Name = key,
            MaxRoundsPerRound = 3,
            Category = WorkflowCategory.Workflow,
            IsRetired = isRetired,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes = nodes,
        };
    }

    private static AgentConfigEntity Agent(string key, bool isRetired)
    {
        return new AgentConfigEntity
        {
            Key = key,
            Version = 1,
            ConfigJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = !isRetired,
            IsRetired = isRetired,
        };
    }

    private static AgentRoleEntity Role(long id, string key, bool isRetired, bool isSystemManaged = false)
    {
        return new AgentRoleEntity
        {
            Id = id,
            Key = key,
            DisplayName = key,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            IsRetired = isRetired,
            IsSystemManaged = isSystemManaged,
        };
    }

    private static AgentRoleAssignmentEntity Assignment(string agentKey, AgentRoleEntity role)
    {
        return new AgentRoleAssignmentEntity
        {
            AgentKey = agentKey,
            Role = role,
            RoleId = role.Id,
            CreatedAtUtc = DateTime.UtcNow,
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
