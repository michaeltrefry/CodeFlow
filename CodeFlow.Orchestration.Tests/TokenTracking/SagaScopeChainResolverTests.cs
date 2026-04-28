using CodeFlow.Orchestration.TokenTracking;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Orchestration.Tests.TokenTracking;

public sealed class SagaScopeChainResolverTests
{
    [Fact]
    public async Task ResolveAsync_TopLevelSaga_ReturnsItselfAsRootWithEmptyScopeChain()
    {
        await using var dbContext = NewInMemoryDbContext();
        var topLevelTraceId = Guid.NewGuid();
        dbContext.WorkflowSagas.Add(NewSagaState(traceId: topLevelTraceId, parentTraceId: null));
        await dbContext.SaveChangesAsync();

        var scope = await SagaScopeChainResolver.ResolveAsync(dbContext, topLevelTraceId, CancellationToken.None);

        scope.RootTraceId.Should().Be(topLevelTraceId);
        scope.ScopeChain.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_OneLevelNestedSaga_ReturnsParentAsRootAndCurrentAsScopeChain()
    {
        await using var dbContext = NewInMemoryDbContext();
        var rootTraceId = Guid.NewGuid();
        var subflowTraceId = Guid.NewGuid();
        dbContext.WorkflowSagas.Add(NewSagaState(traceId: rootTraceId, parentTraceId: null));
        dbContext.WorkflowSagas.Add(NewSagaState(traceId: subflowTraceId, parentTraceId: rootTraceId));
        await dbContext.SaveChangesAsync();

        var scope = await SagaScopeChainResolver.ResolveAsync(dbContext, subflowTraceId, CancellationToken.None);

        scope.RootTraceId.Should().Be(rootTraceId);
        scope.ScopeChain.Should().Equal(subflowTraceId);
    }

    [Fact]
    public async Task ResolveAsync_TwoLevelNestedSaga_BuildsScopeChainRootToLeafExcludingRoot()
    {
        // Root → subflow (level 1) → review-loop iteration (level 2) → originating node.
        // scopeChain must be ordered root→leaf and exclude root itself, so slices 6/7/8 can
        // roll up totals at any scope by filtering the JSON array for any matching scope id.
        await using var dbContext = NewInMemoryDbContext();
        var rootTraceId = Guid.NewGuid();
        var level1TraceId = Guid.NewGuid();
        var level2TraceId = Guid.NewGuid();
        dbContext.WorkflowSagas.Add(NewSagaState(traceId: rootTraceId, parentTraceId: null));
        dbContext.WorkflowSagas.Add(NewSagaState(traceId: level1TraceId, parentTraceId: rootTraceId));
        dbContext.WorkflowSagas.Add(NewSagaState(traceId: level2TraceId, parentTraceId: level1TraceId));
        await dbContext.SaveChangesAsync();

        var scope = await SagaScopeChainResolver.ResolveAsync(dbContext, level2TraceId, CancellationToken.None);

        scope.RootTraceId.Should().Be(rootTraceId);
        scope.ScopeChain.Should().Equal(level1TraceId, level2TraceId);
    }

    [Fact]
    public async Task ResolveAsync_WhenSagaRowMissing_TreatsCurrentAsRootAndReturnsEmptyChain()
    {
        // Edge case: agent invocation begins before the saga row commits, or a unit-test
        // scenario where the saga wasn't seeded. Resolver must not throw and must not drop
        // the only TraceId we have — we'd lose the record. Better to attribute it to the
        // best-known TraceId than to crash the consumer.
        await using var dbContext = NewInMemoryDbContext();
        var orphanTraceId = Guid.NewGuid();

        var scope = await SagaScopeChainResolver.ResolveAsync(dbContext, orphanTraceId, CancellationToken.None);

        scope.RootTraceId.Should().Be(orphanTraceId);
        scope.ScopeChain.Should().BeEmpty();
    }

    private static CodeFlowDbContext NewInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
            .UseInMemoryDatabase($"saga-scope-resolver-{Guid.NewGuid():N}")
            .Options;
        return new CodeFlowDbContext(options);
    }

    private static WorkflowSagaStateEntity NewSagaState(Guid traceId, Guid? parentTraceId)
    {
        return new WorkflowSagaStateEntity
        {
            CorrelationId = Guid.NewGuid(),
            TraceId = traceId,
            CurrentState = "Active",
            CurrentNodeId = Guid.NewGuid(),
            CurrentRoundId = Guid.NewGuid(),
            WorkflowKey = "test-workflow",
            WorkflowVersion = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            ParentTraceId = parentTraceId
        };
    }
}
