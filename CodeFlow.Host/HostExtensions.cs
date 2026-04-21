using CodeFlow.Host.Workspace;
using CodeFlow.Orchestration;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Anthropic;
using CodeFlow.Runtime.LMStudio;
using CodeFlow.Runtime.Mcp;
using CodeFlow.Runtime.OpenAI;
using CodeFlow.Runtime.Workspace;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Net.Http;

namespace CodeFlow.Host;

public static class HostExtensions
{
    public static IServiceCollection AddCodeFlowHost(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return AddCodeFlowHost(services, configuration, configureBus: null);
    }

    public static IServiceCollection AddCodeFlowHost(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureBus)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddCodeFlowInfrastructure(configuration);

        services.AddCodeFlowBus(configuration, x =>
        {
            x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();

            x.AddSagaStateMachine<WorkflowSagaStateMachine, WorkflowSagaStateEntity, WorkflowSagaStateMachineDefinition>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<CodeFlowDbContext>();
                    r.UseMySql();
                });

            configureBus?.Invoke(x);
        });

        return services;
    }

    public static IServiceCollection AddCodeFlowInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var artifactOptions = ResolveArtifactOptions(configuration);
        var openAiOptions = ResolveOpenAiOptions(configuration);
        var anthropicOptions = ResolveAnthropicOptions(configuration);
        var lmStudioOptions = ResolveLmStudioOptions(configuration);
        var secretsOptions = ResolveSecretsOptions(configuration);

        services.AddSingleton(artifactOptions);
        services.AddSingleton(openAiOptions);
        services.AddSingleton(anthropicOptions);
        services.AddSingleton(lmStudioOptions);
        services.AddSingleton(secretsOptions);
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        services.AddDbContext<CodeFlowDbContext>(builder =>
        {
            var connectionString = configuration[CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable]
                ?? CodeFlowPersistenceDefaults.LocalDevelopmentConnectionString;
            CodeFlowDbContextOptions.Configure(builder, connectionString);
        });

        services.AddSingleton<IArtifactStore, FileSystemArtifactStore>();
        services.AddScoped<IAgentConfigRepository, AgentConfigRepository>();
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IMcpServerRepository, McpServerRepository>();
        services.AddScoped<IAgentRoleRepository, AgentRoleRepository>();
        services.AddScoped<ISkillRepository, SkillRepository>();
        services.AddScoped<IRoleResolutionService, RoleResolutionService>();
        services.AddScoped<IGitHostSettingsRepository, GitHostSettingsRepository>();
        services.AddHttpClient<IGitHostVerifier, GitHostVerifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddSingleton<ModelClientRegistry>(provider => BuildModelClientRegistry(provider));
        services.AddSingleton<ContextAssembler>();
        services.AddSingleton<HostToolProvider>();
        services.AddSingleton<Agent>();
        services.AddSingleton<IAgentInvoker>(provider => provider.GetRequiredService<Agent>());

        services.AddSingleton<IMcpSessionFactory, ModelContextProtocolSessionFactory>();
        services.TryAddSingleton<IMcpConnectionInfoProvider, NullMcpConnectionInfoProvider>();
        services.AddSingleton<IMcpClient, DefaultMcpClient>();
        services.AddSingleton<McpToolDiscovery>();

        services.AddCodeFlowWorkspace(configuration);

        return services;
    }

    public static IServiceCollection AddCodeFlowBus(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureBus = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var rabbitMqOptions = ResolveRabbitMqOptions(configuration);
        services.AddSingleton(rabbitMqOptions);

        services.AddMassTransit(x =>
        {
            x.SetKebabCaseEndpointNameFormatter();

            configureBus?.Invoke(x);

            x.AddEntityFrameworkOutbox<CodeFlowDbContext>(o =>
            {
                o.UseMySql();
                o.UseBusOutbox();
            });

            x.AddConfigureEndpointsCallback((context, _, cfg) =>
            {
                cfg.UseMessageRetry(r => r.Exponential(
                    retryLimit: 8,
                    minInterval: TimeSpan.FromMilliseconds(200),
                    maxInterval: TimeSpan.FromSeconds(5),
                    intervalDelta: TimeSpan.FromMilliseconds(500)));
                cfg.UseEntityFrameworkOutbox<CodeFlowDbContext>(context);

                if (rabbitMqOptions.ConsumerConcurrencyLimit is int concurrencyLimit)
                {
                    cfg.ConcurrentMessageLimit = concurrencyLimit;
                }
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                var hostUri = new Uri(
                    $"amqp://{rabbitMqOptions.Host}:{rabbitMqOptions.Port}/{Uri.EscapeDataString(rabbitMqOptions.VirtualHost)}");
                cfg.Host(hostUri, h =>
                {
                    h.Username(rabbitMqOptions.Username);
                    h.Password(rabbitMqOptions.Password);
                });

                if (rabbitMqOptions.PrefetchCount is ushort prefetchCount)
                {
                    cfg.PrefetchCount = prefetchCount;
                }

                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    public static async Task ApplyDatabaseMigrationsAsync(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public static async Task ApplyDatabaseMigrationsAsync(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    private static FileSystemArtifactStoreOptions ResolveArtifactOptions(IConfiguration configuration)
    {
        var configuredRootDirectory = configuration
            .GetSection(CodeFlowHostDefaults.ArtifactsSectionName)["RootDirectory"];

        if (!string.IsNullOrWhiteSpace(configuredRootDirectory))
        {
            return new FileSystemArtifactStoreOptions(configuredRootDirectory);
        }

        return new FileSystemArtifactStoreOptions(
            Path.Combine(Environment.CurrentDirectory, "artifacts"));
    }

    private static ModelClientRegistry BuildModelClientRegistry(IServiceProvider provider)
    {
        var registrations = new List<ModelClientRegistration>();
        var openAiOptions = provider.GetRequiredService<OpenAIModelClientOptions>();
        var anthropicOptions = provider.GetRequiredService<AnthropicModelClientOptions>();
        var lmStudioOptions = provider.GetRequiredService<LMStudioModelClientOptions>();

        if (!string.IsNullOrWhiteSpace(openAiOptions.ApiKey))
        {
            registrations.Add(new ModelClientRegistration(
                "openai",
                new OpenAIModelClient(new HttpClient(), openAiOptions)));
        }

        if (!string.IsNullOrWhiteSpace(anthropicOptions.ApiKey))
        {
            registrations.Add(new ModelClientRegistration(
                "anthropic",
                new AnthropicModelClient(new HttpClient(), anthropicOptions)));
        }

        registrations.Add(new ModelClientRegistration(
            "lmstudio",
            new LMStudioModelClient(new HttpClient(), lmStudioOptions)));

        return new ModelClientRegistry(registrations);
    }

    private static RabbitMqTransportOptions ResolveRabbitMqOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(CodeFlowHostDefaults.RabbitMqSectionName);

        return new RabbitMqTransportOptions
        {
            Host = section["Host"] ?? "127.0.0.1",
            Port = ParseInt(section["Port"], 5673),
            VirtualHost = section["VirtualHost"] ?? "codeflow",
            Username = section["Username"] ?? "codeflow",
            Password = section["Password"] ?? "codeflow_dev",
            PrefetchCount = ParseUShort(section["PrefetchCount"]),
            ConsumerConcurrencyLimit = ParseNullableInt(section["ConsumerConcurrencyLimit"])
        };
    }

    private static OpenAIModelClientOptions ResolveOpenAiOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(CodeFlowHostDefaults.OpenAiSectionName);

        return new OpenAIModelClientOptions
        {
            ApiKey = section["ApiKey"] ?? string.Empty,
            ResponsesEndpoint = ParseUri(section["ResponsesEndpoint"], "https://api.openai.com/v1/responses"),
            MaxRetryAttempts = ParseInt(section["MaxRetryAttempts"], 3),
            InitialRetryDelay = ParseTimeSpan(section["InitialRetryDelay"], TimeSpan.FromSeconds(1))
        };
    }

    private static AnthropicModelClientOptions ResolveAnthropicOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(CodeFlowHostDefaults.AnthropicSectionName);

        return new AnthropicModelClientOptions
        {
            ApiKey = section["ApiKey"] ?? string.Empty,
            MessagesEndpoint = ParseUri(section["MessagesEndpoint"], "https://api.anthropic.com/v1/messages"),
            ApiVersion = section["ApiVersion"] ?? "2023-06-01",
            MaxRetryAttempts = ParseInt(section["MaxRetryAttempts"], 3),
            InitialRetryDelay = ParseTimeSpan(section["InitialRetryDelay"], TimeSpan.FromSeconds(1))
        };
    }

    private static SecretsOptions ResolveSecretsOptions(IConfiguration configuration)
    {
        var masterKeyBase64 = configuration
            .GetSection(CodeFlowHostDefaults.SecretsSectionName)["MasterKey"];

        return SecretsOptions.FromBase64(masterKeyBase64 ?? string.Empty);
    }

    private static LMStudioModelClientOptions ResolveLmStudioOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(CodeFlowHostDefaults.LmStudioSectionName);

        return new LMStudioModelClientOptions
        {
            ApiKey = section["ApiKey"] ?? string.Empty,
            ResponsesEndpoint = ParseUri(section["ResponsesEndpoint"], "http://localhost:1234/v1/responses"),
            MaxRetryAttempts = ParseInt(section["MaxRetryAttempts"], 3),
            InitialRetryDelay = ParseTimeSpan(section["InitialRetryDelay"], TimeSpan.FromSeconds(1))
        };
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static ushort? ParseUShort(string? value)
    {
        return ushort.TryParse(value, out var parsed) ? parsed : null;
    }

    private static TimeSpan ParseTimeSpan(string? value, TimeSpan fallback)
    {
        return TimeSpan.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static Uri ParseUri(string? value, string fallback)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return parsed;
        }

        return new Uri(fallback);
    }
}
