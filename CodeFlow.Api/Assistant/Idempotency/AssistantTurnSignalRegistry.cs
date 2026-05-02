using System.Collections.Concurrent;

namespace CodeFlow.Api.Assistant.Idempotency;

/// <summary>
/// sc-525 — Per-process fast path for in-flight idempotency-key duplicates. Lets a duplicate
/// request waiting for an originating turn on this same API instance unblock the moment the
/// originating recorder flushes, instead of waiting on the next DB poll. Cross-instance
/// duplicates fall back to DB polling and never touch this registry.
/// </summary>
public sealed class AssistantTurnSignalRegistry
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> waiters = new();

    /// <summary>
    /// Returns a task that completes when <see cref="Signal"/> is invoked for
    /// <paramref name="recordId"/>. The token cancels the wait without affecting other
    /// waiters on the same record.
    /// </summary>
    public Task WaitAsync(Guid recordId, CancellationToken cancellationToken)
    {
        var tcs = waiters.GetOrAdd(
            recordId,
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        return tcs.Task.WaitAsync(cancellationToken);
    }

    public void Signal(Guid recordId)
    {
        if (waiters.TryRemove(recordId, out var tcs))
        {
            tcs.TrySetResult(true);
        }
        else
        {
            // No waiters yet — leave a pre-completed entry so a future WaitAsync returns
            // synchronously instead of hanging until the next DB poll.
            var precompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            precompleted.SetResult(true);
            waiters.TryAdd(recordId, precompleted);
        }
    }
}
