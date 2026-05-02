using CodeFlow.Runtime.Container;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Host.Container;

internal sealed class ContainerResourceSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    private readonly DockerLifecycleService lifecycle;
    private readonly ContainerToolOptions containerOptions;
    private readonly ILogger<ContainerResourceSweepService> logger;

    public ContainerResourceSweepService(
        DockerLifecycleService lifecycle,
        IOptions<ContainerToolOptions> containerOptions,
        ILogger<ContainerResourceSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(containerOptions);
        ArgumentNullException.ThrowIfNull(logger);
        this.lifecycle = lifecycle;
        this.containerOptions = containerOptions.Value;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // sc-532: on the SandboxController backend the controller manages its own per-job
        // lifecycle (sc-533 will land controller-side cleanup in detail). The legacy in-
        // process sweep doesn't run; logging once at startup keeps operators oriented.
        if (containerOptions.Backend == ContainerBackend.SandboxController)
        {
            logger.LogInformation(
                "Container resource sweeps run on the sandbox controller under this backend; in-process sweep is disabled.");
            return;
        }

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
                if (result.RemovedContainers > 0 || result.RemovedVolumes > 0 || result.RemovedExecutionWorkspaces > 0)
                {
                    logger.LogInformation(
                        "Container resource sweep removed {ContainerCount} stale container(s), {VolumeCount} stale cache volume(s), and {WorkspaceCount} stale execution workspace(s).",
                        result.RemovedContainers,
                        result.RemovedVolumes,
                        result.RemovedExecutionWorkspaces);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (DockerCliNotAvailableException)
            {
                logger.LogInformation(
                    "docker CLI is not available on PATH; container resource sweeps are disabled for this process.");
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
