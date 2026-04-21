using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Host.Workspace;

internal sealed class WorktreeSweepHostedService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly WorktreeSweeper sweeper;
    private readonly ILogger<WorktreeSweepHostedService> logger;

    public WorktreeSweepHostedService(
        WorktreeSweeper sweeper,
        ILogger<WorktreeSweepHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(sweeper);
        ArgumentNullException.ThrowIfNull(logger);

        this.sweeper = sweeper;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await sweeper.SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Initial workspace sweep failed.");
        }

        using var timer = new PeriodicTimer(SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }

                await sweeper.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Periodic workspace sweep failed.");
            }
        }
    }
}
