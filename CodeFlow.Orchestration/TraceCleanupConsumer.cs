using System.Text.Json;
using CodeFlow.Contracts;
using CodeFlow.Runtime.Container;
using CodeFlow.Runtime.Workspace;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Orchestration;

/// <summary>
/// Owns post-termination cleanup for a workflow trace — Docker container/volume/execution-
/// workspace cleanup, per-trace workdir removal, and per-trace git-credential file removal.
///
/// <para>
/// Replaces three <c>WhenEnter(Completed/Failed)</c> hooks that previously lived inside
/// <c>WorkflowSagaStateMachine</c>. Decoupling them gives the saga a single concern
/// (workflow routing) and lets cleanup policy evolve — TTLs, sandbox-VM resource shapes,
/// new resource types — without dragging the saga along.
/// </para>
///
/// <para>
/// All cleanup operations swallow their own failures and log a warning. Periodic sweep
/// services (the workdir sweep, <c>GitCredentialSweepService</c>, and orphan container
/// reconciliation) are the safety net for anything missed here.
/// </para>
/// </summary>
public sealed class TraceCleanupConsumer : IConsumer<TraceTerminated>
{
    private readonly IOptions<WorkspaceOptions> workspaceOptions;
    private readonly ILogger<TraceCleanupConsumer> logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly DockerLifecycleService? dockerLifecycle;

    public TraceCleanupConsumer(
        IOptions<WorkspaceOptions> workspaceOptions,
        ILogger<TraceCleanupConsumer> logger,
        ILoggerFactory loggerFactory,
        DockerLifecycleService? dockerLifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceOptions);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this.workspaceOptions = workspaceOptions;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
        this.dockerLifecycle = dockerLifecycle;
    }

    public async Task Consume(ConsumeContext<TraceTerminated> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;
        await CleanupContainersAsync(evt.TraceId, context.CancellationToken);
        TryCleanupHappyPathArtifacts(evt);
    }

    private async Task CleanupContainersAsync(Guid traceId, CancellationToken cancellationToken)
    {
        if (dockerLifecycle is null)
        {
            return;
        }

        try
        {
            var cleanup = await dockerLifecycle.CleanupWorkflowAsync(traceId, cancellationToken);
            if (cleanup.RemovedContainers > 0
                || cleanup.RemovedVolumes > 0
                || cleanup.RemovedExecutionWorkspaces > 0)
            {
                logger.LogInformation(
                    "Cleaned up {ContainerCount} container(s), {VolumeCount} cache volume(s), and {WorkspaceCount} execution workspace(s) for workflow trace {TraceId}.",
                    cleanup.RemovedContainers,
                    cleanup.RemovedVolumes,
                    cleanup.RemovedExecutionWorkspaces,
                    traceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to clean up workflow-scoped Docker resources for trace {TraceId}; orphan sweep should retry later.",
                traceId);
        }
    }

    /// <summary>
    /// Happy-path workdir + git-credential cleanup. Fires only when (a) the saga is the top of
    /// its trace tree (<c>ParentTraceId is null</c>) and (b) every entry in
    /// <c>workflow.repositories</c> has a non-empty <c>prUrl</c>. Otherwise the workdir is left
    /// in place so an operator can inspect what went wrong; the periodic workdir sweep catches
    /// anything genuinely orphaned past the configured TTL.
    /// </summary>
    private void TryCleanupHappyPathArtifacts(TraceTerminated evt)
    {
        if (evt.ParentTraceId is not null)
        {
            return;
        }

        if (!AllRepositoriesHavePrUrl(evt.WorkflowInputsJson))
        {
            return;
        }

        var sagaLogger = loggerFactory.CreateLogger("CodeFlow.Orchestration.TraceCleanupConsumer.Workdir");
        TraceWorkdirCleanup.TryRemove(
            workspaceOptions.Value.WorkingDirectoryRoot,
            evt.TraceId,
            sagaLogger);
        // sc-660: per-trace git-credential file removed on the same happy-path tick as the
        // workdir; the periodic GitCredentialSweepService is the safety net for anything left.
        GitCredentialFile.TryRemove(
            workspaceOptions.Value.GitCredentialRoot,
            evt.TraceId,
            sagaLogger);
    }

    /// <summary>
    /// Returns true iff <paramref name="inputsJson"/> contains a non-empty <c>repositories</c>
    /// array AND every entry has a non-empty <c>prUrl</c> string. Any other shape returns false
    /// (workflow had no repos, some repo failed to publish, or the field is missing).
    /// Public for unit-testability — small pure predicate, no security implications.
    /// </summary>
    public static bool AllRepositoriesHavePrUrl(string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(inputsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(inputsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("repositories", out var repos)
                || repos.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var seen = false;
            foreach (var entry in repos.EnumerateArray())
            {
                seen = true;
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("prUrl", out var prUrl)
                    || prUrl.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(prUrl.GetString()))
                {
                    return false;
                }
            }

            return seen;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
