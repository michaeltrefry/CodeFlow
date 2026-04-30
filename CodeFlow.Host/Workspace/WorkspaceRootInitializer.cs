using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Host.Workspace;

internal sealed class WorkspaceRootInitializer : IHostedService
{
    private readonly WorkspaceOptions options;
    private readonly ILogger<WorkspaceRootInitializer> logger;

    public WorkspaceRootInitializer(
        IOptions<WorkspaceOptions> options,
        ILogger<WorkspaceRootInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options.Value;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var root = options.Root;
        var cachePath = options.CachePath;
        var workPath = options.WorkPath;

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(cachePath);
            Directory.CreateDirectory(workPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to create workspace directories under '{root}'. Configure CodeFlow:Workspace:Root to a writable path.", ex);
        }

        var probeFile = Path.Combine(root, ".codeflow-write-probe");
        try
        {
            File.WriteAllText(probeFile, "probe");
            File.ReadAllText(probeFile);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Workspace root '{root}' is not writable by this process. Configure CodeFlow:Workspace:Root to a writable path.", ex);
        }
        finally
        {
            try
            {
                File.Delete(probeFile);
            }
            catch
            {
            }
        }

        logger.LogInformation(
            "Workspace root initialized at {Root} (cache: {Cache}, work: {Work}).",
            root,
            cachePath,
            workPath);

        // Probe AssistantWorkspaceRoot separately. We *warn* rather than throw because the homepage
        // assistant runs fine without an assignable role — only role-tool host adapters need this
        // path to be writable. A loud startup warning beats a silent per-conversation failure that
        // gets swallowed in CodeFlowAssistant.ResolveWorkspace and only surfaces as missing tools.
        ProbeAssistantWorkspaceRoot();

        return Task.CompletedTask;
    }

    private void ProbeAssistantWorkspaceRoot()
    {
        var assistantRoot = string.IsNullOrWhiteSpace(options.AssistantWorkspaceRoot)
            ? WorkspaceOptions.DefaultAssistantWorkspaceRoot
            : options.AssistantWorkspaceRoot;

        try
        {
            Directory.CreateDirectory(assistantRoot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Assistant workspace root '{AssistantRoot}' could not be created. Per-conversation host tools will not work until this is resolved. "
                + "Set Workspace__AssistantWorkspaceRoot to a writable path (default '{Default}' is a container path).",
                assistantRoot, WorkspaceOptions.DefaultAssistantWorkspaceRoot);
            return;
        }

        var assistantProbeFile = Path.Combine(assistantRoot, ".codeflow-assistant-write-probe");
        try
        {
            File.WriteAllText(assistantProbeFile, "probe");
            File.ReadAllText(assistantProbeFile);
            logger.LogInformation(
                "Assistant workspace root initialized at {AssistantRoot}.",
                assistantRoot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Assistant workspace root '{AssistantRoot}' is not writable by this process. Per-conversation host tools will not work until this is resolved. "
                + "On the standard prod layout, ensure the bind-mounted host dir (CODEFLOW_ASSISTANT_DIR) is owned by the api process uid.",
                assistantRoot);
        }
        finally
        {
            try
            {
                File.Delete(assistantProbeFile);
            }
            catch
            {
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
