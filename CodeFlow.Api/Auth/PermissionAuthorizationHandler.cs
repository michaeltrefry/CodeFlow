using Microsoft.AspNetCore.Authorization;

namespace CodeFlow.Api.Auth;

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        Permission = permission;
    }

    public string Permission { get; }
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionChecker permissionChecker;
    private readonly ICurrentUser currentUser;

    public PermissionAuthorizationHandler(IPermissionChecker permissionChecker, ICurrentUser currentUser)
    {
        this.permissionChecker = permissionChecker ?? throw new ArgumentNullException(nameof(permissionChecker));
        this.currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (permissionChecker.HasPermission(currentUser, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
