using CodeFlow.Persistence;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodeFlow.Api.Tests.Integration;

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
}
