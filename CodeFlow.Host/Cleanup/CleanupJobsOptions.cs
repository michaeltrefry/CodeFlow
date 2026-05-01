namespace CodeFlow.Host.Cleanup;

public sealed class CleanupJobsOptions
{
    public const string SectionName = "CleanupJobs";

    public bool TraceRetentionEnabled { get; set; }

    public int TraceRetentionDays { get; set; } = 90;

    public bool RetiredObjectCleanupEnabled { get; set; }

    public int SweepIntervalMinutes { get; set; } = 60;

    public int InitialDelaySeconds { get; set; } = 30;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (TraceRetentionEnabled && (TraceRetentionDays < 1 || TraceRetentionDays > 3650))
        {
            errors.Add("CleanupJobs:TraceRetentionDays must be between 1 and 3650 when trace retention is enabled.");
        }

        if (SweepIntervalMinutes < 1 || SweepIntervalMinutes > 1440)
        {
            errors.Add("CleanupJobs:SweepIntervalMinutes must be between 1 and 1440.");
        }

        if (InitialDelaySeconds < 0 || InitialDelaySeconds > 3600)
        {
            errors.Add("CleanupJobs:InitialDelaySeconds must be between 0 and 3600.");
        }

        return errors;
    }
}
