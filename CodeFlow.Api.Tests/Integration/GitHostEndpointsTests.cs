using CodeFlow.Runtime.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class GitHostEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public GitHostEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Get_returns_unconfigured_defaults_when_no_settings_exist()
    {
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<GitHostSettingsResponseDto>("/api/admin/git-host");

        response.Should().NotBeNull();
        response!.HasToken.Should().BeFalse();
        response.UpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Put_then_get_roundtrips_without_exposing_token()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/git-host", new
        {
            mode = "GitHub",
            baseUrl = (string?)null,
            token = new { action = "Replace", value = "ghp_round_trip_secret_value" },
        });
        put.EnsureSuccessStatusCode();

        var updated = (await put.Content.ReadFromJsonAsync<GitHostSettingsResponseDto>())!;
        updated.Mode.Should().Be("GitHub");
        updated.HasToken.Should().BeTrue();

        var rawGet = await client.GetStringAsync("/api/admin/git-host");
        rawGet.Should().NotContain("ghp_round_trip_secret_value");

        var rawPut = await put.Content.ReadAsStringAsync();
        rawPut.Should().NotContain("ghp_round_trip_secret_value");
    }

    [Fact]
    public async Task Put_rejects_replace_with_empty_value()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/git-host", new
        {
            mode = "GitHub",
            baseUrl = (string?)null,
            token = new { action = "Replace", value = "" },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_rejects_preserve_on_first_save()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/git-host", new
        {
            mode = "GitHub",
            baseUrl = (string?)null,
            token = new { action = "Preserve", value = (string?)null },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_with_preserve_updates_mode_without_retouching_token()
    {
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/git-host", new
        {
            mode = "GitHub",
            baseUrl = (string?)null,
            token = new { action = "Replace", value = "ghp_preserve_test_secret" },
        })).EnsureSuccessStatusCode();

        var preserve = await client.PutAsJsonAsync("/api/admin/git-host", new
        {
            mode = "GitLab",
            baseUrl = "https://gitlab.example.com",
            token = new { action = "Preserve", value = (string?)null },
        });
        preserve.EnsureSuccessStatusCode();

        var settings = (await preserve.Content.ReadFromJsonAsync<GitHostSettingsResponseDto>())!;
        settings.Mode.Should().Be("GitLab");
        settings.BaseUrl.Should().Be("https://gitlab.example.com");
        settings.HasToken.Should().BeTrue();

        var preservedBody = await preserve.Content.ReadAsStringAsync();
        preservedBody.Should().NotContain("ghp_preserve_test_secret");
    }

    [Fact]
    public async Task Put_rejects_gitlab_without_baseUrl()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/git-host", new
        {
            mode = "GitLab",
            baseUrl = (string?)null,
            token = new { action = "Replace", value = "glpat_token" },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_trims_trailing_slash_on_gitlab_baseUrl()
    {
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/admin/git-host", new
        {
            mode = "GitLab",
            baseUrl = "https://gitlab.example.com/",
            token = new { action = "Replace", value = "glpat_tok" },
        });
        put.EnsureSuccessStatusCode();

        var settings = (await put.Content.ReadFromJsonAsync<GitHostSettingsResponseDto>())!;
        settings.BaseUrl.Should().Be("https://gitlab.example.com");
    }

    [Fact]
    public async Task Verify_marks_LastVerifiedAt_when_verifier_succeeds()
    {
        using var stubFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHostVerifier>();
                services.AddSingleton<IGitHostVerifier>(new StubVerifier(success: true, error: null));
            });
        });
        using var client = stubFactory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/git-host", new
        {
            mode = "GitHub",
            baseUrl = (string?)null,
            token = new { action = "Replace", value = "ghp_for_verify" },
        })).EnsureSuccessStatusCode();

        var verify = await client.PostAsync("/api/admin/git-host/verify", content: null);
        verify.EnsureSuccessStatusCode();
        var verifyBody = (await verify.Content.ReadFromJsonAsync<GitHostVerifyResponseDto>())!;
        verifyBody.Success.Should().BeTrue();
        verifyBody.LastVerifiedAtUtc.Should().NotBeNull();

        var settings = (await client.GetFromJsonAsync<GitHostSettingsResponseDto>("/api/admin/git-host"))!;
        settings.LastVerifiedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Verify_surfaces_failure_without_marking_verified()
    {
        using var stubFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHostVerifier>();
                services.AddSingleton<IGitHostVerifier>(new StubVerifier(success: false, error: "401 Unauthorized"));
            });
        });
        using var client = stubFactory.CreateClient();

        (await client.PutAsJsonAsync("/api/admin/git-host", new
        {
            mode = "GitHub",
            baseUrl = (string?)null,
            token = new { action = "Replace", value = "ghp_bad" },
        })).EnsureSuccessStatusCode();

        var verify = await client.PostAsync("/api/admin/git-host/verify", content: null);
        var body = (await verify.Content.ReadFromJsonAsync<GitHostVerifyResponseDto>())!;
        body.Success.Should().BeFalse();
        body.Error.Should().Contain("401");

        var settings = (await client.GetFromJsonAsync<GitHostSettingsResponseDto>("/api/admin/git-host"))!;
        settings.LastVerifiedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Verify_returns_badrequest_when_settings_missing()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/admin/git-host/verify", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record GitHostSettingsResponseDto(
        string Mode,
        string? BaseUrl,
        bool HasToken,
        DateTime? LastVerifiedAtUtc,
        string? UpdatedBy,
        DateTime? UpdatedAtUtc);

    private sealed record GitHostVerifyResponseDto(
        bool Success,
        DateTime? LastVerifiedAtUtc,
        string? Error);

    private sealed class StubVerifier : IGitHostVerifier
    {
        private readonly bool success;
        private readonly string? error;

        public StubVerifier(bool success, string? error)
        {
            this.success = success;
            this.error = error;
        }

        public Task<GitHostVerificationResult> VerifyAsync(
            GitHostMode mode,
            string? baseUrl,
            string token,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHostVerificationResult(success, error));
    }
}
