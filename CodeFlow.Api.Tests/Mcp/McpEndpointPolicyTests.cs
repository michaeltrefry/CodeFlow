using CodeFlow.Api.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Tests.Mcp;

public sealed class McpEndpointPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1/mcp")]
    [InlineData("http://localhost/mcp")]
    [InlineData("http://10.0.0.5/mcp")]
    [InlineData("http://172.16.0.1/mcp")]
    [InlineData("http://172.31.255.1/mcp")]
    [InlineData("http://192.168.1.5/mcp")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/mcp")]
    [InlineData("http://[fd00::1]/mcp")]
    public async Task Default_policy_rejects_internal_addresses(string url)
    {
        var policy = BuildPolicy();

        var result = await policy.ValidateAsync(new Uri(url), CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Default_policy_rejects_http_scheme()
    {
        var policy = BuildPolicy(); // defaults: schemes=[https], AllowInternalTargets=false

        var result = await policy.ValidateAsync(new Uri("http://example.com/mcp"), CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("Scheme");
    }

    [Fact]
    public async Task AllowInternalTargets_permits_localhost()
    {
        var policy = BuildPolicy(o =>
        {
            o.AllowInternalTargets = true;
            o.AllowedSchemes = new List<string> { "http", "https" };
        });

        var result = await policy.ValidateAsync(new Uri("http://127.0.0.1:8080/mcp"), CancellationToken.None);

        result.IsAllowed.Should().BeTrue(result.Reason);
    }

    [Fact]
    public async Task AllowedHosts_wildcard_matches_subdomains()
    {
        var policy = BuildPolicy(o =>
        {
            o.AllowInternalTargets = true;
            o.AllowedHosts = new List<string> { "*.example.com" };
        });

        (await policy.ValidateAsync(new Uri("https://mcp.example.com/path"), CancellationToken.None))
            .IsAllowed.Should().BeTrue();
        (await policy.ValidateAsync(new Uri("https://attacker.com/mcp"), CancellationToken.None))
            .IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task AllowedHosts_empty_means_no_host_restriction()
    {
        var policy = BuildPolicy(o =>
        {
            o.AllowInternalTargets = true;
            o.AllowedHosts = new List<string>();
        });

        (await policy.ValidateAsync(new Uri("https://anything.at.all/mcp"), CancellationToken.None))
            .IsAllowed.Should().BeTrue();
    }

    private static McpEndpointPolicy BuildPolicy(Action<McpEndpointPolicyOptions>? configure = null)
    {
        var options = new McpEndpointPolicyOptions();
        configure?.Invoke(options);
        return new McpEndpointPolicy(new StaticOptionsMonitor<McpEndpointPolicyOptions>(options));
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
