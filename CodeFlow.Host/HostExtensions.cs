using CodeFlow.Host.Mcp;
using CodeFlow.Host.Workspace;
using CodeFlow.Orchestration;
using CodeFlow.Orchestration.DryRun;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using CodeFlow.Persistence.Authority;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Anthropic;
using CodeFlow.Runtime.LMStudio;
using CodeFlow.Runtime.Mcp;
using CodeFlow.Runtime.OpenAI;
using CodeFlow.Runtime.Workspace;
using LlmProviderKeys = CodeFlow.Persistence.LlmProviderKeys;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Net.Http;
using Microsoft.Extensions.Options;

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
            var connectionString = configuration[CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable];
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"CodeFlow requires a database connection string at configuration key "
                    + $"'{CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable}' "
                    + "(typically provided via environment variable of the same name). "
                    + "Set it to a MariaDB/MySQL connection string before starting the host. "
                    + "The runtime no longer falls back to a hard-coded local-dev connection string.");
            }
            CodeFlowDbContextOptions.Configure(builder, connectionString);
        });

        services.AddSingleton<IArtifactStore, FileSystemArtifactStore>();
        services.AddMemoryCache();
        services.AddSingleton<LogicNodeScriptHost>();
        services.AddSingleton<IWorkflowDataflowAnalyzer, WorkflowDataflowAnalyzer>();
        services.AddScoped<IAgentConfigRepository, AgentConfigRepository>();
        services.AddScoped<IPromptPartialRepository, PromptPartialRepository>();
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IWorkflowFixtureRepository, WorkflowFixtureRepository>();
        services.AddScoped<DryRunExecutor>();
        services.AddScoped<IMcpServerRepository, McpServerRepository>();
        services.AddScoped<IAgentRoleRepository, AgentRoleRepository>();
        services.AddScoped<ISkillRepository, SkillRepository>();
        services.AddScoped<IRoleResolutionService, RoleResolutionService>();
        services.AddScoped<IGitHostSettingsRepository, GitHostSettingsRepository>();
        services.AddScoped<ILlmProviderSettingsRepository, LlmProviderSettingsRepository>();
        services.AddScoped<IAssistantSettingsRepository, AssistantSettingsRepository>();
        services.AddScoped<ITokenUsageRecordRepository, TokenUsageRecordRepository>();
        services.AddScoped<IRefusalEventRepository, RefusalEventRepository>();
        services.AddSingleton<IRefusalEventSink, EfRefusalEventSink>();
        services.AddScoped<IAgentInvocationAuthorityRepository, AgentInvocationAuthorityRepository>();
        services.AddScoped<IAuthorityResolver, AuthorityResolver>();
        services.AddScoped<IAuthoritySnapshotRecorder, AuthoritySnapshotRecorder>();
        services.AddHttpClient<IGitHostVerifier, GitHostVerifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddHttpClient(VcsProviderFactory.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IVcsProviderFactory, VcsProviderFactory>();

        services.AddHostedService<WorkdirSweepService>();

        services.AddSingleton<DbBackedLlmProviderConfigResolver>();
        services.AddSingleton<ILlmProviderConfigResolver>(sp => sp.GetRequiredService<DbBackedLlmProviderConfigResolver>());
        services.AddSingleton<ILlmProviderConfigInvalidator>(sp => sp.GetRequiredService<DbBackedLlmProviderConfigResolver>());

        // Route model clients through IHttpClientFactory so DNS rotation and handler-lifetime
        // rotation actually happens. Typed clients are transient; ModelClientRegistry's Resolve
        // factories invoke GetRequiredService on every agent invocation so each call sees a
        // fresh HttpClient (sharing a pooled-and-rotated HttpMessageHandler).
        services.AddHttpClient<OpenAIModelClient>()
            .AddTypedClient<OpenAIModelClient>((http, sp) =>
                new OpenAIModelClient(http, () => sp.GetRequiredService<ILlmProviderConfigResolver>().ResolveOpenAI()));
        services.AddHttpClient<AnthropicModelClient>()
            .AddTypedClient<AnthropicModelClient>((http, sp) =>
                new AnthropicModelClient(http, () => sp.GetRequiredService<ILlmProviderConfigResolver>().ResolveAnthropic()));
        services.AddHttpClient<LMStudioModelClient>()
            .AddTypedClient<LMStudioModelClient>((http, sp) =>
                new LMStudioModelClient(http, () => sp.GetRequiredService<ILlmProviderConfigResolver>().ResolveLMStudio()));

        services.AddSingleton<ModelClientRegistry>(provider => BuildModelClientRegistry(provider));
        services.AddSingleton<IScribanTemplateRenderer, ScribanTemplateRenderer>();
        services.AddSingleton<IDecisionTemplateRenderer, DecisionTemplateRenderer>();
        services.AddSingleton<IRetryContextBuilder, RetryContextBuilder>();
        services.AddSingleton<ContextAssembler>();
        services.AddSingleton<HostToolProvider>(sp =>
            new HostToolProvider(
                workspaceTools: new WorkspaceHostToolService(
                    sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value),
                vcsTools: new VcsHostToolService(
                    sp.GetRequiredService<IVcsProviderFactory>())));
        services.AddSingleton<Agent>();
        services.AddSingleton<IAgentInvoker>(provider => provider.GetRequiredService<Agent>());

        services.AddSingleton<IMcpSessionFactory, ModelContextProtocolSessionFactory>();
        // Resolve MCP server connection info from the admin DB at invocation time. The previous
        // default (NullMcpConnectionInfoProvider, which always returned null) made every
        // role-granted MCP tool throw McpServerNotConfiguredException despite the admin UI
        // showing the server as healthy — the admin/health path bypasses this provider, so the
        // gap was invisible until role grants started reaching the homepage assistant.
        services.AddSingleton<IMcpConnectionInfoProvider, DbBackedMcpConnectionInfoProvider>();
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

            x.AddConfigureEndpointsCallback((context, endpointName, cfg) =>
            {
                cfg.UseMessageRetry(r => r.Exponential(
                    retryLimit: 8,
                    minInterval: TimeSpan.FromMilliseconds(200),
                    maxInterval: TimeSpan.FromSeconds(5),
                    intervalDelta: TimeSpan.FromMilliseconds(500)));

                // The API's temporary trace observer queues only fan events out to in-memory SSE
                // listeners and do not publish follow-up bus messages. Skipping the EF outbox
                // there avoids unnecessary InboxState/OutboxMessage contention with the real
                // workflow endpoints during trace startup.
                if (!IsTraceObserverEndpoint(endpointName))
                {
                    cfg.UseEntityFrameworkOutbox<CodeFlowDbContext>(context);
                }

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
        await SystemPromptPartialSeeder.SeedAsync(dbContext);
        await SystemAgentRoleSeeder.SeedAsync(dbContext);
    }

    public static async Task ApplyDatabaseMigrationsAsync(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        await dbContext.Database.MigrateAsync();
        await SystemPromptPartialSeeder.SeedAsync(dbContext);
        await SystemAgentRoleSeeder.SeedAsync(dbContext);
    }

    private static FileSystemArtifactStoreOptions ResolveArtifactOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(CodeFlowHostDefaults.ArtifactsSectionName);
        var configuredRootDirectory = section["RootDirectory"];
        var maxArtifactBytes = section.GetValue<long?>("MaxArtifactBytes");

        var rootDirectory = string.IsNullOrWhiteSpace(configuredRootDirectory)
            ? Path.Combine(Environment.CurrentDirectory, "artifacts")
            : configuredRootDirectory;

        return new FileSystemArtifactStoreOptions(rootDirectory, maxArtifactBytes);
    }

    private static ModelClientRegistry BuildModelClientRegistry(IServiceProvider rootProvider)
    {
        // Always register all three providers; missing credentials surface as an actionable
        // InvalidOperationException at invocation time rather than silently hiding the provider.
        var factories = new[]
        {
            new KeyValuePair<string, Func<IModelClient>>(
                LlmProviderKeys.OpenAi,
                () => rootProvider.GetRequiredService<OpenAIModelClient>()),
            new KeyValuePair<string, Func<IModelClient>>(
                LlmProviderKeys.Anthropic,
                () => rootProvider.GetRequiredService<AnthropicModelClient>()),
            new KeyValuePair<string, Func<IModelClient>>(
                LlmProviderKeys.LmStudio,
                () => rootProvider.GetRequiredService<LMStudioModelClient>()),
        };

        return new ModelClientRegistry(factories);
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

    private static bool IsTraceObserverEndpoint(string? endpointName)
    {
        return !string.IsNullOrWhiteSpace(endpointName)
            && endpointName.StartsWith("api-trace-observer-", StringComparison.OrdinalIgnoreCase);
    }
}
