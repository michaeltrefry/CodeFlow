using CodeFlow.Api.Assistant.Idempotency;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

public sealed class AssistantTurnSignalRegistryTests
{
    [Fact]
    public async Task WaitAsync_completes_when_Signal_is_invoked()
    {
        var registry = new AssistantTurnSignalRegistry();
        var recordId = Guid.NewGuid();

        var waitTask = registry.WaitAsync(recordId, CancellationToken.None);
        waitTask.IsCompleted.Should().BeFalse();

        registry.Signal(recordId);

        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitAsync_completes_synchronously_when_Signal_arrived_first()
    {
        var registry = new AssistantTurnSignalRegistry();
        var recordId = Guid.NewGuid();

        registry.Signal(recordId);

        // A wait that arrives after the signal must not hang — pre-completed waiter already
        // present in the registry. One-second budget is generous; in practice this should
        // complete inside the test's first microsecond.
        await registry.WaitAsync(recordId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitAsync_throws_OperationCanceledException_when_token_cancelled()
    {
        var registry = new AssistantTurnSignalRegistry();
        var recordId = Guid.NewGuid();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await FluentActions.Invoking(() => registry.WaitAsync(recordId, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Signal_for_unrelated_records_does_not_complete_other_waiters()
    {
        var registry = new AssistantTurnSignalRegistry();
        var aimedAt = Guid.NewGuid();
        var otherRecord = Guid.NewGuid();

        var waitTask = registry.WaitAsync(aimedAt, CancellationToken.None);

        registry.Signal(otherRecord);

        // Brief settle window to make sure no spurious completion sneaks through.
        await Task.Delay(20);
        waitTask.IsCompleted.Should().BeFalse();

        registry.Signal(aimedAt);
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
