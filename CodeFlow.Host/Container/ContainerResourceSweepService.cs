using CodeFlow.Runtime.Container;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Host.Container;

internal sealed class ContainerResourceSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    private readonly DockerLifecycleService lifecycle;
    private readonly ILogger<ContainerResourceSweepService> logger;

    public ContainerResourceSweepService(
        DockerLifecycleService lifecycle,
        ILogger<ContainerResourceSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(logger);
        this.lifecycle = lifecycle;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await lifecycle.SweepOrphansAsync(stoppingToken);
                if (result.RemovedContainers > 0 || result.RemovedVolumes > 0)
                {
                    logger.LogInformation(
                        "Container resource sweep removed {ContainerCount} stale container(s) and {VolumeCount} stale cache volume(s).",
                        result.RemovedContainers,
                        result.RemovedVolumes);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Container resource sweep failed; continuing.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
