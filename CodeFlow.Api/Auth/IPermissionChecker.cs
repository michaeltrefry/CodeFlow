namespace CodeFlow.Api.Auth;

public interface IPermissionChecker
{
    /// <summary>
    /// Async permission check — implementations that need to call an external PermissionsApi
    /// can do so without blocking on a thread-pool thread. Tests should call this directly
    /// (the previous sync overload was removed in F-019; bridging via .GetAwaiter().GetResult()
    /// caused thread starvation under load and ignored cancellation).
    /// </summary>
    Task<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken = default);
}
