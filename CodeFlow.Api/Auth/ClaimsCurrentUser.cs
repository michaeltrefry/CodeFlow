using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace CodeFlow.Api.Auth;

public sealed class ClaimsCurrentUser : ICurrentUser
{
    private readonly ClaimsPrincipal principal;
    private readonly AuthOptions options;
    private readonly Lazy<IReadOnlyList<string>> roles;

    public ClaimsCurrentUser(IHttpContextAccessor httpContextAccessor, IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(options);

        this.principal = httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        this.options = options.Value;
        // Registered as scoped, so the Lazy lives for the request and Roles is extracted once
        // no matter how many permission checks fire on the same request.
        this.roles = new Lazy<IReadOnlyList<string>>(ExtractRoles, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAuthenticated => principal.Identity?.IsAuthenticated ?? false;

    public string? Id => principal.FindFirst(options.SubjectClaim)?.Value
        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public string? Email => principal.FindFirst(options.EmailClaim)?.Value
        ?? principal.FindFirst(ClaimTypes.Email)?.Value;

    public string? Name => principal.FindFirst(options.NameClaim)?.Value
        ?? principal.FindFirst(ClaimTypes.Name)?.Value
        ?? Email;

    public IReadOnlyList<string> Roles => roles.Value;

    private IReadOnlyList<string> ExtractRoles()
    {
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in principal.FindAll(options.RolesClaim))
        {
            extracted.Add(claim.Value);
        }

        foreach (var claim in principal.FindAll(ClaimTypes.Role))
        {
            extracted.Add(claim.Value);
        }

        return extracted.ToArray();
    }
}
