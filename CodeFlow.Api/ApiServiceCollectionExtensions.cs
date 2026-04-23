using CodeFlow.Api.Mcp;
using CodeFlow.Api.TraceEvents;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Host;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
}
