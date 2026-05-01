using CodeFlow.Api;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CodeFlow.Api.Tests.Integration;

/// <summary>
/// Helpers for the sc-60 authorization tests. Lets a single test customize the principal the
/// dev-bypass auth handler asserts (default is Admin via <c>CodeFlowApiFactory</c>) by
/// post-configuring <see cref="CodeFlow.Api.Auth.AuthOptions.DevelopmentRoles"/> on a derived
/// factory. Uses <c>PostConfigure</c> rather than configuration overrides because the config
/// binder appends list entries instead of replacing them, which would leave the default Admin
/// role in the list and defeat the test.
/// </summary>
internal static class NotificationsTestFactoryExtensions
{
    public static WebApplicationFactory<Program> WithRoles(this WebApplicationFactory<Program> factory, params string[] roles)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<CodeFlow.Api.Auth.AuthOptions>(options =>
                {
                    options.DevelopmentRoles = roles.ToList();
                });
            });
        });
    }
}

[Trait("Category", "EndToEnd")]
public sealed class NotificationsEndpointsTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public NotificationsEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    public async Task InitializeAsync()
    {
        // CodeFlowApiFactory is a shared class fixture; rows leak between tests. Reset every
        // notification table so each test sees a clean state.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        await db.NotificationDeliveryAttempts.ExecuteDeleteAsync();
        await db.NotificationRoutes.ExecuteDeleteAsync();
        await db.NotificationTemplates.ExecuteDeleteAsync();
        await db.NotificationProviders.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListProviders_WhenEmpty_ReturnsEmptyArray()
    {
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<JsonElement[]>("/api/admin/notification-providers");

        response.Should().NotBeNull();
        response!.Should().BeEmpty();
    }

    [Fact]
    public async Task PutProvider_RoundTrips_AndDoesNotLeakCredential()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack — Production",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = """{"workspace":"acme"}""",
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-leaks-must-not-appear-in-response" },
        });
        put.EnsureSuccessStatusCode();

        var rawPut = await put.Content.ReadAsStringAsync();
        rawPut.Should().NotContain("xoxb-leaks-must-not-appear-in-response",
            "credential plaintext must never appear in a list/get response");

        var rawGet = await client.GetStringAsync("/api/admin/notification-providers");
        rawGet.Should().NotContain("xoxb-leaks-must-not-appear-in-response");

        var providers = (await client.GetFromJsonAsync<JsonElement[]>("/api/admin/notification-providers"))!;
        providers.Should().ContainSingle();
        var provider = providers[0];
        provider.GetProperty("id").GetString().Should().Be("slack-prod");
        provider.GetProperty("displayName").GetString().Should().Be("Slack — Production");
        provider.GetProperty("channel").GetString().Should().Be("Slack");
        provider.GetProperty("hasCredential").GetBoolean().Should().BeTrue();
        provider.GetProperty("enabled").GetBoolean().Should().BeTrue();
        provider.GetProperty("isArchived").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PutProvider_RejectsReplaceWithEmptyCredentialValue()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack — Production",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "" },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutProvider_RejectsUnspecifiedChannel()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/notification-providers/p1", new
        {
            displayName = "Bad",
            channel = 0, // Unspecified
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Preserve", value = (string?)null },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteProvider_ArchivesAndExcludesFromDefaultList()
    {
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-old", new
        {
            displayName = "Slack — Old",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-old" },
        })).EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync("/api/admin/notification-providers/slack-old");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var defaultList = (await client.GetFromJsonAsync<JsonElement[]>("/api/admin/notification-providers"))!;
        defaultList.Should().BeEmpty();

        var includingArchived = (await client.GetFromJsonAsync<JsonElement[]>(
            "/api/admin/notification-providers?includeArchived=true"))!;
        includingArchived.Should().ContainSingle();
        includingArchived[0].GetProperty("isArchived").GetBoolean().Should().BeTrue();
        includingArchived[0].GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DeleteProvider_ReturnsNotFoundWhenAbsent()
    {
        using var client = factory.CreateClient();
        var response = await client.DeleteAsync("/api/admin/notification-providers/never-existed");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutRoute_WhenProviderMissing_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/notification-routes/route-orphan", new
        {
            eventKind = "HitlTaskPending",
            providerId = "no-such-provider",
            recipients = new[]
            {
                new { channel = "Slack", address = "C012", displayName = (string?)null }
            },
            template = new { templateId = "hitl-task-pending/slack/default", version = 1 },
            minimumSeverity = "Normal",
            enabled = true,
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await put.Content.ReadAsStringAsync();
        body.Should().Contain("no-such-provider");
    }

    [Fact]
    public async Task PutRoute_WhenRecipientChannelMismatchesProvider_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();

        // Create a Slack provider, then try to attach an Email recipient — mismatch must fail.
        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-test" },
        })).EnsureSuccessStatusCode();

        var put = await client.PutAsJsonAsync("/api/admin/notification-routes/route-mismatch", new
        {
            eventKind = "HitlTaskPending",
            providerId = "slack-prod",
            recipients = new[]
            {
                new { channel = "Email", address = "ops@example.com", displayName = (string?)null }
            },
            template = new { templateId = "tmpl/slack", version = 1 },
            minimumSeverity = "Normal",
            enabled = true,
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await put.Content.ReadAsStringAsync();
        body.Should().Contain("Email");
        body.Should().Contain("Slack");
    }

    [Fact]
    public async Task PutRoute_RoundTripsViaList()
    {
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-test" },
        })).EnsureSuccessStatusCode();

        var put = await client.PutAsJsonAsync("/api/admin/notification-routes/route-prod", new
        {
            eventKind = "HitlTaskPending",
            providerId = "slack-prod",
            recipients = new[]
            {
                new { channel = "Slack", address = "C012AB3CD", displayName = "#hitl-queue" }
            },
            template = new { templateId = "hitl-task-pending/slack/default", version = 2 },
            minimumSeverity = "High",
            enabled = true,
        });
        put.EnsureSuccessStatusCode();

        var routes = (await client.GetFromJsonAsync<JsonElement[]>("/api/admin/notification-routes"))!;
        routes.Should().ContainSingle();
        var route = routes[0];
        route.GetProperty("routeId").GetString().Should().Be("route-prod");
        route.GetProperty("providerId").GetString().Should().Be("slack-prod");
        route.GetProperty("minimumSeverity").GetString().Should().Be("High");
        route.GetProperty("template").GetProperty("version").GetInt32().Should().Be(2);
        route.GetProperty("recipients").GetArrayLength().Should().Be(1);
        route.GetProperty("recipients")[0].GetProperty("displayName").GetString().Should().Be("#hitl-queue");
    }

    [Fact]
    public async Task DeleteRoute_RemovesRow()
    {
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-test" },
        })).EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/admin/notification-routes/route-doomed", new
        {
            eventKind = "HitlTaskPending",
            providerId = "slack-prod",
            recipients = new[] { new { channel = "Slack", address = "C012", displayName = (string?)null } },
            template = new { templateId = "tmpl/slack", version = 1 },
            minimumSeverity = "Normal",
            enabled = true,
        })).EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync("/api/admin/notification-routes/route-doomed");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var routes = (await client.GetFromJsonAsync<JsonElement[]>("/api/admin/notification-routes"))!;
        routes.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTemplates_WithoutTemplateIdQuery_ReturnsValidationProblem()
    {
        // sc-57 only supports per-template-id history listings; a full inventory listing lands
        // in sc-63 with the template editor. Make sure the contract is explicit so the UI knows
        // to scope its query.
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/admin/notification-templates");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListTemplates_WithTemplateIdQuery_ReturnsVersionsDescending()
    {
        // Seed two versions of one template directly through the repository — the template
        // editor (sc-63) will eventually expose a publish endpoint, but sc-57 only reads.
        await using var scope = factory.Services.CreateAsyncScope();
        var templates = scope.ServiceProvider.GetRequiredService<INotificationTemplateRepository>();
        await templates.PublishAsync(new NotificationTemplateUpsert(
            TemplateId: "hitl-task-pending/email/default",
            EventKind: CodeFlow.Contracts.Notifications.NotificationEventKind.HitlTaskPending,
            Channel: CodeFlow.Contracts.Notifications.NotificationChannel.Email,
            SubjectTemplate: "v1 subject",
            BodyTemplate: "v1 body",
            UpdatedBy: "test"));
        await templates.PublishAsync(new NotificationTemplateUpsert(
            TemplateId: "hitl-task-pending/email/default",
            EventKind: CodeFlow.Contracts.Notifications.NotificationEventKind.HitlTaskPending,
            Channel: CodeFlow.Contracts.Notifications.NotificationChannel.Email,
            SubjectTemplate: "v2 subject",
            BodyTemplate: "v2 body",
            UpdatedBy: "test"));

        using var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<JsonElement[]>(
            "/api/admin/notification-templates?templateId=hitl-task-pending/email/default");

        response.Should().NotBeNull();
        response!.Length.Should().Be(2);
        response[0].GetProperty("version").GetInt32().Should().Be(2);
        response[1].GetProperty("version").GetInt32().Should().Be(1);
    }

    // --- sc-58 validate + test-send ----------------------------------------------------

    [Fact]
    public async Task ValidateProvider_WhenProviderMissing_Returns404()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/admin/notification-providers/never-existed/validate", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ValidateProvider_DisabledProvider_ReturnsProviderNotRegistered()
    {
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-paused", new
        {
            displayName = "Slack — paused",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = false,
            credential = new { action = "Replace", value = "xoxb-paused" },
        })).EnsureSuccessStatusCode();

        var response = await client.PostAsync("/api/admin/notification-providers/slack-paused/validate", content: null);
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        body.GetProperty("isValid").GetBoolean().Should().BeFalse();
        body.GetProperty("errorCode").GetString().Should().Be("dispatcher.provider_not_registered");
    }

    [Fact]
    public async Task TestSend_WhenProviderMissing_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/admin/notification-providers/never-existed/test-send",
            new
            {
                recipient = new { channel = "Slack", address = "C012", displayName = (string?)null },
                template = (object?)null,
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TestSend_RecipientChannelMismatch_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-test" },
        })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/api/admin/notification-providers/slack-prod/test-send",
            new
            {
                recipient = new { channel = "Email", address = "ops@example.com", displayName = (string?)null },
                template = (object?)null,
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TestSend_RecipientAddressMissing_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-test" },
        })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/api/admin/notification-providers/slack-prod/test-send",
            new
            {
                recipient = new { channel = "Slack", address = "", displayName = (string?)null },
                template = (object?)null,
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TestSend_NoPublicBaseUrl_SurfacesActionUrlUnconfiguredError()
    {
        // PublicBaseUrl is unset in CodeFlowApiFactory's in-memory config (sc-53). Test-send
        // must surface that as a structured error so admins know to configure it before any
        // real HITL events fire — rather than silently sending a notification with an empty
        // action URL.
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-test" },
        })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/api/admin/notification-providers/slack-prod/test-send",
            new
            {
                recipient = new { channel = "Slack", address = "C012AB3CD", displayName = (string?)null },
                template = (object?)null,
            });

        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        body.GetProperty("delivery").GetProperty("errorCode").GetString().Should().Be("dispatcher.action_url_unconfigured");
    }

    [Fact]
    public async Task GetDiagnostics_ReturnsCountsAndPublicBaseUrlStatus()
    {
        using var client = factory.CreateClient();

        // PublicBaseUrl is unset in the test factory's in-memory config, so ActionUrlsConfigured
        // is false out of the gate.
        var diag = await client.GetFromJsonAsync<JsonElement>("/api/admin/notifications/diagnostics");

        diag.GetProperty("providerCount").GetInt32().Should().Be(0);
        diag.GetProperty("routeCount").GetInt32().Should().Be(0);
        diag.GetProperty("actionUrlsConfigured").GetBoolean().Should().BeFalse();
        // PublicBaseUrl is null in the test config. ASP.NET Core's default serialisation may
        // omit null properties, so accept either "missing" or explicit null — both signal
        // unconfigured to the UI.
        if (diag.TryGetProperty("publicBaseUrl", out var publicBaseUrl))
        {
            publicBaseUrl.ValueKind.Should().Be(JsonValueKind.Null);
        }

        // Add a provider and confirm the count goes up.
        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-test" },
        })).EnsureSuccessStatusCode();

        var diag2 = await client.GetFromJsonAsync<JsonElement>("/api/admin/notifications/diagnostics");
        diag2.GetProperty("providerCount").GetInt32().Should().Be(1);
    }

    // --- sc-59 delivery audit listing ---------------------------------------------------

    [Fact]
    public async Task ListDeliveryAttempts_WhenEmpty_ReturnsEmptyPage()
    {
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<JsonElement>("/api/admin/notification-delivery-attempts");

        page.GetProperty("items").EnumerateArray().Should().BeEmpty();
        AssertNoNextCursor(page);
    }

    [Fact]
    public async Task ListDeliveryAttempts_FiltersByEventIdAndStatus_ReturnsScopedRows()
    {
        using var client = factory.CreateClient();

        var eventA = Guid.NewGuid();
        var eventB = Guid.NewGuid();
        await SeedAttemptAsync(eventA, providerId: "slack-prod", status: NotificationDeliveryStatus.Sent,
            destination: "C012AB3CD", attemptNumber: 1);
        await SeedAttemptAsync(eventA, providerId: "slack-prod", status: NotificationDeliveryStatus.Failed,
            destination: "C012AB3CD", attemptNumber: 2, errorCode: "slack.transport_error");
        await SeedAttemptAsync(eventB, providerId: "slack-prod", status: NotificationDeliveryStatus.Sent,
            destination: "C012AB3CD", attemptNumber: 1);

        // Filter by event id — only the two attempts for event A.
        var pageEventA = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/notification-delivery-attempts?eventId={eventA}");
        var items = pageEventA.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items.Select(i => i.GetProperty("eventId").GetGuid()).Should().AllBeEquivalentTo(eventA);

        // Filter by status — only the failed attempt.
        var pageFailed = await client.GetFromJsonAsync<JsonElement>(
            "/api/admin/notification-delivery-attempts?status=Failed");
        var failedItems = pageFailed.GetProperty("items").EnumerateArray().ToList();
        failedItems.Should().ContainSingle();
        failedItems[0].GetProperty("status").GetString().Should().Be("Failed");
        failedItems[0].GetProperty("errorCode").GetString().Should().Be("slack.transport_error");
    }

    [Fact]
    public async Task ListDeliveryAttempts_FiltersByProvider_ScopesToThatProvider()
    {
        using var client = factory.CreateClient();

        var eventId = Guid.NewGuid();
        await SeedAttemptAsync(eventId, providerId: "slack-prod", status: NotificationDeliveryStatus.Sent,
            destination: "C012AB3CD", attemptNumber: 1);
        await SeedAttemptAsync(eventId, providerId: "email-prod", status: NotificationDeliveryStatus.Sent,
            destination: "ops@example.com", attemptNumber: 1);

        var page = await client.GetFromJsonAsync<JsonElement>(
            "/api/admin/notification-delivery-attempts?providerId=email-prod");

        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("providerId").GetString().Should().Be("email-prod");
        items[0].GetProperty("normalizedDestination").GetString().Should().Be("ops@example.com");
    }

    [Fact]
    public async Task ListDeliveryAttempts_PagesViaBeforeIdCursor()
    {
        using var client = factory.CreateClient();

        // Seed 3 attempts — request limit=2 so the first page returns the two newest with a
        // non-null cursor, and a follow-up call with that cursor returns the remaining one.
        var eventId = Guid.NewGuid();
        for (var i = 1; i <= 3; i++)
        {
            await SeedAttemptAsync(eventId, providerId: "slack-prod", status: NotificationDeliveryStatus.Sent,
                destination: "C012AB3CD", attemptNumber: i);
        }

        var firstPage = await client.GetFromJsonAsync<JsonElement>(
            "/api/admin/notification-delivery-attempts?limit=2");
        var firstItems = firstPage.GetProperty("items").EnumerateArray().ToList();
        firstItems.Should().HaveCount(2);
        firstPage.GetProperty("nextBeforeId").ValueKind.Should().Be(JsonValueKind.Number);
        var cursor = firstPage.GetProperty("nextBeforeId").GetInt64();

        var secondPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/notification-delivery-attempts?limit=2&beforeId={cursor}");
        var secondItems = secondPage.GetProperty("items").EnumerateArray().ToList();
        secondItems.Should().ContainSingle();
        AssertNoNextCursor(secondPage);

        // No row should appear on both pages.
        var firstIds = firstItems.Select(i => i.GetProperty("id").GetInt64()).ToHashSet();
        var secondIds = secondItems.Select(i => i.GetProperty("id").GetInt64()).ToHashSet();
        firstIds.Should().NotIntersectWith(secondIds);
    }

    [Fact]
    public async Task ListDeliveryAttempts_OrdersDescendingByMostRecent()
    {
        using var client = factory.CreateClient();

        var eventId = Guid.NewGuid();
        await SeedAttemptAsync(eventId, providerId: "slack-prod", status: NotificationDeliveryStatus.Sent,
            destination: "C012AB3CD", attemptNumber: 1);
        await SeedAttemptAsync(eventId, providerId: "slack-prod", status: NotificationDeliveryStatus.Sent,
            destination: "C012AB3CD", attemptNumber: 2);

        var page = await client.GetFromJsonAsync<JsonElement>(
            "/api/admin/notification-delivery-attempts");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        // Newest first — attempt_number 2 was inserted second so its id is larger.
        items[0].GetProperty("attemptNumber").GetInt32().Should()
            .BeGreaterThan(items[1].GetProperty("attemptNumber").GetInt32());
    }

    /// <summary>
    /// Asserts that the page has no follow-up cursor. ASP.NET Core's default JSON serialiser
    /// may omit null properties — accept either "missing" or explicit null as "no more pages".
    /// </summary>
    private static void AssertNoNextCursor(JsonElement page)
    {
        if (page.TryGetProperty("nextBeforeId", out var cursor))
        {
            cursor.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    /// <summary>
    /// Seed a delivery-attempt row directly through the repository so the audit endpoint has
    /// something to surface. The dispatcher is the production write path; tests bypass it to
    /// avoid the ceremony of standing up the full provider + route + template chain.
    /// </summary>
    private async Task SeedAttemptAsync(
        Guid eventId,
        string providerId,
        NotificationDeliveryStatus status,
        string destination,
        int attemptNumber,
        string? errorCode = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<INotificationDeliveryAttemptRepository>();
        var nowUtc = DateTimeOffset.UtcNow;
        await repo.RecordAsync(
            new NotificationDeliveryResult(
                EventId: eventId,
                RouteId: $"route/{providerId}",
                ProviderId: providerId,
                Status: status,
                AttemptedAtUtc: nowUtc,
                CompletedAtUtc: nowUtc,
                AttemptNumber: attemptNumber,
                NormalizedDestination: destination,
                ProviderMessageId: status == NotificationDeliveryStatus.Sent ? $"msg-{attemptNumber}" : null,
                ErrorCode: errorCode,
                ErrorMessage: errorCode is null ? null : "seeded for sc-59 audit test"),
            NotificationEventKind.HitlTaskPending);
    }

    // --- sc-60 authorization tests ------------------------------------------------------

    public static IEnumerable<object[]> NotificationEndpoints()
    {
        // Every endpoint exposed by NotificationsEndpoints. The "verb url" pairs are the same
        // path strings registered in MapNotificationsEndpoints — keep this list in sync as the
        // surface area grows.
        yield return new object[] { "GET",    "/api/admin/notification-providers" };
        yield return new object[] { "PUT",    "/api/admin/notification-providers/some-id" };
        yield return new object[] { "DELETE", "/api/admin/notification-providers/some-id" };
        yield return new object[] { "POST",   "/api/admin/notification-providers/some-id/validate" };
        yield return new object[] { "POST",   "/api/admin/notification-providers/some-id/test-send" };
        yield return new object[] { "GET",    "/api/admin/notification-routes" };
        yield return new object[] { "PUT",    "/api/admin/notification-routes/some-route" };
        yield return new object[] { "DELETE", "/api/admin/notification-routes/some-route" };
        yield return new object[] { "GET",    "/api/admin/notification-templates" };
        yield return new object[] { "GET",    "/api/admin/notifications/diagnostics" };
        yield return new object[] { "GET",    "/api/admin/notification-delivery-attempts" };
    }

    [Theory]
    [MemberData(nameof(NotificationEndpoints))]
    public async Task NotificationEndpoint_RejectsNonAdminCallers(string verb, string url)
    {
        // Non-admin clients should hit the PermissionRequirement before any controller logic
        // runs and get 403. Both NotificationsRead and NotificationsWrite policies are
        // Admin-only per CodeFlowApiDefaults.PermissionRoleMatrix; this Theory verifies every
        // endpoint enforces that, including the sc-58 validate/test-send and sc-59 audit
        // listing endpoints added after sc-57's original auth wiring.
        using var nonAdminFactory = factory.WithRoles(CodeFlowApiDefaults.Roles.Viewer);
        using var client = nonAdminFactory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(verb), url);
        if (verb is "PUT" or "POST")
        {
            // Body is irrelevant — auth runs before model binding for the policies we care
            // about. Send a syntactically valid JSON object so a 400 from missing body can't
            // mask a 403/200 outcome.
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"non-admin callers must not reach {verb} {url}");
    }

    [Theory]
    [MemberData(nameof(NotificationEndpoints))]
    public async Task NotificationEndpoint_AllowsAdminCallers(string verb, string url)
    {
        // Counterpart to the 403 Theory: admins are not blocked by the auth pipeline. The
        // exact response body varies (404 for missing rows, 200 for empty lists, 400 for the
        // templates endpoint without a templateId, etc.) — we only assert the call did NOT
        // return 401 or 403. This pins "admin role grants access" without coupling to the
        // specific success shape, which the dedicated tests already cover.
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(verb), url);
        if (verb is "PUT" or "POST")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            $"admin must be authenticated for {verb} {url}");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            $"admin must be authorized for {verb} {url}");
    }

    // --- sc-60 validation gap-fillers ---------------------------------------------------

    [Fact]
    public async Task TestSend_AgainstArchivedProvider_SurfacesProviderNotRegistered()
    {
        // The registry returns null for archived providers (sc-54 promoted to async factory
        // pattern), and TestSend surfaces that as a structured error rather than 404 so the
        // admin UI can render alongside the other validation outcomes. Pin that behaviour.
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-archived", new
        {
            displayName = "Slack — to-be-archived",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-test" },
        })).EnsureSuccessStatusCode();

        (await client.DeleteAsync("/api/admin/notification-providers/slack-archived")).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/api/admin/notification-providers/slack-archived/test-send",
            new
            {
                recipient = new { channel = "Slack", address = "C012AB3CD", displayName = (string?)null },
                template = (object?)null,
            });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("delivery").GetProperty("errorCode").GetString()
            .Should().Be("dispatcher.provider_not_registered");
    }

    [Fact]
    public async Task PutRoute_RejectsEmptyRecipientArray()
    {
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = "xoxb-test" },
        })).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync("/api/admin/notification-routes/r1", new
        {
            eventKind = "HitlTaskPending",
            providerId = "slack-prod",
            recipients = Array.Empty<object>(),
            template = new { templateId = "t/1", version = 1 },
            minimumSeverity = "Normal",
            enabled = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutProvider_AcceptsArbitraryAdditionalConfigJson_WithoutCrashing()
    {
        // additional_config_json is provider-specific opaque bytes (engine selector for email,
        // future Slack workspace hint, etc.). The API doesn't validate the shape because that
        // would tightly couple the API layer to every provider's schema — pin "the API stores
        // whatever the admin put in and returns 200" so a future contributor doesn't add naive
        // JSON-schema validation that breaks bespoke provider configs.
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/notification-providers/email-bespoke", new
        {
            displayName = "Email bespoke",
            channel = "Email",
            endpointUrl = (string?)null,
            fromAddress = "ops@example.com",
            additionalConfigJson = """{"engine":"smtp","host":"mx","port":587,"unknown_extra_field":42}""",
            enabled = true,
            credential = new { action = "Replace", value = "smtp-password" },
        });
        put.EnsureSuccessStatusCode();

        var saved = await put.Content.ReadFromJsonAsync<JsonElement>();
        saved.GetProperty("additionalConfigJson").GetString().Should().Contain("unknown_extra_field");
    }

    [Fact]
    public async Task PutProvider_RejectsMalformedEndpointUrl()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/notification-providers/p1", new
        {
            displayName = "P1",
            channel = "Email",
            endpointUrl = "not-a-url",
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Preserve", value = (string?)null },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ValidateAndTestSend_DoNotEchoCredential()
    {
        // sc-58 routes — the response shapes don't include the credential field, but a future
        // refactor could leak it (e.g. by adding a debug field). Round-trip with a marker
        // string and assert it never appears in either response body.
        using var client = factory.CreateClient();

        var marker = "xoxb-marker-must-not-leak-12345";
        (await client.PutAsJsonAsync("/api/admin/notification-providers/slack-prod", new
        {
            displayName = "Slack",
            channel = "Slack",
            endpointUrl = (string?)null,
            fromAddress = (string?)null,
            additionalConfigJson = (string?)null,
            enabled = true,
            credential = new { action = "Replace", value = marker },
        })).EnsureSuccessStatusCode();

        // Validate path: hits Slack's auth.test which will fail with a fake token, but the
        // response body must not echo our credential under any error path.
        var validate = await client.PostAsJsonAsync(
            "/api/admin/notification-providers/slack-prod/validate", new { });
        var validateBody = await validate.Content.ReadAsStringAsync();
        validateBody.Should().NotContain(marker);

        // Test-send path: same expectation. Action-URL-unconfigured short-circuit fires first
        // because the test factory has no PublicBaseUrl — but even if the call ever reached
        // Slack's API, the response shape only carries delivery metadata, never the cred.
        var testSend = await client.PostAsJsonAsync(
            "/api/admin/notification-providers/slack-prod/test-send",
            new
            {
                recipient = new { channel = "Slack", address = "C012AB3CD", displayName = (string?)null },
                template = (object?)null,
            });
        var testSendBody = await testSend.Content.ReadAsStringAsync();
        testSendBody.Should().NotContain(marker);
    }
}
