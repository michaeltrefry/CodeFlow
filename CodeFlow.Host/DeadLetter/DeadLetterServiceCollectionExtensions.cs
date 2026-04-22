using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Host.DeadLetter;

public static class DeadLetterServiceCollectionExtensions
{
    public static IServiceCollection AddCodeFlowDeadLetter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DeadLetterOptions>()
            .Bind(configuration.GetSection(DeadLetterOptions.SectionName));

        services.AddSingleton<IDlqRetryTransport, RabbitMqDlqRetryTransport>();
        services.AddHttpClient<IDeadLetterStore, RabbitMqDeadLetterStore>();

        return services;
    }
}
