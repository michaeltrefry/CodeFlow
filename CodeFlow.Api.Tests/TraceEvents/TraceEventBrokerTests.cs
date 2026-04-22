using CodeFlow.Api.TraceEvents;
using FluentAssertions;

namespace CodeFlow.Api.Tests.TraceEvents;

public sealed class TraceEventBrokerTests
{
    [Fact]
    public async Task Subscribe_FiltersByTraceId()
    {
        var broker = new TraceEventBroker();
        var traceId = Guid.NewGuid();
        var otherTrace = Guid.NewGuid();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<TraceEvent>();

        var consumer = Task.Run(async () =>
        {
            await foreach (var evt in broker.SubscribeAsync(traceId, cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 2)
                {
                    break;
                }
            }
        }, cts.Token);

        await Task.Delay(50, cts.Token);

        await broker.PublishAsync(Make(traceId, TraceEventKind.Requested), cts.Token);
        await broker.PublishAsync(Make(otherTrace, TraceEventKind.Requested), cts.Token);
        await broker.PublishAsync(Make(traceId, TraceEventKind.Completed), cts.Token);

        await consumer;

        received.Should().HaveCount(2);
        received[0].TraceId.Should().Be(traceId);
        received[1].TraceId.Should().Be(traceId);
        received[0].Kind.Should().Be(TraceEventKind.Requested);
        received[1].Kind.Should().Be(TraceEventKind.Completed);
    }

    [Fact]
    public async Task Subscribe_ReleasesOnCancel()
    {
        var broker = new TraceEventBroker();
        var traceId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in broker.SubscribeAsync(traceId, cts.Token))
            {
            }
        });

        await Task.Delay(50);
        cts.Cancel();

        var completed = await Task.WhenAny(consumer, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(consumer);
    }

    [Fact]
    public async Task Publish_ToOtherTrace_IsNoOpWhenNoSubscribersOnThatTrace()
    {
        // Partitioning by TraceId: publishing an event for a trace with no subscribers must not
        // touch any other trace's channel, regardless of subscriber count.
        var broker = new TraceEventBroker();
        var watchedTrace = Guid.NewGuid();
        var unrelatedTrace = Guid.NewGuid();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<TraceEvent>();

        var consumer = Task.Run(async () =>
        {
            await foreach (var evt in broker.SubscribeAsync(watchedTrace, cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 1) break;
            }
        }, cts.Token);

        await Task.Delay(50, cts.Token);

        // 1000 events on the unrelated trace must not block or fill any buffer for watchedTrace.
        for (var i = 0; i < 1000; i++)
        {
            await broker.PublishAsync(Make(unrelatedTrace, TraceEventKind.Requested), cts.Token);
        }

        await broker.PublishAsync(Make(watchedTrace, TraceEventKind.Completed), cts.Token);

        await consumer;
        received.Should().ContainSingle().Which.TraceId.Should().Be(watchedTrace);
    }

    private static TraceEvent Make(Guid traceId, TraceEventKind kind)
    {
        return new TraceEvent(
            TraceId: traceId,
            RoundId: Guid.NewGuid(),
            Kind: kind,
            AgentKey: "reviewer",
            AgentVersion: 1,
            OutputRef: null,
            InputRef: null,
            Decision: null,
            DecisionPayload: null,
            TimestampUtc: DateTimeOffset.UtcNow);
    }
}
