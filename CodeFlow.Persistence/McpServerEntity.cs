using CodeFlow.Runtime.Mcp;

namespace CodeFlow.Persistence;

public sealed class McpServerEntity
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public McpTransportKind Transport { get; set; }

    public string EndpointUrl { get; set; } = null!;

    public byte[]? BearerTokenCipher { get; set; }

    public McpServerHealthStatus HealthStatus { get; set; } = McpServerHealthStatus.Unverified;

    public DateTime? LastVerifiedAtUtc { get; set; }

    public string? LastVerificationError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    public bool IsArchived { get; set; }
}
