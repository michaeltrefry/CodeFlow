using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class McpServersEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public McpServersEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Post_then_get_roundtrips_server_without_exposing_bearer_token()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            key = $"artifacts-{Guid.NewGuid():N}",
            displayName = "Artifacts",
            transport = "StreamableHttp",
            endpointUrl = "https://artifacts.local/mcp",
            bearerToken = "super-secret",
        });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<McpServerResponseDto>();
        created.Should().NotBeNull();
        created!.HasBearerToken.Should().BeTrue();
        created.HealthStatus.Should().Be("Unverified");

        var fetched = await client.GetFromJsonAsync<McpServerResponseDto>($"/api/mcp-servers/{created.Id}");
        fetched!.Key.Should().Be(created.Key);
        fetched.HasBearerToken.Should().BeTrue();

        // Response never includes the plaintext value — GET returns only the HasBearerToken flag.
        var raw = await client.GetStringAsync($"/api/mcp-servers/{created.Id}");
        raw.Should().NotContain("super-secret");
    }

    [Fact]
    public async Task Post_with_duplicate_key_returns_conflict()
    {
        using var client = factory.CreateClient();
        var key = $"dup-{Guid.NewGuid():N}";

        var payload = new
        {
            key,
            displayName = "D",
            transport = "StreamableHttp",
            endpointUrl = "https://x.local/mcp",
            bearerToken = (string?)null,
        };

        (await client.PostAsJsonAsync("/api/mcp-servers", payload)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/mcp-servers", payload)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Put_with_replace_bearer_token_rotates_secret()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            key = $"rotate-{Guid.NewGuid():N}",
            displayName = "Rotate",
            transport = "StreamableHttp",
            endpointUrl = "https://rotate.local/mcp",
            bearerToken = "original",
        });
        var created = (await create.Content.ReadFromJsonAsync<McpServerResponseDto>())!;

        var update = await client.PutAsJsonAsync($"/api/mcp-servers/{created.Id}", new
        {
            displayName = "Rotate",
            transport = "StreamableHttp",
            endpointUrl = "https://rotate.local/mcp",
            bearerToken = new { action = "Replace", value = "rotated" },
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = await client.GetFromJsonAsync<McpServerResponseDto>($"/api/mcp-servers/{created.Id}");
        fetched!.HasBearerToken.Should().BeTrue();
    }

    [Fact]
    public async Task Put_with_clear_bearer_token_drops_secret()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            key = $"clear-{Guid.NewGuid():N}",
            displayName = "Clear",
            transport = "StreamableHttp",
            endpointUrl = "https://clear.local/mcp",
            bearerToken = "to-be-removed",
        });
        var created = (await create.Content.ReadFromJsonAsync<McpServerResponseDto>())!;

        await client.PutAsJsonAsync($"/api/mcp-servers/{created.Id}", new
        {
            displayName = "Clear",
            transport = "StreamableHttp",
            endpointUrl = "https://clear.local/mcp",
            bearerToken = new { action = "Clear" },
        });

        var fetched = await client.GetFromJsonAsync<McpServerResponseDto>($"/api/mcp-servers/{created.Id}");
        fetched!.HasBearerToken.Should().BeFalse();
    }

    [Fact]
    public async Task Put_with_replace_but_empty_value_returns_validation_problem()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            key = $"bad-{Guid.NewGuid():N}",
            displayName = "Bad",
            transport = "StreamableHttp",
            endpointUrl = "https://bad.local/mcp",
            bearerToken = (string?)null,
        });
        var created = (await create.Content.ReadFromJsonAsync<McpServerResponseDto>())!;

        var update = await client.PutAsJsonAsync($"/api/mcp-servers/{created.Id}", new
        {
            displayName = "Bad",
            transport = "StreamableHttp",
            endpointUrl = "https://bad.local/mcp",
            bearerToken = new { action = "Replace", value = "" },
        });

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_archives_and_hides_from_default_list()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            key = $"archive-{Guid.NewGuid():N}",
            displayName = "Archive",
            transport = "StreamableHttp",
            endpointUrl = "https://archive.local/mcp",
            bearerToken = (string?)null,
        });
        var created = (await create.Content.ReadFromJsonAsync<McpServerResponseDto>())!;

        var delete = await client.DeleteAsync($"/api/mcp-servers/{created.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var defaultList = await client.GetFromJsonAsync<IReadOnlyList<McpServerResponseDto>>("/api/mcp-servers");
        defaultList!.Should().NotContain(s => s.Id == created.Id);

        var withArchived = await client.GetFromJsonAsync<IReadOnlyList<McpServerResponseDto>>("/api/mcp-servers?includeArchived=true");
        withArchived!.Should().Contain(s => s.Id == created.Id);
    }

    [Fact]
    public async Task Unknown_id_returns_404_on_get_put_delete()
    {
        using var client = factory.CreateClient();

        (await client.GetAsync("/api/mcp-servers/99999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync("/api/mcp-servers/99999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync("/api/mcp-servers/99999", new
        {
            displayName = "x",
            transport = "StreamableHttp",
            endpointUrl = "https://x/mcp",
            bearerToken = new { action = "Preserve" },
        })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record McpServerResponseDto(
        long Id,
        string Key,
        string DisplayName,
        string Transport,
        string EndpointUrl,
        bool HasBearerToken,
        string HealthStatus,
        DateTime? LastVerifiedAtUtc,
        string? LastVerificationError,
        DateTime CreatedAtUtc,
        string? CreatedBy,
        DateTime UpdatedAtUtc,
        string? UpdatedBy,
        bool IsArchived);
}
