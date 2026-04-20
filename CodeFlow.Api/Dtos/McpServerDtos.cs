using CodeFlow.Persistence;
using CodeFlow.Runtime.Mcp;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Dtos;

public sealed record McpServerCreateRequest(
    string? Key,
    string? DisplayName,
    McpTransportKind Transport,
    string? EndpointUrl,
    string? BearerToken);

public sealed record McpServerUpdateRequest(
    string? DisplayName,
    McpTransportKind Transport,
    string? EndpointUrl,
    BearerTokenPayload? BearerToken);

public sealed record BearerTokenPayload(BearerTokenAction Action, string? Value);

public sealed record McpServerResponse(
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

public sealed record McpServerToolResponse(
    long Id,
    long ServerId,
    string ToolName,
    string? Description,
    JsonNode? Parameters,
    bool IsMutating,
    DateTime SyncedAtUtc);

public sealed record McpServerVerifyResponse(
    McpServerHealthStatus HealthStatus,
    DateTime? LastVerifiedAtUtc,
    string? LastVerificationError,
    int? DiscoveredToolCount);

public sealed record McpServerRefreshResponse(
    McpServerHealthStatus HealthStatus,
    DateTime? LastVerifiedAtUtc,
    string? LastVerificationError,
    IReadOnlyList<McpServerToolResponse> Tools);
