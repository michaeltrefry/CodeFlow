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
    public void AddCodeFlowAuth_succeeds_when_Production_and_DevelopmentBypass_disabled()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(developmentBypass: false);
        var environment = new TestHostEnvironment(Environments.Production);

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
        var configuration = BuildConfiguration(developmentBypass: false);
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
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:DevelopmentBypass"] = developmentBypass ? "true" : "false"
            })
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
