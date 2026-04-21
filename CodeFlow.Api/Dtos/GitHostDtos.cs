using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Api.Dtos;

public sealed record GitHostSettingsResponse(
    GitHostMode Mode,
    string? BaseUrl,
    bool HasToken,
    DateTime? LastVerifiedAtUtc,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record GitHostSettingsRequest(
    GitHostMode Mode,
    string? BaseUrl,
    string? Token);

public sealed record GitHostVerifyResponse(
    bool Success,
    DateTime? LastVerifiedAtUtc,
    string? Error);
