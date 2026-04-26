using CodeFlow.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.MariaDb;
using Testcontainers.RabbitMq;

namespace CodeFlow.Api.Tests.Integration;

public sealed class CodeFlowApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    static CodeFlowApiFactory()
    {
        // WebApplication.CreateBuilder reads configuration at builder-construction time, before
        // WebApplicationFactory.ConfigureWebHost has a chance to inject in-memory values.
        // Seed values that are read eagerly during service registration so the host can start
        // (Secrets master key for AesGcmSecretProtector; Auth:DevelopmentBypass=true so the
        // production OIDC fail-fast in AddCodeFlowAuth doesn't trip in the test environment).
        Environment.SetEnvironmentVariable("Secrets__MasterKey", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        Environment.SetEnvironmentVariable("Auth__DevelopmentBypass", "true");
    }

    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_api_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly RabbitMqContainer rabbitMqContainer = new RabbitMqBuilder("rabbitmq:4.0-management")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly string artifactRoot = Path.Combine(
        Path.GetTempPath(),
        "codeflow-api-tests",
        Guid.NewGuid().ToString("N"));

    public string ArtifactRoot => artifactRoot;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(artifactRoot);
        await mariaDbContainer.StartAsync();
        await rabbitMqContainer.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        if (Directory.Exists(artifactRoot))
        {
            try
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        await rabbitMqContainer.DisposeAsync();
        await mariaDbContainer.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable] = mariaDbContainer.GetConnectionString(),
                ["RabbitMq:Host"] = rabbitMqContainer.Hostname,
                ["RabbitMq:Port"] = rabbitMqContainer.GetMappedPublicPort(5672).ToString(),
                ["RabbitMq:VirtualHost"] = "/",
                ["RabbitMq:Username"] = "codeflow",
                ["RabbitMq:Password"] = "codeflow_dev",
                ["Artifacts:RootDirectory"] = artifactRoot,
                ["Auth:DevelopmentBypass"] = "true",
                ["Auth:RequireHttpsMetadata"] = "false",
                ["McpEndpointPolicy:AllowInternalTargets"] = "true",
                ["McpEndpointPolicy:AllowedSchemes:0"] = "http",
                ["McpEndpointPolicy:AllowedSchemes:1"] = "https",
                ["Secrets:MasterKey"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
            });
        });
    }
}
