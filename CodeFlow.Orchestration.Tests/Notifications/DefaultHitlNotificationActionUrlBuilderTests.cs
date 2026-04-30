using CodeFlow.Orchestration.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CodeFlow.Orchestration.Tests.Notifications;

public sealed class DefaultHitlNotificationActionUrlBuilderTests
{
    [Fact]
    public void BuildForPendingTask_ProducesQueryStringRouteWithTaskAndTraceIds()
    {
        var builder = new DefaultHitlNotificationActionUrlBuilder(Options.Create(new NotificationOptions
        {
            PublicBaseUrl = "https://codeflow.example.com",
        }));

        var traceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var url = builder.BuildForPendingTask(4242, traceId);

        url.ToString().Should().Be("https://codeflow.example.com/hitl?task=4242&trace=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    }

    [Fact]
    public void BuildForPendingTask_TolerantOfTrailingSlashOnBaseUrl()
    {
        var builder = new DefaultHitlNotificationActionUrlBuilder(Options.Create(new NotificationOptions
        {
            PublicBaseUrl = "https://codeflow.example.com/",
        }));

        var url = builder.BuildForPendingTask(7, Guid.Empty);

        url.ToString().Should().Be("https://codeflow.example.com/hitl?task=7&trace=00000000-0000-0000-0000-000000000000");
    }

    [Fact]
    public void BuildForPendingTask_WhenBaseUrlMissing_ThrowsInvalidOperation()
    {
        var builder = new DefaultHitlNotificationActionUrlBuilder(Options.Create(new NotificationOptions
        {
            PublicBaseUrl = null,
        }));

        Action act = () => builder.BuildForPendingTask(1, Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PublicBaseUrl is not configured*");
    }
}
