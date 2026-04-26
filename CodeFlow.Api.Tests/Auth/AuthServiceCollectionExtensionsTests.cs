using CodeFlow.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CodeFlow.Api.Tests.Auth;

public sealed class AuthServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCodeFlowAuth_throws_when_Production_and_DevelopmentBypass_enabled()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(developmentBypass: true);
        var environment = new TestHostEnvironment(Environments.Production);

        var act = () => services.AddCodeFlowAuth(configuration, environment);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*DevelopmentBypass*Production*");
    }

    [Fact]
    public void AddCodeFlowAuth_succeeds_when_Production_and_DevelopmentBypass_disabled_with_OIDC_config()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(
            developmentBypass: false,
            authority: "https://identity.trefry.net/realms/trefry",
            audience: "codeflow-api");
        var environment = new TestHostEnvironment(Environments.Production);

        var act = () => services.AddCodeFlowAuth(configuration, environment);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddCodeFlowAuth_throws_when_DevelopmentBypass_disabled_and_Authority_missing()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(
            developmentBypass: false,
            authority: null,
            audience: "codeflow-api");
        var environment = new TestHostEnvironment(Environments.Production);

        var act = () => services.AddCodeFlowAuth(configuration, environment);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Auth:Authority*");
    }

    [Fact]
    public void AddCodeFlowAuth_throws_when_DevelopmentBypass_disabled_and_Audience_missing()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(
            developmentBypass: false,
            authority: "https://identity.trefry.net/realms/trefry",
            audience: null);
        var environment = new TestHostEnvironment(Environments.Production);

        var act = () => services.AddCodeFlowAuth(configuration, environment);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Auth:Audience*");
    }

    [Fact]
    public void AddCodeFlowAuth_throws_with_grouped_message_when_both_Authority_and_Audience_missing()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(
            developmentBypass: false,
            authority: null,
            audience: null);
        var environment = new TestHostEnvironment(Environments.Production);

        var act = () => services.AddCodeFlowAuth(configuration, environment);

        act.Should()
            .Throw<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("Auth:Authority") && ex.Message.Contains("Auth:Audience"));
    }

    [Fact]
    public void AddCodeFlowAuth_does_not_require_OIDC_config_when_DevelopmentBypass_enabled()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(
            developmentBypass: true,
            authority: null,
            audience: null);
        var environment = new TestHostEnvironment(Environments.Development);

        var act = () => services.AddCodeFlowAuth(configuration, environment);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddCodeFlowAuth_succeeds_when_Development_and_DevelopmentBypass_enabled()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(developmentBypass: true);
        var environment = new TestHostEnvironment(Environments.Development);

        var act = () => services.AddCodeFlowAuth(configuration, environment);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task AddCodeFlowAuth_does_not_register_development_scheme_in_Production()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(
            developmentBypass: false,
            authority: "https://identity.trefry.net/realms/trefry",
            audience: "codeflow-api");
        var environment = new TestHostEnvironment(Environments.Production);

        services.AddCodeFlowAuth(configuration, environment);

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var schemes = await schemeProvider.GetAllSchemesAsync();
        var scheme = schemes.SingleOrDefault(s => s.Name == DevelopmentAuthenticationHandler.SchemeName);

        scheme.Should().BeNull("Development authentication handler must not be registered in Production");
    }

    [Fact]
    public async Task AddCodeFlowAuth_registers_development_scheme_outside_Production()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(developmentBypass: true);
        var environment = new TestHostEnvironment(Environments.Development);

        services.AddCodeFlowAuth(configuration, environment);

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var schemes = await schemeProvider.GetAllSchemesAsync();
        var scheme = schemes.SingleOrDefault(s => s.Name == DevelopmentAuthenticationHandler.SchemeName);

        scheme.Should().NotBeNull();
    }

    private static IConfiguration BuildConfiguration(bool developmentBypass)
    {
        return BuildConfiguration(developmentBypass, authority: null, audience: null);
    }

    private static IConfiguration BuildConfiguration(bool developmentBypass, string? authority, string? audience)
    {
        var values = new Dictionary<string, string?>
        {
            ["Auth:DevelopmentBypass"] = developmentBypass ? "true" : "false"
        };
        if (authority is not null)
        {
            values["Auth:Authority"] = authority;
        }
        if (audience is not null)
        {
            values["Auth:Audience"] = audience;
        }
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
            ApplicationName = "CodeFlow.Api.Tests";
            ContentRootPath = AppContext.BaseDirectory;
            ContentRootFileProvider = new NullFileProvider();
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; }

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
