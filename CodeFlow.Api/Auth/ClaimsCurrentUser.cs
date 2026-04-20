using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace CodeFlow.Api.Auth;

public sealed class ClaimsCurrentUser : ICurrentUser
{
    private readonly ClaimsPrincipal principal;
    private readonly AuthOptions options;

    public ClaimsCurrentUser(IHttpContextAccessor httpContextAccessor, IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(options);

        this.principal = httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        this.options = options.Value;
    }

    public bool IsAuthenticated => principal.Identity?.IsAuthenticated ?? false;

    public string? Id => principal.FindFirst(options.SubjectClaim)?.Value
        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public string? Email => principal.FindFirst(options.EmailClaim)?.Value
        ?? principal.FindFirst(ClaimTypes.Email)?.Value;

    public string? Name => principal.FindFirst(options.NameClaim)?.Value
        ?? principal.FindFirst(ClaimTypes.Name)?.Value
        ?? Email;

    public IReadOnlyList<string> Roles => ExtractRoles();

    private IReadOnlyList<string> ExtractRoles()
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in principal.FindAll(options.RolesClaim))
        {
            roles.Add(claim.Value);
        }

        foreach (var claim in principal.FindAll(ClaimTypes.Role))
        {
            roles.Add(claim.Value);
        }

        return roles.ToArray();
    }
}
