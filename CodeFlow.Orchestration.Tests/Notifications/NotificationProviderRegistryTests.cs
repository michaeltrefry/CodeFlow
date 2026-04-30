using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Orchestration.Tests.Notifications;

public sealed class NotificationProviderRegistryTests
{
    [Fact]
    public async Task GetByIdAsync_ReturnsStaticProviderWhenRegistered()
    {
        var staticProvider = new StubProvider("slack-test", NotificationChannel.Slack);
        var registry = new NotificationProviderRegistry(
            staticProviders: new[] { (INotificationProvider)staticProvider },
            factories: Array.Empty<INotificationProviderFactory>());

        var resolved = await registry.GetByIdAsync("slack-test");

        resolved.Should().BeSameAs(staticProvider);
    }

    [Fact]
    public async Task GetByIdAsync_FallsThroughToFactoryWhenStaticMisses()
    {
        var configRepo = new InMemoryConfigRepo();
        configRepo.Add("slack-prod", NotificationChannel.Slack, plaintextCredential: "xoxb-real");

        var factory = new RecordingFactory(NotificationChannel.Slack);
        var registry = new NotificationProviderRegistry(
            staticProviders: Array.Empty<INotificationProvider>(),
            factories: new[] { (INotificationProviderFactory)factory },
            configRepository: configRepo);

        var resolved = await registry.GetByIdAsync("slack-prod");

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be("slack-prod");
        factory.CreateCalls.Should().HaveCount(1);
        factory.CreateCalls[0].Config.Id.Should().Be("slack-prod");
        factory.CreateCalls[0].PlaintextCredential.Should().Be("xoxb-real");
    }

    [Fact]
    public async Task GetByIdAsync_CachesResolvedFactoryProvider()
    {
        var configRepo = new InMemoryConfigRepo();
        configRepo.Add("slack-prod", NotificationChannel.Slack, plaintextCredential: "xoxb-real");

        var factory = new RecordingFactory(NotificationChannel.Slack);
        var registry = new NotificationProviderRegistry(
            staticProviders: Array.Empty<INotificationProvider>(),
            factories: new[] { (INotificationProviderFactory)factory },
            configRepository: configRepo);

        var first = await registry.GetByIdAsync("slack-prod");
        var second = await registry.GetByIdAsync("slack-prod");

        first.Should().BeSameAs(second);
        factory.CreateCalls.Should().HaveCount(1, "the registry caches the factory result for the lifetime of the scope");
        configRepo.LookupCount.Should().Be(1, "the config repo is only consulted on cache miss");
    }

    [Fact]
    public async Task GetByIdAsync_TreatsArchivedRowAsMissing()
    {
        var configRepo = new InMemoryConfigRepo();
        configRepo.Add("slack-old", NotificationChannel.Slack, plaintextCredential: "xoxb-old", archived: true);

        var factory = new RecordingFactory(NotificationChannel.Slack);
        var registry = new NotificationProviderRegistry(
            staticProviders: Array.Empty<INotificationProvider>(),
            factories: new[] { (INotificationProviderFactory)factory },
            configRepository: configRepo);

        var resolved = await registry.GetByIdAsync("slack-old");

        resolved.Should().BeNull();
        factory.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByIdAsync_TreatsDisabledRowAsMissing()
    {
        var configRepo = new InMemoryConfigRepo();
        configRepo.Add("slack-paused", NotificationChannel.Slack, plaintextCredential: "xoxb", enabled: false);

        var factory = new RecordingFactory(NotificationChannel.Slack);
        var registry = new NotificationProviderRegistry(
            staticProviders: Array.Empty<INotificationProvider>(),
            factories: new[] { (INotificationProviderFactory)factory },
            configRepository: configRepo);

        var resolved = await registry.GetByIdAsync("slack-paused");

        resolved.Should().BeNull();
        factory.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByIdAsync_WhenNoFactoryForChannel_ReturnsNull()
    {
        var configRepo = new InMemoryConfigRepo();
        configRepo.Add("email-prod", NotificationChannel.Email, plaintextCredential: "smtp-key");

        var slackOnly = new RecordingFactory(NotificationChannel.Slack);
        var registry = new NotificationProviderRegistry(
            staticProviders: Array.Empty<INotificationProvider>(),
            factories: new[] { (INotificationProviderFactory)slackOnly },
            configRepository: configRepo,
            logger: NullLogger<NotificationProviderRegistry>.Instance);

        var resolved = await registry.GetByIdAsync("email-prod");

        resolved.Should().BeNull();
    }

    [Fact]
    public void Constructor_RejectsDuplicateProviderIds()
    {
        Action act = () => new NotificationProviderRegistry(
            staticProviders: new[]
            {
                (INotificationProvider)new StubProvider("dup", NotificationChannel.Slack),
                new StubProvider("dup", NotificationChannel.Slack)
            },
            factories: Array.Empty<INotificationProviderFactory>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*registered with id 'dup'*");
    }

    [Fact]
    public void Constructor_RejectsDuplicateFactoryChannels()
    {
        Action act = () => new NotificationProviderRegistry(
            staticProviders: Array.Empty<INotificationProvider>(),
            factories: new[]
            {
                (INotificationProviderFactory)new RecordingFactory(NotificationChannel.Slack),
                new RecordingFactory(NotificationChannel.Slack)
            });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*channel Slack*");
    }

    private sealed class StubProvider(string id, NotificationChannel channel) : INotificationProvider
    {
        public string Id { get; } = id;
        public NotificationChannel Channel { get; } = channel;

        public Task<NotificationDeliveryResult> SendAsync(
            NotificationMessage message, NotificationRoute route, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<ProviderValidationResult> ValidateAsync(CancellationToken ct = default) =>
            Task.FromResult(ProviderValidationResult.Valid());
    }

    private sealed class RecordingFactory(NotificationChannel channel) : INotificationProviderFactory
    {
        public NotificationChannel Channel { get; } = channel;
        public List<NotificationProviderConfigWithCredential> CreateCalls { get; } = new();

        public Task<INotificationProvider> CreateAsync(
            NotificationProviderConfigWithCredential config,
            CancellationToken cancellationToken = default)
        {
            CreateCalls.Add(config);
            return Task.FromResult<INotificationProvider>(
                new StubProvider(config.Config.Id, config.Config.Channel));
        }
    }

    private sealed class InMemoryConfigRepo : INotificationProviderConfigRepository
    {
        private readonly Dictionary<string, NotificationProviderConfigWithCredential> rows = new();
        public int LookupCount { get; private set; }

        public void Add(
            string id,
            NotificationChannel channel,
            string? plaintextCredential = null,
            bool enabled = true,
            bool archived = false)
        {
            var now = DateTime.UtcNow;
            rows[id] = new NotificationProviderConfigWithCredential(
                Config: new NotificationProviderConfig(
                    Id: id,
                    DisplayName: id,
                    Channel: channel,
                    EndpointUrl: null,
                    FromAddress: null,
                    HasCredential: plaintextCredential is not null,
                    AdditionalConfigJson: null,
                    Enabled: enabled,
                    IsArchived: archived,
                    CreatedAtUtc: now,
                    CreatedBy: null,
                    UpdatedAtUtc: now,
                    UpdatedBy: null),
                PlaintextCredential: plaintextCredential);
        }

        public Task<IReadOnlyList<NotificationProviderConfig>> ListAsync(bool includeArchived = false, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NotificationProviderConfig?> GetAsync(string providerId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NotificationProviderConfigWithCredential?> GetWithDecryptedCredentialAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            LookupCount++;
            return Task.FromResult(rows.TryGetValue(providerId, out var v) ? v : null);
        }

        public Task UpsertAsync(NotificationProviderUpsert upsert, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ArchiveAsync(string providerId, string? archivedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
