using CodeFlow.Host.Container;
using CodeFlow.Host.Cleanup;
using CodeFlow.Host.Mcp;
using CodeFlow.Host.Web;
using CodeFlow.Host.Workspace;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration;
using CodeFlow.Orchestration.DryRun;
using CodeFlow.Orchestration.Notifications;
using CodeFlow.Orchestration.Notifications.Providers.Email;
using CodeFlow.Orchestration.Notifications.Providers.Slack;
using CodeFlow.Orchestration.Notifications.Providers.Sms;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using CodeFlow.Persistence.Authority;
using CodeFlow.Persistence.Notifications;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Anthropic;
using CodeFlow.Runtime.Container;
using CodeFlow.Runtime.LMStudio;
using CodeFlow.Runtime.Mcp;
using CodeFlow.Runtime.OpenAI;
using CodeFlow.Runtime.Web;
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
        services.AddHostedService<TraceRetentionCleanupJob>();
        services.AddHostedService<RetiredObjectCleanupJob>();

        services.AddCodeFlowBus(configuration, x =>
        {
            x.AddConsumer<AgentInvocationConsumer, AgentInvocationConsumerDefinition>();
            x.AddConsumer<HitlTaskPendingEventNotificationConsumer>();

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
        services.AddScoped<IWebSearchProviderSettingsRepository, WebSearchProviderSettingsRepository>();
        services.AddScoped<IAssistantSettingsRepository, AssistantSettingsRepository>();
        services.AddScoped<ITokenUsageRecordRepository, TokenUsageRecordRepository>();
        services.AddScoped<IRefusalEventRepository, RefusalEventRepository>();
        services.AddSingleton<IRefusalEventSink, EfRefusalEventSink>();
        services.AddScoped<IAgentInvocationAuthorityRepository, AgentInvocationAuthorityRepository>();
        services.AddScoped<IAuthorityResolver, AuthorityResolver>();
        services.AddScoped<IAuthoritySnapshotRecorder, AuthoritySnapshotRecorder>();
        services.AddScoped<CodeFlowCleanupRunner>();
        services.AddOptions<CleanupJobsOptions>()
            .Bind(configuration.GetSection(CleanupJobsOptions.SectionName))
            .Validate(options => options.Validate().Count == 0, "CleanupJobs options are invalid.")
            .ValidateOnStart();

        // Notification subsystem (epic 48). Provider adapters (Slack/Email/SMS, sc-54/55/56)
        // register their own INotificationProvider implementations; the registry below picks
        // them up via DI enumeration.
        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName));
        services.AddScoped<INotificationProviderConfigRepository, NotificationProviderConfigRepository>();
        services.AddScoped<INotificationRouteRepository, NotificationRouteRepository>();
        services.AddScoped<INotificationTemplateRepository, NotificationTemplateRepository>();
        services.AddScoped<INotificationDeliveryAttemptRepository, NotificationDeliveryAttemptRepository>();
        services.AddScoped<INotificationTemplateRenderer, ScribanNotificationTemplateRenderer>();
        services.AddScoped<INotificationProviderRegistry, NotificationProviderRegistry>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddSingleton<IHitlNotificationActionUrlBuilder, DefaultHitlNotificationActionUrlBuilder>();

        // Slack provider (sc-54). Named HttpClient so connection pooling + DNS rotation are
        // handled by IHttpClientFactory; the factory creates one provider instance per stored
        // Slack config row at dispatch time.
        services.AddHttpClient(SlackNotificationProviderFactory.HttpClientName, client =>
        {
            client.BaseAddress = SlackNotificationProviderFactory.DefaultBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<SlackNotificationProviderFactory>();
        services.AddSingleton<INotificationProviderFactory>(sp =>
            sp.GetRequiredService<SlackNotificationProviderFactory>());

        // SMS provider (sc-56). v1 ships Twilio behind the Sms-channel factory; future SMS
        // engines (Vonage, SNS, Plivo) plug in by refactoring the factory to dispatch on an
        // engine selector (mirrors EmailNotificationProviderFactory's SES vs SMTP split).
        services.AddHttpClient(SmsNotificationProviderFactory.TwilioHttpClientName, client =>
        {
            client.BaseAddress = SmsNotificationProviderFactory.TwilioDefaultBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<SmsNotificationProviderFactory>();
        services.AddSingleton<INotificationProviderFactory>(sp =>
            sp.GetRequiredService<SmsNotificationProviderFactory>());


        // Email provider (sc-55). One factory dispatches between the SES (AWS SDK) and SMTP
        // (MailKit) engines based on each config row's AdditionalConfigJson. SES provider
        // instances build their own AmazonSimpleEmailServiceV2Client; SMTP instances open one
        // SmtpClient per send.
        services.AddSingleton<EmailNotificationProviderFactory>();
        services.AddSingleton<INotificationProviderFactory>(sp =>
            sp.GetRequiredService<EmailNotificationProviderFactory>());
        services.AddHttpClient<IGitHostVerifier, GitHostVerifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddHttpClient(VcsProviderFactory.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IVcsProviderFactory, VcsProviderFactory>();
        services.AddSingleton<IPerTraceCredentialResolver, PerTraceCredentialResolver>();

        services.AddHostedService<WorkdirSweepService>();
        services.AddHostedService<GitCredentialSweepService>();

        services.AddSingleton<DbBackedLlmProviderConfigResolver>();
        services.AddSingleton<ILlmProviderConfigResolver>(sp => sp.GetRequiredService<DbBackedLlmProviderConfigResolver>());
        services.AddSingleton<ILlmProviderConfigInvalidator>(sp => sp.GetRequiredService<DbBackedLlmProviderConfigResolver>());

        // Route model clients through IHttpClientFactory so DNS rotation and handler-lifetime
        // rotation actually happens. Typed clients are transient; ModelClientRegistry's Resolve
        // factories invoke GetRequiredService on every agent invocation so each call sees a
        // fresh HttpClient (sharing a pooled-and-rotated HttpMessageHandler).
        //
        // Timeout is set to 300s (vs the HttpClient default of 100s) because complex
        // planning / review-loop turns on large contexts can legitimately take 2+ minutes
        // to first byte — observed live: a review-loop:plan-author-review node tripping the
        // default 100s ceiling at ~124s. The LLM gateways themselves enforce shorter
        // server-side limits, so this isn't a wait-forever; it just stops the client from
        // killing a slow-but-progressing call before the provider gives up.
        services.AddHttpClient<OpenAIModelClient>(c => c.Timeout = TimeSpan.FromSeconds(300))
            .AddTypedClient<OpenAIModelClient>((http, sp) =>
                new OpenAIModelClient(http, () => sp.GetRequiredService<ILlmProviderConfigResolver>().ResolveOpenAI()));
        services.AddHttpClient<AnthropicModelClient>(c => c.Timeout = TimeSpan.FromSeconds(300))
            .AddTypedClient<AnthropicModelClient>((http, sp) =>
                new AnthropicModelClient(http, () => sp.GetRequiredService<ILlmProviderConfigResolver>().ResolveAnthropic()));
        services.AddHttpClient<LMStudioModelClient>(c => c.Timeout = TimeSpan.FromSeconds(300))
            .AddTypedClient<LMStudioModelClient>((http, sp) =>
                new LMStudioModelClient(http, () => sp.GetRequiredService<ILlmProviderConfigResolver>().ResolveLMStudio()));

        services.AddSingleton<ModelClientRegistry>(provider => BuildModelClientRegistry(provider));
        services.AddSingleton<IScribanTemplateRenderer, ScribanTemplateRenderer>();
        services.AddSingleton<IDecisionTemplateRenderer, DecisionTemplateRenderer>();
        services.AddSingleton<IRetryContextBuilder, RetryContextBuilder>();
        services.AddSingleton<ContextAssembler>();
        services.AddOptions<ContainerToolOptions>()
            .Bind(configuration.GetSection(ContainerToolOptions.SectionName))
            .Validate(options => options.Validate().Count == 0, "ContainerTools options are invalid.")
            .ValidateOnStart();
        services.AddOptions<WebToolOptions>()
            .Bind(configuration.GetSection(WebToolOptions.SectionName))
            .Validate(options => options.Validate().Count == 0, "WebTools options are invalid.")
            .ValidateOnStart();
        services.AddOptions<SandboxControllerOptions>()
            .Bind(configuration.GetSection(SandboxControllerOptions.SectionName))
            .Validate(
                options =>
                {
                    // Only validate the sandbox-controller block when the backend selects it —
                    // otherwise empty defaults are fine (the path isn't used).
                    var backendOptions = configuration.GetSection(ContainerToolOptions.SectionName).Get<ContainerToolOptions>();
                    if (backendOptions?.Backend == ContainerBackend.SandboxController)
                    {
                        return options.Validate().Count == 0;
                    }
                    return true;
                },
                "SandboxController options are invalid.")
            .ValidateOnStart();

        // sc-532: backend-aware IDockerCommandRunner registration. Docker (default) keeps
        // the legacy CLI runner; SandboxController routes runs through the out-of-process
        // controller via mTLS HTTP and no-ops cleanup commands.
        services.AddSingleton<IDockerCommandRunner>(sp =>
        {
            var containerOptions = sp.GetRequiredService<IOptions<ContainerToolOptions>>().Value;
            if (containerOptions.Backend == ContainerBackend.SandboxController)
            {
                var sandboxOptions = sp.GetRequiredService<IOptions<SandboxControllerOptions>>().Value;
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(SandboxControllerHttpClientName);
                return new SandboxControllerRunner(httpClient, sandboxOptions);
            }
            return new DockerCliCommandRunner();
        });

        // mTLS-equipped HttpClient for the sandbox-controller backend. Always registered (no
        // cost when the backend isn't selected); SocketsHttpHandler loads the client cert,
        // pins the server CA + CN, and refuses anything else.
        services.AddHttpClient(SandboxControllerHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var containerOptions = sp.GetRequiredService<IOptions<ContainerToolOptions>>().Value;
                if (containerOptions.Backend != ContainerBackend.SandboxController)
                {
                    return new System.Net.Http.HttpClientHandler();
                }
                var sandboxOptions = sp.GetRequiredService<IOptions<SandboxControllerOptions>>().Value;
                return BuildSandboxControllerHandler(sandboxOptions);
            });
        services.AddSingleton<ContainerExecutionWorkspaceProvider>(sp =>
        {
            var containerOptions = sp.GetRequiredService<IOptions<ContainerToolOptions>>().Value;
            var workspaceOptions = sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value;
            var executionRoot = ResolveExecutionWorkspaceRoot(containerOptions, workspaceOptions);
            return new ContainerExecutionWorkspaceProvider(
                executionRoot,
                logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<ContainerExecutionWorkspaceProvider>>());
        });
        services.AddSingleton<DockerLifecycleService>(sp =>
            new DockerLifecycleService(
                sp.GetRequiredService<IOptions<ContainerToolOptions>>().Value,
                sp.GetRequiredService<IDockerCommandRunner>(),
                sp.GetRequiredService<ContainerExecutionWorkspaceProvider>()));
        services.AddSingleton<DockerHostToolService>(sp =>
            new DockerHostToolService(
                sp.GetRequiredService<IOptions<ContainerToolOptions>>().Value,
                sp.GetRequiredService<IDockerCommandRunner>(),
                sp.GetRequiredService<DockerLifecycleService>(),
                sp.GetRequiredService<ContainerExecutionWorkspaceProvider>()));
        // sc-537: register the in-process container sweep ONLY when the Docker
        // backend is selected. On the SandboxController backend the controller
        // owns its own per-job + periodic sweep (sc-533), and the in-process
        // service has nothing to do — register-then-noop was a v1 step;
        // Phase-1 cutover skips registration entirely. This is the prod path
        // by default; the rollback flow re-enables the legacy sweep by
        // setting ContainerTools__Backend=Docker.
        var registeredBackend = configuration
            .GetSection(ContainerToolOptions.SectionName)
            .Get<ContainerToolOptions>()?.Backend ?? ContainerBackend.Docker;
        if (registeredBackend == ContainerBackend.Docker)
        {
            services.AddHostedService<ContainerResourceSweepService>();
        }
        // sc-???: web-search adapter is admin-configurable in the DB (Web Search settings page).
        // The DB-backed dispatcher reads the active provider on each call (30s cache) and falls
        // back to NullWebSearchProvider's "search-not-configured" refusal when no provider is
        // selected — same surface as before, but operators can now wire Brave (and future
        // providers) without a code change.
        services.AddSingleton<DbBackedWebSearchProvider>();
        services.AddSingleton<IWebSearchProvider>(sp => sp.GetRequiredService<DbBackedWebSearchProvider>());
        services.AddSingleton<IWebSearchProviderInvalidator>(sp => sp.GetRequiredService<DbBackedWebSearchProvider>());
        services.AddSingleton<WebHostToolService>(sp =>
            new WebHostToolService(
                sp.GetRequiredService<IOptions<WebToolOptions>>().Value,
                searchProvider: sp.GetRequiredService<IWebSearchProvider>()));
        services.AddSingleton<HostToolProvider>(sp =>
            new HostToolProvider(
                workspaceTools: new WorkspaceHostToolService(
                    sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value),
                vcsTools: new VcsHostToolService(
                    factory: sp.GetRequiredService<IVcsProviderFactory>(),
                    gitCli: sp.GetRequiredService<IGitCli>(),
                    hostGuard: sp.GetRequiredService<IRepoUrlHostGuard>(),
                    workspaceOptions: sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value),
                containerTools: sp.GetRequiredService<DockerHostToolService>(),
                webTools: sp.GetRequiredService<WebHostToolService>()));
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

                // MassTransit's defaults (QueryDelay=30s, QueryMessageLimit=1) make idle-system
                // submissions appear to hang: the API stages AgentInvokeRequested in the outbox
                // table and then waits up to 30s for the next poll before the message reaches
                // RabbitMQ and the saga row gets written. UseBusOutbox is supposed to fire an
                // in-process Notify() to wake the delivery loop on commit, but the publish path
                // in TracesEndpoints.CreateTraceAsync doesn't run inside an EF SaveChanges, so
                // the notification can miss and we fall back to the poll. The trace-submit UI
                // comments describe a "~1s poll" budget; this is what makes that true.
                o.QueryDelay = TimeSpan.FromSeconds(1);
                o.QueryMessageLimit = 100;
                o.QueryTimeout = TimeSpan.FromSeconds(10);
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

    // sc-532: name for the typed HttpClient that talks to the sandbox-controller. Used by
    // the IDockerCommandRunner factory above when Backend == SandboxController.
    internal const string SandboxControllerHttpClientName = "sandbox-controller";

    // sc-532: build the SocketsHttpHandler for the sandbox-controller HTTP client. Loads
    // the client cert + key, validates the server cert against ONLY the configured CA
    // bundle (never the OS trust store), and pins the server cert's Common Name so a
    // swapped server cert with a different CN is rejected.
    private static System.Net.Http.HttpMessageHandler BuildSandboxControllerHandler(
        SandboxControllerOptions options)
    {
        var clientCert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(
            options.ClientCertPath,
            options.ClientKeyPath);

        // Build a trust pool containing ONLY the configured CA. We don't fall back to the
        // OS trust store — the controller's CA is internal-only and the OS store is
        // irrelevant for this private mTLS path.
        var caStore = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
        caStore.ImportFromPemFile(options.ServerCAPath);

        var chainPolicy = new System.Security.Cryptography.X509Certificates.X509ChainPolicy
        {
            TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust,
            RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            VerificationFlags = System.Security.Cryptography.X509Certificates.X509VerificationFlags.NoFlag,
        };
        chainPolicy.CustomTrustStore.AddRange(caStore);

        return new System.Net.Http.SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ClientCertificates = new System.Security.Cryptography.X509Certificates.X509CertificateCollection { clientCert },
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                CertificateChainPolicy = chainPolicy,
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    // CertificateChainPolicy restricts chain trust to the configured CA. If
                    // the chain doesn't build against it, .NET sets RemoteCertificateChainErrors
                    // here — reject. RemoteCertificateNotAvailable / RemoteCertificateNameMismatch
                    // are also fail conditions; we don't allow any of them.
                    if (errors != System.Net.Security.SslPolicyErrors.None)
                    {
                        return false;
                    }
                    if (cert is not System.Security.Cryptography.X509Certificates.X509Certificate2 cert2)
                    {
                        return false;
                    }
                    // Add the CN pin per docs/sandbox-executor.md §11.5 (defence on top of the
                    // chain check — a swapped server cert with a different CN is rejected even
                    // if it chains to the same internal CA).
                    var commonName = cert2.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                    return string.Equals(commonName, options.ServerCommonName, StringComparison.Ordinal);
                },
            },
        };
    }

    // Container execution workspaces (one {traceId:N}/ subdir per active workflow) need a
    // writable home that the sweep doesn't touch. The canonical workdir-sweep enumerates
    // direct children of WorkingDirectoryRoot but only deletes entries whose name matches the
    // {traceId:N} 32-hex shape (see WorkdirSweep), so reserved-name siblings under the root
    // are safe. We prefer placing the exec root as a sibling of the workdir (so build artifacts
    // don't share inode-table contention with traces); only when the workdir is at the
    // filesystem root (e.g. `/workspace` whose parent is `/`, which is unwritable as the app
    // uid) do we fall under the workdir itself. Operators with a non-standard layout can
    // override via ContainerTools:ExecutionWorkspaceRootPath.
    private static string ResolveExecutionWorkspaceRoot(
        ContainerToolOptions containerOptions,
        WorkspaceOptions workspaceOptions)
    {
        if (!string.IsNullOrWhiteSpace(containerOptions.ExecutionWorkspaceRootPath))
        {
            return containerOptions.ExecutionWorkspaceRootPath!;
        }

        var workingRoot = workspaceOptions.WorkingDirectoryRoot;
        if (string.IsNullOrWhiteSpace(workingRoot))
        {
            return Path.Combine(Path.GetTempPath(), "codeflow-" + containerOptions.ExecutionWorkspaceDirectoryName);
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(workingRoot));
        // Treat both an empty parent (workingRoot was a relative slug like "workspace") and
        // the filesystem root ("/" — what `Path.GetDirectoryName("/workspace")` returns) as
        // "no usable parent" and place the exec root inside the workdir itself. The sweep's
        // 32-hex-only filter keeps the reserved subdirectory off the eviction list.
        if (string.IsNullOrWhiteSpace(parent) || IsFilesystemRoot(parent))
        {
            return Path.Combine(workingRoot, containerOptions.ExecutionWorkspaceDirectoryName);
        }

        return Path.Combine(parent, containerOptions.ExecutionWorkspaceDirectoryName);
    }

    private static bool IsFilesystemRoot(string path)
    {
        // Cross-platform "is this just / or C:\?" check. Path.GetPathRoot returns the same
        // string when the input IS the root, so equality with the canonicalised form is the
        // most reliable test.
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? string.Empty);
        return string.Equals(trimmed, root, StringComparison.Ordinal);
    }
}
