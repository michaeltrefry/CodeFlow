using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class NotificationPersistenceTests : IAsyncLifetime
{
    private static readonly byte[] MasterKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly MariaDbContainer container = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_notifications")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private string? connectionString;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        connectionString = container.GetConnectionString();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await container.DisposeAsync();
    }

    [Fact]
    public async Task ProviderRepository_UpsertReplace_PersistsEncryptedCredential()
    {
        var providerId = $"slack-{Guid.NewGuid():N}";

        await using (var db = CreateContext())
        {
            using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
            var repo = new NotificationProviderConfigRepository(db, protector);

            await repo.UpsertAsync(new NotificationProviderUpsert(
                Id: providerId,
                DisplayName: "Slack — Production",
                Channel: NotificationChannel.Slack,
                EndpointUrl: "https://slack.com/api",
                FromAddress: null,
                Credential: new NotificationProviderCredentialUpdate(
                    NotificationProviderCredentialAction.Replace,
                    "xoxb-supersecret"),
                AdditionalConfigJson: "{\"workspace\":\"acme\"}",
                Enabled: true,
                UpdatedBy: "tester"));
        }

        await using (var db = CreateContext())
        {
            using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
            var repo = new NotificationProviderConfigRepository(db, protector);

            var read = await repo.GetAsync(providerId);
            read.Should().NotBeNull();
            read!.HasCredential.Should().BeTrue();
            read.Channel.Should().Be(NotificationChannel.Slack);
            read.EndpointUrl.Should().Be("https://slack.com/api");
            read.AdditionalConfigJson.Should().Be("{\"workspace\":\"acme\"}");
            read.Enabled.Should().BeTrue();
            read.IsArchived.Should().BeFalse();
            read.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc);

            var withSecret = await repo.GetWithDecryptedCredentialAsync(providerId);
            withSecret.Should().NotBeNull();
            withSecret!.PlaintextCredential.Should().Be("xoxb-supersecret");

            // Verify the on-disk bytes are not the plaintext.
            var raw = await db.NotificationProviders
                .AsNoTracking()
                .Where(p => p.Id == providerId)
                .Select(p => p.EncryptedCredential)
                .SingleAsync();
            raw.Should().NotBeNull();
            raw!.Length.Should().BeGreaterThan("xoxb-supersecret".Length);
        }
    }

    [Fact]
    public async Task ProviderRepository_UpsertPreserveAndClear_RespectCredentialActions()
    {
        var providerId = $"email-{Guid.NewGuid():N}";

        await using (var db = CreateContext())
        {
            using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
            var repo = new NotificationProviderConfigRepository(db, protector);

            await repo.UpsertAsync(new NotificationProviderUpsert(
                Id: providerId,
                DisplayName: "Email — Mailgun",
                Channel: NotificationChannel.Email,
                EndpointUrl: "smtp://mailgun:587",
                FromAddress: "ops@codeflow.example.com",
                Credential: new NotificationProviderCredentialUpdate(
                    NotificationProviderCredentialAction.Replace,
                    "key-original"),
                AdditionalConfigJson: null,
                Enabled: true,
                UpdatedBy: "tester"));

            // Preserve keeps the original credential intact even though the rest of the row mutates.
            await repo.UpsertAsync(new NotificationProviderUpsert(
                Id: providerId,
                DisplayName: "Email — Mailgun (renamed)",
                Channel: NotificationChannel.Email,
                EndpointUrl: "smtp://mailgun:2525",
                FromAddress: "ops@codeflow.example.com",
                Credential: new NotificationProviderCredentialUpdate(
                    NotificationProviderCredentialAction.Preserve,
                    null),
                AdditionalConfigJson: null,
                Enabled: true,
                UpdatedBy: "tester"));
        }

        await using (var db = CreateContext())
        {
            using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
            var repo = new NotificationProviderConfigRepository(db, protector);

            var withSecret = await repo.GetWithDecryptedCredentialAsync(providerId);
            withSecret.Should().NotBeNull();
            withSecret!.PlaintextCredential.Should().Be("key-original");
            withSecret.Config.DisplayName.Should().Be("Email — Mailgun (renamed)");

            await repo.UpsertAsync(new NotificationProviderUpsert(
                Id: providerId,
                DisplayName: "Email — Mailgun (renamed)",
                Channel: NotificationChannel.Email,
                EndpointUrl: "smtp://mailgun:2525",
                FromAddress: "ops@codeflow.example.com",
                Credential: new NotificationProviderCredentialUpdate(
                    NotificationProviderCredentialAction.Clear,
                    null),
                AdditionalConfigJson: null,
                Enabled: true,
                UpdatedBy: "tester"));

            var afterClear = await repo.GetAsync(providerId);
            afterClear!.HasCredential.Should().BeFalse();

            var clearedSecret = await repo.GetWithDecryptedCredentialAsync(providerId);
            clearedSecret!.PlaintextCredential.Should().BeNull();
        }
    }

    [Fact]
    public async Task ProviderRepository_Archive_DisablesAndExcludesFromDefaultList()
    {
        var providerId = $"sms-{Guid.NewGuid():N}";

        await using (var db = CreateContext())
        {
            using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
            var repo = new NotificationProviderConfigRepository(db, protector);

            await repo.UpsertAsync(new NotificationProviderUpsert(
                Id: providerId,
                DisplayName: "SMS — Twilio",
                Channel: NotificationChannel.Sms,
                EndpointUrl: null,
                FromAddress: "+15550001111",
                Credential: new NotificationProviderCredentialUpdate(
                    NotificationProviderCredentialAction.Replace,
                    "twilio-token"),
                AdditionalConfigJson: null,
                Enabled: true,
                UpdatedBy: null));

            await repo.ArchiveAsync(providerId, "tester");

            var defaultList = await repo.ListAsync();
            defaultList.Should().NotContain(p => p.Id == providerId);

            var includingArchived = await repo.ListAsync(includeArchived: true);
            var archived = includingArchived.SingleOrDefault(p => p.Id == providerId);
            archived.Should().NotBeNull();
            archived!.IsArchived.Should().BeTrue();
            archived.Enabled.Should().BeFalse();
        }
    }

    [Fact]
    public async Task RouteRepository_Upsert_RoundTripsRecipientsAndDispatchesByEventKind()
    {
        var providerId = $"slack-{Guid.NewGuid():N}";

        await using (var db = CreateContext())
        {
            using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
            var providerRepo = new NotificationProviderConfigRepository(db, protector);
            await providerRepo.UpsertAsync(new NotificationProviderUpsert(
                Id: providerId,
                DisplayName: "Slack — Test",
                Channel: NotificationChannel.Slack,
                EndpointUrl: null,
                FromAddress: null,
                Credential: new NotificationProviderCredentialUpdate(
                    NotificationProviderCredentialAction.Clear,
                    null),
                AdditionalConfigJson: null,
                Enabled: true,
                UpdatedBy: null));

            var routeRepo = new NotificationRouteRepository(db);
            await routeRepo.UpsertAsync(new NotificationRoute(
                RouteId: "route-hitl-slack",
                EventKind: NotificationEventKind.HitlTaskPending,
                ProviderId: providerId,
                Recipients:
                [
                    new NotificationRecipient(NotificationChannel.Slack, "C012AB3CD", "#hitl-queue"),
                    new NotificationRecipient(NotificationChannel.Slack, "C99XY", "#oncall")
                ],
                Template: new NotificationTemplateRef("hitl-task-pending/slack/default", 2),
                MinimumSeverity: NotificationSeverity.High,
                Enabled: true));

            // Disabled route — should be excluded from ListByEventKindAsync.
            await routeRepo.UpsertAsync(new NotificationRoute(
                RouteId: "route-hitl-slack-disabled",
                EventKind: NotificationEventKind.HitlTaskPending,
                ProviderId: providerId,
                Recipients: [new NotificationRecipient(NotificationChannel.Slack, "C-NO", null)],
                Template: new NotificationTemplateRef("hitl-task-pending/slack/default", 2),
                MinimumSeverity: NotificationSeverity.Info,
                Enabled: false));
        }

        await using (var db = CreateContext())
        {
            var routeRepo = new NotificationRouteRepository(db);
            var routes = await routeRepo.ListByEventKindAsync(NotificationEventKind.HitlTaskPending);

            routes.Should().ContainSingle();
            var route = routes[0];
            route.RouteId.Should().Be("route-hitl-slack");
            route.Recipients.Should().HaveCount(2);
            route.Recipients[0].Address.Should().Be("C012AB3CD");
            route.Recipients[0].DisplayName.Should().Be("#hitl-queue");
            route.MinimumSeverity.Should().Be(NotificationSeverity.High);
            route.Template.TemplateId.Should().Be("hitl-task-pending/slack/default");
            route.Template.Version.Should().Be(2);
        }
    }

    [Fact]
    public async Task TemplateRepository_PublishSameContent_IsIdempotent()
    {
        const string templateId = "hitl-task-pending/email/default";

        await using var db = CreateContext();
        var repo = new NotificationTemplateRepository(db);

        var v1 = await repo.PublishAsync(new NotificationTemplateUpsert(
            TemplateId: templateId,
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Email,
            SubjectTemplate: "[CodeFlow] HITL review needed",
            BodyTemplate: "Body v1: open {{ action_url }}",
            UpdatedBy: "tester"));

        v1.Version.Should().Be(1);

        var sameAgain = await repo.PublishAsync(new NotificationTemplateUpsert(
            TemplateId: templateId,
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Email,
            SubjectTemplate: "[CodeFlow] HITL review needed",
            BodyTemplate: "Body v1: open {{ action_url }}",
            UpdatedBy: "tester"));

        sameAgain.Version.Should().Be(1);

        var v2 = await repo.PublishAsync(new NotificationTemplateUpsert(
            TemplateId: templateId,
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Email,
            SubjectTemplate: "[CodeFlow] HITL review needed",
            BodyTemplate: "Body v2 — clearer copy. Open {{ action_url }}",
            UpdatedBy: "tester"));

        v2.Version.Should().Be(2);

        var versions = await repo.ListVersionsAsync(templateId);
        versions.Should().HaveCount(2);
        versions[0].Version.Should().Be(2); // descending
        versions[1].Version.Should().Be(1);

        var latest = await repo.GetLatestAsync(templateId);
        latest!.Version.Should().Be(2);
        latest.BodyTemplate.Should().Contain("Body v2");
    }

    [Fact]
    public async Task DeliveryAttemptRepository_RecordsAttempts_InOrder()
    {
        var eventId = Guid.NewGuid();

        await using (var db = CreateContext())
        {
            var repo = new NotificationDeliveryAttemptRepository(db);

            await repo.RecordAsync(new NotificationDeliveryResult(
                EventId: eventId,
                RouteId: "route-1",
                ProviderId: "slack-prod",
                Status: NotificationDeliveryStatus.Failed,
                AttemptedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-2),
                CompletedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-1),
                AttemptNumber: 1,
                NormalizedDestination: "C012AB3CD",
                ProviderMessageId: null,
                ErrorCode: "slack.timeout",
                ErrorMessage: "request timed out"
            ), NotificationEventKind.HitlTaskPending);

            await repo.RecordAsync(new NotificationDeliveryResult(
                EventId: eventId,
                RouteId: "route-1",
                ProviderId: "slack-prod",
                Status: NotificationDeliveryStatus.Sent,
                AttemptedAtUtc: DateTimeOffset.UtcNow,
                CompletedAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(40),
                AttemptNumber: 2,
                NormalizedDestination: "C012AB3CD",
                ProviderMessageId: "1234567890.000200",
                ErrorCode: null,
                ErrorMessage: null
            ), NotificationEventKind.HitlTaskPending);
        }

        await using (var db = CreateContext())
        {
            var repo = new NotificationDeliveryAttemptRepository(db);

            var attempts = await repo.ListByEventIdAsync(eventId);
            attempts.Should().HaveCount(2);
            attempts[0].AttemptNumber.Should().Be(1);
            attempts[0].Status.Should().Be(NotificationDeliveryStatus.Failed);
            attempts[1].AttemptNumber.Should().Be(2);
            attempts[1].Status.Should().Be(NotificationDeliveryStatus.Sent);

            var latest = await repo.LatestForDestinationAsync(eventId, "slack-prod", "C012AB3CD");
            latest.Should().NotBeNull();
            latest!.AttemptNumber.Should().Be(2);
            latest.Status.Should().Be(NotificationDeliveryStatus.Sent);
            latest.ProviderMessageId.Should().Be("1234567890.000200");
        }
    }

    [Fact]
    public async Task DeliveryAttemptRepository_RejectsDuplicateOnUniqueIdempotencyIndex()
    {
        var eventId = Guid.NewGuid();
        var result = new NotificationDeliveryResult(
            EventId: eventId,
            RouteId: "route-1",
            ProviderId: "slack-prod",
            Status: NotificationDeliveryStatus.Sent,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(40),
            AttemptNumber: 1,
            NormalizedDestination: "C012AB3CD",
            ProviderMessageId: "ts-1",
            ErrorCode: null,
            ErrorMessage: null);

        await using (var db = CreateContext())
        {
            var repo = new NotificationDeliveryAttemptRepository(db);
            await repo.RecordAsync(result, NotificationEventKind.HitlTaskPending);
        }

        await using (var db = CreateContext())
        {
            var repo = new NotificationDeliveryAttemptRepository(db);
            Func<Task> duplicate = () => repo.RecordAsync(result, NotificationEventKind.HitlTaskPending);
            await duplicate.Should().ThrowAsync<DbUpdateException>(
                "the unique index on (event_id, provider_id, normalized_destination, attempt_number) " +
                "must reject a duplicate row even when the dispatcher races itself");
        }
    }

    private CodeFlowDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
