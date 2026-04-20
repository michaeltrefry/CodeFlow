namespace CodeFlow.Api.Auth;

public interface IPermissionChecker
{
    bool HasPermission(ICurrentUser user, string permission);
}
