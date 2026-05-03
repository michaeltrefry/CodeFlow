using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class WebSearchProviderEndpointsTests
    : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public WebSearchProviderEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Shared class fixture — wipe the singleton row so "no settings yet" assertions hold
        // even when run after a sibling test that wrote a row.
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        await dbContext.WebSearchProviders.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_returns_none_defaults_when_no_row_has_been_written()
    {
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<WebSearchProviderResponseDto>(
            "/api/admin/web-search-provider");

        response.Should().NotBeNull();
        response!.Provider.Should().Be("none");
        response.HasApiKey.Should().BeFalse();
        response.UpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Put_then_get_roundtrips_without_exposing_token()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/web-search-provider", new
        {
            provider = "brave",
            endpointUrl = (string?)null,
            token = new { action = "Replace", value = "brave_round_trip_secret_value" },
        });
        put.EnsureSuccessStatusCode();

        var updated = (await put.Content.ReadFromJsonAsync<WebSearchProviderResponseDto>())!;
        updated.Provider.Should().Be("brave");
        updated.HasApiKey.Should().BeTrue();

        // Defense-in-depth: secret never crosses the wire on either response.
        var rawGet = await client.GetStringAsync("/api/admin/web-search-provider");
        rawGet.Should().NotContain("brave_round_trip_secret_value");

        var rawPut = await put.Content.ReadAsStringAsync();
        rawPut.Should().NotContain("brave_round_trip_secret_value");
    }

    [Fact]
    public async Task Put_rejects_unknown_provider()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/web-search-provider", new
        {
            provider = "kagi",
            endpointUrl = (string?)null,
            token = new { action = "Preserve", value = (string?)null },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_rejects_replace_with_empty_value()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/web-search-provider", new
        {
            provider = "brave",
            endpointUrl = (string?)null,
            token = new { action = "Replace", value = "" },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_rejects_invalid_endpoint_url()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/web-search-provider", new
        {
            provider = "brave",
            endpointUrl = "ftp://api.search.brave.com/foo",
            token = new { action = "Replace", value = "real-key" },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preserve_keeps_stored_token_when_switching_provider_to_none_and_back()
    {
        using var client = factory.CreateClient();

        // Initial: configure Brave with a key.
        (await client.PutAsJsonAsync("/api/admin/web-search-provider", new
        {
            provider = "brave",
            endpointUrl = (string?)null,
            token = new { action = "Replace", value = "preserve-test-key" },
        })).EnsureSuccessStatusCode();

        // Toggle off without clearing the key.
        var disabled = await client.PutAsJsonAsync("/api/admin/web-search-provider", new
        {
            provider = "none",
            endpointUrl = (string?)null,
            token = new { action = "Preserve", value = (string?)null },
        });
        disabled.EnsureSuccessStatusCode();
        var disabledBody = (await disabled.Content.ReadFromJsonAsync<WebSearchProviderResponseDto>())!;
        disabledBody.Provider.Should().Be("none");
        disabledBody.HasApiKey.Should().BeTrue();

        // Toggle back on; key should still be there.
        var reenabled = await client.PutAsJsonAsync("/api/admin/web-search-provider", new
        {
            provider = "brave",
            endpointUrl = (string?)null,
            token = new { action = "Preserve", value = (string?)null },
        });
        reenabled.EnsureSuccessStatusCode();
        var reenabledBody = (await reenabled.Content.ReadFromJsonAsync<WebSearchProviderResponseDto>())!;
        reenabledBody.Provider.Should().Be("brave");
        reenabledBody.HasApiKey.Should().BeTrue();
    }

    private sealed record WebSearchProviderResponseDto(
        string Provider,
        bool HasApiKey,
        string? EndpointUrl,
        string? UpdatedBy,
        DateTime? UpdatedAtUtc);
}
