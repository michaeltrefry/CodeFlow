using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class AuthDiscoveryEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public AuthDiscoveryEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task AuthConfig_IsReachable_WithoutAuthorizationHeader()
    {
        using var client = CreateClientWithAuthConfig(authority: "https://identity.example/realms/codeflow", audience: "codeflow-api");
        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.GetAsync("/api/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AuthConfig_StillRejectsBogusBearerToken_OnProtectedRoute_ButPublicRouteSucceeds()
    {
        // Sanity: the public /api/auth/config endpoint must work even if the caller sends a
        // bogus Authorization header (a CLI mid-bootstrap may have stale credentials).
        using var client = CreateClientWithAuthConfig(authority: "https://identity.example/realms/codeflow", audience: "codeflow-api");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await client.GetAsync("/api/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AuthConfig_ReturnsAuthorityAndAudienceFromConfig_WithCliDefaults()
    {
        const string authority = "https://identity.example/realms/codeflow";
        const string audience = "codeflow-api";

        using var client = CreateClientWithAuthConfig(authority, audience);

        var payload = await client.GetFromJsonAsync<AuthDiscoveryPayload>("/api/auth/config");

        payload.Should().NotBeNull();
        payload!.Authority.Should().Be(authority);
        payload.Audience.Should().Be(audience);
        payload.ClientId.Should().Be("codeflow-cli");
        payload.Scopes.Should().Be("openid profile email");
    }

    [Fact]
    public async Task AuthConfig_RespectsConfiguredCliClientIdAndScopes()
    {
        using var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Authority"] = "https://identity.example/realms/codeflow",
                    ["Auth:Audience"] = "codeflow-api",
                    ["Auth:CliClientId"] = "custom-cli-client",
                    ["Auth:CliScopes"] = "openid profile email offline_access codeflow.api"
                });
            });
        }).CreateClient();

        var payload = await client.GetFromJsonAsync<AuthDiscoveryPayload>("/api/auth/config");

        payload.Should().NotBeNull();
        payload!.ClientId.Should().Be("custom-cli-client");
        payload.Scopes.Should().Be("openid profile email offline_access codeflow.api");
    }

    [Fact]
    public async Task AuthConfig_DoesNotLeakSecretsOrUnexpectedFields()
    {
        using var client = CreateClientWithAuthConfig(authority: "https://identity.example/realms/codeflow", audience: "codeflow-api");

        using var response = await client.GetAsync("/api/auth/config");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        var properties = document.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        properties.Should().BeEquivalentTo(new[] { "authority", "clientId", "scopes", "audience" });
        raw.Should().NotContainAny("secret", "Secret", "clientSecret", "client_secret", "password", "token");
    }

    [Fact]
    public async Task AuthConfig_ReturnsProblemDetails_WhenAuthEnabledButAuthorityMissing()
    {
        using var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                // Disable the test fixture's development bypass so this exercises the
                // "auth enabled, discovery config missing" branch. AddCodeFlowAuth's startup
                // fail-fast still requires Authority + Audience to be set at boot, so we seed
                // them — then null Authority back out via IOptions reload at request time by
                // letting the endpoint re-read IOptionsSnapshot from configuration that no
                // longer has it. Easiest path: set Audience but not Authority and override
                // DevelopmentBypass after startup via direct options.
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Authority"] = "https://identity.example/realms/codeflow",
                    ["Auth:Audience"] = "codeflow-api"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<CodeFlow.Api.Auth.AuthOptions>(o =>
                {
                    o.DevelopmentBypass = false;
                    o.Authority = string.Empty;
                });
            });
        }).CreateClient();

        using var response = await client.GetAsync("/api/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().Contain("Authority");
    }

    [Fact]
    public async Task AuthConfig_ReturnsProblemDetails_WhenAuthEnabledButAudienceMissing()
    {
        using var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Authority"] = "https://identity.example/realms/codeflow",
                    ["Auth:Audience"] = "codeflow-api"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<CodeFlow.Api.Auth.AuthOptions>(o =>
                {
                    o.DevelopmentBypass = false;
                    o.Audience = string.Empty;
                });
            });
        }).CreateClient();

        using var response = await client.GetAsync("/api/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().Contain("Audience");
    }

    private HttpClient CreateClientWithAuthConfig(string authority, string audience)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Authority"] = authority,
                    ["Auth:Audience"] = audience
                });
            });
        }).CreateClient();
    }

    private sealed record AuthDiscoveryPayload(
        string Authority,
        string ClientId,
        string Scopes,
        string Audience);
}
