using Testcontainers.RabbitMq;

namespace CodeFlow.Api.Tests.DeadLetter;

/// <summary>
/// xunit class-scoped fixture that owns ONE RabbitMQ Testcontainer for the
/// <see cref="RabbitMqDlqRetryTransportIntegrationTests"/> class (sc-699 Phase B Api).
/// Replaces the previous per-test <c>IAsyncLifetime</c> on the test class itself, which was
/// creating a fresh broker for every test method (~3s × 2 tests = ~6s of container churn).
/// Tests use distinct queue names so they don't collide on a shared broker.
/// </summary>
public sealed class RabbitMqIntegrationFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } = new RabbitMqBuilder("rabbitmq:4.0-management")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
