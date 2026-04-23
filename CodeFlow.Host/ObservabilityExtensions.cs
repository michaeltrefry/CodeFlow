using CodeFlow.Runtime.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CodeFlow.Host;

public static class ObservabilityExtensions
{
    public const string ObservabilitySectionName = "Observability";
    public const string DefaultServiceName = "codeflow";
    public const string MassTransitSourceName = "MassTransit";

    public static IServiceCollection AddCodeFlowObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string? serviceName = null,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ObservabilitySectionName);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        section.GetSection("OtlpHeaders").Bind(headers);

        var options = new ObservabilityOptions
        {
            ServiceName = serviceName ?? section["ServiceName"] ?? DefaultServiceName,
            EnableConsoleExporter = ParseBool(section["EnableConsoleExporter"], defaultValue: true),
            OtlpEndpoint = section["OtlpEndpoint"],
            OtlpHeaders = headers
        };

        services.AddSingleton(options);

        services.AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder.AddService(options.ServiceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(CodeFlowActivity.SourceName);
                tracing.AddSource(MassTransitSourceName);
                tracing.AddHttpClientInstrumentation();

                if (options.EnableConsoleExporter)
                {
                    tracing.AddConsoleExporter();
                }

                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                {
                    var endpoint = options.OtlpEndpoint;
                    var headerString = options.OtlpHeaders.Count == 0
                        ? null
                        : string.Join(",", options.OtlpHeaders.Select(kv => $"{kv.Key}={kv.Value}"));
                    tracing.AddOtlpExporter(exporter =>
                    {
                        exporter.Endpoint = new Uri(endpoint);
                        if (headerString is not null)
                        {
                            exporter.Headers = headerString;
                        }
                    });
                }

                configureTracing?.Invoke(tracing);
            });

        return services;
    }

    private static bool ParseBool(string? value, bool defaultValue)
    {
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}

public sealed class ObservabilityOptions
{
    public string ServiceName { get; init; } = ObservabilityExtensions.DefaultServiceName;

    public bool EnableConsoleExporter { get; init; } = true;

    public string? OtlpEndpoint { get; init; }

    public IReadOnlyDictionary<string, string> OtlpHeaders { get; init; } =
        new Dictionary<string, string>();
}
