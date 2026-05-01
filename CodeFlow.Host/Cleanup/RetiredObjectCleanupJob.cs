using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Host.Cleanup;

internal sealed class RetiredObjectCleanupJob : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<CleanupJobsOptions> options;
    private readonly ILogger<RetiredObjectCleanupJob> logger;

    public RetiredObjectCleanupJob(
        IServiceScopeFactory scopeFactory,
        IOptions<CleanupJobsOptions> options,
        ILogger<RetiredObjectCleanupJob> logger)
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
            if (configuredOptions.RetiredObjectCleanupEnabled)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var runner = scope.ServiceProvider.GetRequiredService<CodeFlowCleanupRunner>();
                    var result = await runner.DeleteUnreferencedRetiredObjectsAsync(stoppingToken);
                    var totalDeleted = result.DeletedWorkflows + result.DeletedAgents + result.DeletedRoles;
                    if (totalDeleted > 0)
                    {
                        logger.LogInformation(
                            "Retired-object cleanup deleted {WorkflowCount} workflows, {AgentCount} agents, and {RoleCount} roles.",
                            result.DeletedWorkflows,
                            result.DeletedAgents,
                            result.DeletedRoles);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected failure in retired-object cleanup; continuing.");
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
