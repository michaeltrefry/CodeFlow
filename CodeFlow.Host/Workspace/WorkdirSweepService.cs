using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Host.Workspace;

/// <summary>
/// Periodic sweep that catches orphan trace workdirs (failed runs, abandoned work, anything the
/// trace-delete and happy-path cleanup hooks missed). Reads <see cref="GitHostSettings"/> on each
/// tick to pick up live config changes without a restart. No-ops cleanly when
/// <c>WorkingDirectoryRoot</c> is unset.
/// </summary>
internal sealed class WorkdirSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    // First sweep runs ~30 seconds after startup so the host has time to settle and we surface
    // any "root not writable" issue early in operator-visible logs.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<WorkdirSweepService> logger;
    private readonly TimeProvider timeProvider;

    public WorkdirSweepService(
        IServiceScopeFactory scopeFactory,
        ILogger<WorkdirSweepService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
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
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Last-resort guard. Individual file failures already get logged by WorkdirSweep;
                // this catches anything that escaped (bad settings query, lost DB connection,
                // etc.) so the loop survives transient outages.
                logger.LogError(ex, "Unexpected failure in workdir sweep cycle; continuing.");
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

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGitHostSettingsRepository>();
        var settings = await repo.GetAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(settings?.WorkingDirectoryRoot))
        {
            return;
        }

        var maxAgeDays = settings.WorkingDirectoryMaxAgeDays ?? WorkdirSweep.DefaultMaxAgeDays;
        var maxAge = TimeSpan.FromDays(maxAgeDays);

        var deleted = WorkdirSweep.Sweep(
            settings.WorkingDirectoryRoot,
            maxAge,
            timeProvider.GetUtcNow(),
            logger);

        if (deleted > 0)
        {
            logger.LogInformation(
                "Workdir sweep removed {Count} stale entries from {Root} (TTL {Days}d).",
                deleted,
                settings.WorkingDirectoryRoot,
                maxAgeDays);
        }
    }
}
