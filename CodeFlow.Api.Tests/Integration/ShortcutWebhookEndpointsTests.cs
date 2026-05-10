using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
[Collection("CodeFlowApi")]
public sealed class ShortcutWebhookEndpointsTests
{
    private readonly CodeFlowApiFactory factory;

    public ShortcutWebhookEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task PostWebhook_AcceptsStoryCreatePayload()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/integrations/shortcut/webhook", new
        {
            version = "v1",
            actions = new object[]
            {
                new
                {
                    action = "create",
                    entity_type = "story"
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ShortcutWebhookResponse>();
        payload.Should().Be(new ShortcutWebhookResponse("accepted", "Shortcut story create event accepted."));
    }

    [Fact]
    public async Task PostWebhook_IgnoresNonStoryPayload()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/integrations/shortcut/webhook", new
        {
            version = "v1",
            actions = new object[]
            {
                new
                {
                    action = "create",
                    entity_type = "epic"
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ShortcutWebhookResponse>();
        payload.Should().Be(new ShortcutWebhookResponse("ignored", "Shortcut webhook event ignored."));
    }

    [Fact]
    public async Task PostWebhook_IgnoresStoryNonCreatePayload()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/integrations/shortcut/webhook", new
        {
            version = "v1",
            actions = new object[]
            {
                new
                {
                    action = "update",
                    entity_type = "story"
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ShortcutWebhookResponse>();
        payload.Should().Be(new ShortcutWebhookResponse("ignored", "Shortcut webhook event ignored."));
    }

    [Fact]
    public async Task PostWebhook_IgnoresEmptyActions()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/integrations/shortcut/webhook", new
        {
            version = "v1",
            actions = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ShortcutWebhookResponse>();
        payload.Should().Be(new ShortcutWebhookResponse("ignored", "Shortcut webhook event ignored."));
    }

    [Fact]
    public async Task PostWebhook_ReturnsUnsupportedVersion_WhenVersionMissing()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/integrations/shortcut/webhook", new
        {
            actions = new object[]
            {
                new
                {
                    action = "create",
                    entity_type = "story"
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ShortcutWebhookResponse>();
        payload.Should().Be(new ShortcutWebhookResponse("unsupported_version", "Shortcut webhook version must be 'v1'."));
    }

    [Fact]
    public async Task PostWebhook_ReturnsInvalidPayload_WhenMalformedJson()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("{\"version\":\"v1\",\"actions\":[", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/integrations/shortcut/webhook", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ShortcutWebhookResponse>();
        payload.Should().Be(new ShortcutWebhookResponse("invalid_payload", "Malformed JSON payload."));
    }

    private sealed record ShortcutWebhookResponse(string Status, string Message);
}
