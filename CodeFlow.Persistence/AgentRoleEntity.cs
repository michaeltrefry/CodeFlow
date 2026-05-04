namespace CodeFlow.Persistence;

public sealed class AgentRoleEntity
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string? Description { get; set; }

    public string TagsJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    public bool IsArchived { get; set; }

    public bool IsRetired { get; set; }

    /// <summary>
    /// True for roles seeded by <see cref="SystemAgentRoleSeeder"/> on platform startup.
    /// System-managed roles cannot be archived, renamed, or have their grants edited via the
    /// API — the platform keeps them in sync with the host-tool catalog. Authors who want a
    /// variant copy the role to a new key.
    /// </summary>
    public bool IsSystemManaged { get; set; }
}
