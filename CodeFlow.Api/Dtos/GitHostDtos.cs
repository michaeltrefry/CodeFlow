using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Api.Dtos;

public sealed record GitHostSettingsResponse(
    GitHostMode Mode,
    string? BaseUrl,
    bool HasToken,
    string? WorkingDirectoryRoot,
    DateTime? LastVerifiedAtUtc,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record GitHostSettingsRequest(
    GitHostMode Mode,
    string? BaseUrl,
    string? WorkingDirectoryRoot,
    GitHostTokenUpdateRequest? Token);

public sealed record GitHostTokenUpdateRequest(
    GitHostTokenAction Action,
    string? Value);

public sealed record GitHostVerifyResponse(
    bool Success,
    DateTime? LastVerifiedAtUtc,
    string? Error);
