using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Orchestration.Tests.Notifications;

public sealed class HitlTaskPendingEventNotificationConsumerTests
{
    [Fact]
    public async Task Consumer_OnHitlTaskPendingEvent_DispatchesViaINotificationDispatcher()
    {
        var dispatcher = new RecordingDispatcher();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationDispatcher>(dispatcher)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<HitlTaskPendingEventNotificationConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var evt = new HitlTaskPendingEvent(
                EventId: Guid.NewGuid(),
                OccurredAtUtc: DateTimeOffset.UtcNow,
                ActionUrl: new Uri("https://codeflow.example.com/hitl?task=1"),
                Severity: NotificationSeverity.High,
                HitlTaskId: 1,
                TraceId: Guid.NewGuid(),
                RoundId: Guid.NewGuid(),
                NodeId: Guid.NewGuid(),
                WorkflowKey: "wf",
                WorkflowVersion: 1,
                AgentKey: "agent",
                AgentVersion: 1,
                HitlTaskCreatedAtUtc: DateTimeOffset.UtcNow);

            await harness.Bus.Publish(evt);

            (await harness.Consumed.Any<HitlTaskPendingEvent>()).Should().BeTrue();

            dispatcher.DispatchedEvents.Should().ContainSingle();
            var dispatched = (HitlTaskPendingEvent)dispatcher.DispatchedEvents[0];
            dispatched.EventId.Should().Be(evt.EventId);
            dispatched.HitlTaskId.Should().Be(1);
            dispatched.ActionUrl.Should().Be(evt.ActionUrl);
        }
        finally
        {
            await harness.Stop();
        }
    }

    private sealed class RecordingDispatcher : INotificationDispatcher
    {
        public List<INotificationEvent> DispatchedEvents { get; } = new();

        public Task<IReadOnlyList<NotificationDeliveryResult>> DispatchAsync(
            INotificationEvent notificationEvent,
            CancellationToken cancellationToken = default)
        {
            DispatchedEvents.Add(notificationEvent);
            return Task.FromResult<IReadOnlyList<NotificationDeliveryResult>>(Array.Empty<NotificationDeliveryResult>());
        }
    }
}
