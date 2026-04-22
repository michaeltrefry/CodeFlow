namespace CodeFlow.Api.Auth;

public interface IPermissionChecker
{
    /// <summary>
    /// Preferred async entry point — lets implementations that need to call an external
    /// PermissionsApi do so without blocking on a thread-pool thread via .GetAwaiter().GetResult().
    /// </summary>
    Task<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kept for tests and callers that can't go async; implementations that call remote services
    /// should avoid this method on the request hot path.
    /// </summary>
    bool HasPermission(ICurrentUser user, string permission);
}
