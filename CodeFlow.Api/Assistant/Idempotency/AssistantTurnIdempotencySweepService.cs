using CodeFlow.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Assistant.Idempotency;

/// <summary>
/// sc-525 — Periodically deletes <c>assistant_turn_idempotency</c> rows past their TTL so the
/// table doesn't grow unbounded. The TTL is intentionally short (~10 min default); rows past
/// it can no longer serve as a retry target, so dropping them is safe.
/// </summary>
internal sealed class AssistantTurnIdempotencySweepService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<AssistantTurnIdempotencyOptions> options;
    private readonly ILogger<AssistantTurnIdempotencySweepService> logger;
    private readonly TimeProvider timeProvider;

    public AssistantTurnIdempotencySweepService(
        IServiceScopeFactory scopeFactory,
        IOptions<AssistantTurnIdempotencyOptions> options,
        ILogger<AssistantTurnIdempotencySweepService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.scopeFactory = scopeFactory;
        this.options = options;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first run so the sweep doesn't compete with startup work — same shape as
        // the surrounding cleanup background services.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = options.Value.SweepInterval;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider
                    .GetRequiredService<IAssistantTurnIdempotencyRepository>();

                var deleted = await repository.PurgeExpiredAsync(
                    timeProvider.GetUtcNow().UtcDateTime,
                    stoppingToken);

                if (deleted > 0)
                {
                    logger.LogInformation(
                        "Assistant idempotency sweep removed {DeletedCount} expired record(s).",
                        deleted);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Assistant idempotency sweep failed; retrying after the next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
