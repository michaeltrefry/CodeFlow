using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Host.Workspace;

/// <summary>
/// Periodic sweep that catches orphan per-trace git-credential files (failed runs, abandoned
/// work, anything the trace-delete and happy-path cleanup hooks missed). Mirror of
/// <see cref="WorkdirSweepService"/>; the cred file lifecycle is parallel to the workdir
/// lifecycle and reuses the same operator-editable max-age TTL on
/// <see cref="GitHostSettings.WorkingDirectoryMaxAgeDays"/>.
/// </summary>
internal sealed class GitCredentialSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<GitCredentialSweepService> logger;
    private readonly TimeProvider timeProvider;
    private readonly string credentialRoot;

    public GitCredentialSweepService(
        IServiceScopeFactory scopeFactory,
        IOptions<WorkspaceOptions> workspaceOptions,
        ILogger<GitCredentialSweepService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(workspaceOptions);
        ArgumentNullException.ThrowIfNull(logger);
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.credentialRoot = workspaceOptions.Value.GitCredentialRoot;
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
                logger.LogError(ex, "Unexpected failure in git-credential sweep cycle; continuing.");
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

        var maxAgeDays = settings?.WorkingDirectoryMaxAgeDays ?? WorkdirSweep.DefaultMaxAgeDays;
        var maxAge = TimeSpan.FromDays(maxAgeDays);

        var deleted = GitCredentialFile.Sweep(
            credentialRoot,
            maxAge,
            timeProvider.GetUtcNow(),
            logger);

        if (deleted > 0)
        {
            logger.LogInformation(
                "Git-credential sweep removed {Count} stale entries from {Root} (TTL {Days}d).",
                deleted,
                credentialRoot,
                maxAgeDays);
        }
    }
}
