using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Persistence;

public sealed class GitHostSettingsEntity
{
    public const string SingletonKey = "current";

    public string Key { get; set; } = SingletonKey;

    public GitHostMode Mode { get; set; }

    public string? BaseUrl { get; set; }

    public byte[] EncryptedToken { get; set; } = [];

    public string? WorkingDirectoryRoot { get; set; }

    public int? WorkingDirectoryMaxAgeDays { get; set; }

    public DateTime? LastVerifiedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
