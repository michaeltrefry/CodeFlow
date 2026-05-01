using CodeFlow.Runtime.Web;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Web;

public sealed class WebToolOptionsTests
{
    [Fact]
    public void Defaults_match_planned_public_web_policy()
    {
        var options = new WebToolOptions();

        options.AllowedSchemes.Should().BeEquivalentTo(new[] { "https", "http" });
        options.SearchTimeoutSeconds.Should().Be(15);
        options.FetchTimeoutSeconds.Should().Be(20);
        options.MaxRedirects.Should().Be(5);
        options.MaxResponseBytes.Should().Be(2 * 1024 * 1024);
        options.MaxExtractedTextBytes.Should().Be(256 * 1024);
        options.MaxSearchResults.Should().Be(8);
        options.BlockPrivateNetworks.Should().BeTrue();
        options.SendCredentials.Should().BeFalse();
        options.AllowCookies.Should().BeFalse();
        options.AllowAuthHeaders.Should().BeFalse();
        options.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_rejects_non_http_schemes()
    {
        var options = new WebToolOptions
        {
            AllowedSchemes = ["https", "file"]
        };

        var errors = options.Validate();

        errors.Should().Contain(error => error.Contains("http or https"));
    }

    [Fact]
    public void Validate_rejects_credentials_and_private_network_access()
    {
        var options = new WebToolOptions
        {
            BlockPrivateNetworks = false,
            SendCredentials = true,
            AllowCookies = true,
            AllowAuthHeaders = true
        };

        var errors = options.Validate();

        errors.Should().Contain(error => error.Contains("BlockPrivateNetworks"));
        errors.Should().Contain(error => error.Contains("SendCredentials"));
        errors.Should().Contain(error => error.Contains("AllowCookies"));
        errors.Should().Contain(error => error.Contains("AllowAuthHeaders"));
    }

    [Fact]
    public void Validate_rejects_invalid_limits()
    {
        var options = new WebToolOptions
        {
            AllowedSchemes = [],
            SearchTimeoutSeconds = 0,
            FetchTimeoutSeconds = 0,
            MaxRedirects = -1,
            MaxResponseBytes = 0,
            MaxExtractedTextBytes = 0,
            MaxSearchResults = 0
        };

        var errors = options.Validate();

        errors.Should().Contain(error => error.Contains("AllowedSchemes"));
        errors.Should().Contain(error => error.Contains("SearchTimeoutSeconds"));
        errors.Should().Contain(error => error.Contains("FetchTimeoutSeconds"));
        errors.Should().Contain(error => error.Contains("MaxRedirects"));
        errors.Should().Contain(error => error.Contains("MaxResponseBytes"));
        errors.Should().Contain(error => error.Contains("MaxExtractedTextBytes"));
        errors.Should().Contain(error => error.Contains("MaxSearchResults"));
    }
}
