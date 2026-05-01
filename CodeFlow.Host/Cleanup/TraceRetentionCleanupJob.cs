using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Host.Cleanup;

internal sealed class TraceRetentionCleanupJob : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<CleanupJobsOptions> options;
    private readonly ILogger<TraceRetentionCleanupJob> logger;

    public TraceRetentionCleanupJob(
        IServiceScopeFactory scopeFactory,
        IOptions<CleanupJobsOptions> options,
        ILogger<TraceRetentionCleanupJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.scopeFactory = scopeFactory;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunLoopAsync(
            jobName: "trace retention cleanup",
            enabled: static options => options.TraceRetentionEnabled,
            runOnce: async (runner, configuredOptions, cancellationToken) =>
            {
                var result = await runner.DeleteOldTerminalTracesAsync(
                    configuredOptions.TraceRetentionDays,
                    cancellationToken);

                return result.DeletedTraces;
            },
            stoppingToken);
    }

    private async Task RunLoopAsync(
        string jobName,
        Func<CleanupJobsOptions, bool> enabled,
        Func<CodeFlowCleanupRunner, CleanupJobsOptions, CancellationToken, Task<int>> runOnce,
        CancellationToken stoppingToken)
    {
        var configuredOptions = options.Value;
        if (configuredOptions.InitialDelaySeconds > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(configuredOptions.InitialDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            configuredOptions = options.Value;
            if (enabled(configuredOptions))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var runner = scope.ServiceProvider.GetRequiredService<CodeFlowCleanupRunner>();
                    var deleted = await runOnce(runner, configuredOptions, stoppingToken);
                    if (deleted > 0)
                    {
                        logger.LogInformation(
                            "{JobName} deleted {Count} records.",
                            jobName,
                            deleted);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected failure in {JobName}; continuing.", jobName);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(configuredOptions.SweepIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
