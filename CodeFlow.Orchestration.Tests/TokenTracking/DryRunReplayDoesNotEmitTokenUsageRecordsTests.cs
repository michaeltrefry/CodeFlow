using System.Reflection;
using CodeFlow.Orchestration.DryRun;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.TokenTracking;

/// <summary>
/// Architectural guarantee for slice 2: replay (DryRunExecutor) cannot emit
/// <see cref="TokenUsageRecord"/>s, because the DryRun path doesn't enter the InvocationLoop or
/// touch the capture observer at all. Encoded as a structural test on the DryRunExecutor
/// constructor — adding either dependency here would betray the slice 2 contract.
/// </summary>
public sealed class DryRunReplayDoesNotEmitTokenUsageRecordsTests
{
    [Fact]
    public void DryRunExecutor_ConstructorDoesNotDependOnTokenUsageOrInvocationObserver()
    {
        var captureRelatedTypes = new HashSet<Type>
        {
            typeof(ITokenUsageRecordRepository),
            typeof(IInvocationObserver),
            typeof(IAgentInvoker)
        };

        var dependencies = typeof(DryRunExecutor).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToHashSet();

        var leakedDependencies = dependencies.Where(captureRelatedTypes.Contains).ToArray();

        leakedDependencies.Should().BeEmpty(
            "DryRunExecutor must not gain a dependency on the token-usage capture surface or the "
            + "live agent-invocation surface; the replay path dequeues pre-recorded mocks and must "
            + "produce zero TokenUsageRecords by construction. If you genuinely need to wire one of "
            + "these here, the slice 2 'Replay path produces zero records' acceptance is being "
            + "renegotiated and this test should be updated alongside the design comment.");
    }
}
