using CodeFlow.Runtime.Mcp;

namespace CodeFlow.Persistence;

public sealed record McpServer(
    long Id,
    string Key,
    string DisplayName,
    McpTransportKind Transport,
    string EndpointUrl,
    bool HasBearerToken,
    McpServerHealthStatus HealthStatus,
    DateTime? LastVerifiedAtUtc,
    string? LastVerificationError,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime UpdatedAtUtc,
    string? UpdatedBy,
    bool IsArchived);

public sealed record McpServerTool(
    long Id,
    long ServerId,
    string ToolName,
    string? Description,
    string? ParametersJson,
    bool IsMutating,
    DateTime SyncedAtUtc);

public sealed record McpServerCreate(
    string Key,
    string DisplayName,
    McpTransportKind Transport,
    string EndpointUrl,
    string? BearerTokenPlaintext,
    string? CreatedBy);

public sealed record McpServerUpdate(
    string DisplayName,
    McpTransportKind Transport,
    string EndpointUrl,
    BearerTokenUpdate BearerToken,
    string? UpdatedBy);

public sealed record McpServerToolWrite(
    string ToolName,
    string? Description,
    string? ParametersJson,
    bool IsMutating);

public sealed record BearerTokenUpdate(BearerTokenAction Action, string? NewPlaintext)
{
    public static BearerTokenUpdate Preserve() => new(BearerTokenAction.Preserve, null);
    public static BearerTokenUpdate Clear() => new(BearerTokenAction.Clear, null);
    public static BearerTokenUpdate Replace(string newPlaintext) => new(BearerTokenAction.Replace, newPlaintext);
}

public enum BearerTokenAction
{
    Preserve,
    Clear,
    Replace,
}
