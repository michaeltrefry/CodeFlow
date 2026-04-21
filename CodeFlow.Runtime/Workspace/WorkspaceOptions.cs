namespace CodeFlow.Runtime.Workspace;

public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspace";

    public const string CacheDirectoryName = "cache";

    public const string WorkDirectoryName = "work";

    public string Root { get; set; } = string.Empty;

    public TimeSpan WorktreeTtl { get; set; } = TimeSpan.FromHours(24);

    public long ReadMaxBytes { get; set; } = 512 * 1024;

    public int ExecTimeoutSeconds { get; set; } = 600;

    public long ExecOutputMaxBytes { get; set; } = 1024 * 1024;

    public TimeSpan GitCommandTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public IList<string> ExecEnvAllowlist { get; set; } = new List<string>
    {
        "PATH",
        "HOME",
        "LANG",
        "LC_ALL",
        "USER",
        "TMPDIR",
        "SHELL",
    };

    public long DiskUsageWarnBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public TimeSpan DiskUsageCacheDuration { get; set; } = TimeSpan.FromSeconds(60);

    public string CachePath => Path.Combine(Root, CacheDirectoryName);

    public string WorkPath => Path.Combine(Root, WorkDirectoryName);
}
