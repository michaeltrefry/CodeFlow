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

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
