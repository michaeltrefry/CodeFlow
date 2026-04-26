using CodeFlow.Api.Mcp;
using CodeFlow.Api.TraceEvents;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.Validation.Pipeline.Rules;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Host;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json.Serialization;

namespace CodeFlow.Api;

public static class ApiServiceCollectionExtensions
{
    public const string CodeFlowCorsPolicy = "CodeFlowUi";

    public static IServiceCollection AddCodeFlowApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddRouting();
        services.AddEndpointsApiExplorer();

        services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? new[] { "http://localhost:4200" };

        services.AddCors(corsOptions =>
        {
            corsOptions.AddPolicy(CodeFlowCorsPolicy, builder => builder
                .WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
        });

        services.AddSingleton<TraceEventBroker>();

        services.Configure<McpEndpointPolicyOptions>(
            configuration.GetSection(McpEndpointPolicyOptions.SectionName));
        services.AddSingleton<IMcpEndpointPolicy, McpEndpointPolicy>();
        services.AddScoped<IWorkflowPackageResolver, WorkflowPackageResolver>();
        services.AddScoped<IWorkflowPackageImporter, WorkflowPackageImporter>();

        // Workflow validation pipeline (F1). Rules are scoped because they take a per-request
        // DbContext via WorkflowValidationContext; the pipeline itself is scoped so it picks up
        // the correct DI scope's rule instances.
        services.AddScoped<IWorkflowValidationRule, StartNodeAdvisoryRule>();
        services.AddScoped<IWorkflowValidationRule, PortCouplingRule>();
        services.AddScoped<IWorkflowValidationRule, RoleAssignmentRule>();
        services.AddScoped<IWorkflowValidationRule, BackedgeRule>();
        services.AddScoped<IWorkflowValidationRule, PromptLintRule>();
        services.AddScoped<WorkflowValidationPipeline>();

        // Authoring telemetry (O1). Singleton sink — stateless logger wrapper with stable event
        // names. Substituted with a recording fake in tests.
        services.AddSingleton<IAuthoringTelemetry, LoggerAuthoringTelemetry>();

        return services;
    }

    public static IServiceCollection AddCodeFlowApiBus(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddCodeFlowBus(configuration, x =>
        {
            x.AddConsumer<TraceInvokeRequestedObserver, TraceInvokeRequestedObserverDefinition>();
            x.AddConsumer<TraceInvocationCompletedObserver, TraceInvocationCompletedObserverDefinition>();
        });

        return services;
    }

    public static IApplicationBuilder UseCodeFlowCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseCors(CodeFlowCorsPolicy);
        return app;
    }

    /// <summary>
    /// Configure ForwardedHeaders so the API trusts X-Forwarded-Proto/Host/For from the host's
    /// reverse proxy (Caddy in production). The proxy is in a different network namespace than
    /// the container, so the per-request RemoteIpAddress is whatever Docker's bridge presents —
    /// which is not in the default trusted list (127.0.0.1, ::1). KnownNetworks/KnownProxies are
    /// cleared because the API is bound only to 127.0.0.1 / the trefry-network in production,
    /// so the only callers that can reach it are already trusted.
    /// </summary>
    public static IServiceCollection AddCodeFlowForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;

            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            // Optional override: operators can constrain who is trusted to set the headers by
            // listing IPs/CIDRs in ForwardedHeaders:KnownProxies / KnownNetworks. When unset we
            // intentionally trust any upstream because the API is not exposed publicly.
            var knownProxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>();
            if (knownProxies is not null)
            {
                foreach (var proxy in knownProxies)
                {
                    if (IPAddress.TryParse(proxy, out var address))
                    {
                        options.KnownProxies.Add(address);
                    }
                }
            }

            var knownNetworks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>();
            if (knownNetworks is not null)
            {
                foreach (var network in knownNetworks)
                {
                    if (System.Net.IPNetwork.TryParse(network, out var parsed))
                    {
                        options.KnownIPNetworks.Add(parsed);
                    }
                }
            }
        });

        return services;
    }
}
