using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeFlow.Host.Workspace;

public static class WorkspaceServiceCollectionExtensions
{
    public static IServiceCollection AddCodeFlowWorkspace(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WorkspaceOptions>()
            .Bind(configuration.GetSection(WorkspaceOptions.SectionName))
            .PostConfigure(opts =>
            {
                if (string.IsNullOrWhiteSpace(opts.Root))
                {
                    opts.Root = Path.Combine(Environment.CurrentDirectory, "workspace");
                }

                opts.Root = Path.GetFullPath(opts.Root);
            })
            .Validate(
                opts => !string.IsNullOrWhiteSpace(opts.Root),
                "CodeFlow:Workspace:Root must be a non-empty path.")
            .Validate(
                opts => opts.WorktreeTtl > TimeSpan.Zero,
                "CodeFlow:Workspace:WorktreeTtl must be greater than zero.")
            .Validate(
                opts => opts.ReadMaxBytes > 0,
                "CodeFlow:Workspace:ReadMaxBytes must be greater than zero.")
            .Validate(
                opts => opts.ExecTimeoutSeconds > 0,
                "CodeFlow:Workspace:ExecTimeoutSeconds must be greater than zero.")
            .Validate(
                opts => opts.ExecOutputMaxBytes > 0,
                "CodeFlow:Workspace:ExecOutputMaxBytes must be greater than zero.")
            .Validate(
                opts => opts.GitCommandTimeout > TimeSpan.Zero,
                "CodeFlow:Workspace:GitCommandTimeout must be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton<IGitCli>(sp =>
            new GitCli(sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value));

        services.AddSingleton<IRepoUrlHostGuard, RepoUrlHostGuard>();
        services.AddSingleton<IGitHostTokenProvider, GitHostTokenProvider>();
        services.AddSingleton<IVcsProvider, GitHubVcsProvider>();
        services.AddSingleton<IVcsProviderFactory, VcsProviderFactory>();

        services.AddSingleton<IWorkspaceService>(sp =>
            new WorkspaceService(
                sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value,
                sp.GetRequiredService<IGitCli>(),
                sp.GetRequiredService<IRepoUrlHostGuard>()));

        services.AddHostedService<WorkspaceRootInitializer>();

        return services;
    }
}
