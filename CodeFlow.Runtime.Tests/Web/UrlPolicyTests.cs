using System.Net;
using CodeFlow.Runtime.Web;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Web;

public sealed class UrlPolicyTests
{
    [Theory]
    [InlineData("http://localhost/path")]
    [InlineData("http://Localhost/path")]
    [InlineData("https://api.localhost/x")]
    [InlineData("http://service.local/")]
    [InlineData("http://corpwiki.internal/")]
    [InlineData("http://node1.lan/")]
    [InlineData("http://router.home/")]
    [InlineData("http://app.corp/")]
    [InlineData("http://wiki.intranet/")]
    [InlineData("http://x.private/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.5.6.7/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://0.0.0.1/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[ff02::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    public void ValidateLiteralUrl_blocks_loopback_private_link_local_and_metadata(string url)
    {
        var result = UrlPolicy.ValidateLiteralUrl(new WebToolOptions(), url);

        result.Allowed.Should().BeFalse();
        result.Code.Should().Be("url-private-host");
    }

    [Theory]
    [InlineData("https://docs.docker.com/")]
    [InlineData("https://nodejs.org/en/download/")]
    [InlineData("http://example.com/")]
    [InlineData("https://hub.docker.com/_/python")]
    public void ValidateLiteralUrl_allows_public_hosts(string url)
    {
        var result = UrlPolicy.ValidateLiteralUrl(new WebToolOptions(), url);

        result.Allowed.Should().BeTrue();
        result.Uri!.Host.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    public void ValidateLiteralUrl_blocks_non_http_schemes(string url)
    {
        var result = UrlPolicy.ValidateLiteralUrl(new WebToolOptions(), url);

        result.Allowed.Should().BeFalse();
        result.Code.Should().Be("url-scheme-denied");
    }

    [Fact]
    public void ValidateLiteralUrl_blocks_userinfo_credentials()
    {
        var result = UrlPolicy.ValidateLiteralUrl(new WebToolOptions(), "https://user:pass@example.com/");

        result.Allowed.Should().BeFalse();
        result.Code.Should().Be("url-credentials-denied");
    }

    [Fact]
    public void ValidateLiteralUrl_rejects_relative_or_blank_url()
    {
        UrlPolicy.ValidateLiteralUrl(new WebToolOptions(), "").Allowed.Should().BeFalse();
        UrlPolicy.ValidateLiteralUrl(new WebToolOptions(), "/relative/path").Allowed.Should().BeFalse();
    }

    [Fact]
    public void ValidateResolvedAddresses_blocks_when_any_resolved_ip_is_private()
    {
        var uri = new Uri("https://example.com/");
        var addresses = new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5") };

        var result = UrlPolicy.ValidateResolvedAddresses(new WebToolOptions(), uri, addresses);

        result.Allowed.Should().BeFalse();
        result.Code.Should().Be("url-private-host");
    }

    [Fact]
    public void ValidateResolvedAddresses_blocks_when_dns_returns_no_addresses()
    {
        var uri = new Uri("https://example.com/");
        var result = UrlPolicy.ValidateResolvedAddresses(new WebToolOptions(), uri, Array.Empty<IPAddress>());

        result.Allowed.Should().BeFalse();
        result.Code.Should().Be("dns-failed");
    }

    [Fact]
    public void ValidateResolvedAddresses_allows_public_addresses()
    {
        var uri = new Uri("https://example.com/");
        var addresses = new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946") };

        var result = UrlPolicy.ValidateResolvedAddresses(new WebToolOptions(), uri, addresses);

        result.Allowed.Should().BeTrue();
    }
}
