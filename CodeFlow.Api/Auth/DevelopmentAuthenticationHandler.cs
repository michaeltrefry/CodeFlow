using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace CodeFlow.Api.Auth;

public sealed class DevelopmentAuthenticationOptions : AuthenticationSchemeOptions
{
}

public sealed class DevelopmentAuthenticationHandler : AuthenticationHandler<DevelopmentAuthenticationOptions>
{
    public const string SchemeName = "Development";

    private readonly AuthOptions authOptions;

    public DevelopmentAuthenticationHandler(
        IOptionsMonitor<DevelopmentAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AuthOptions> authOptions)
        : base(options, logger, encoder)
    {
        this.authOptions = authOptions.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(authOptions.SubjectClaim, authOptions.DevelopmentUserId),
            new(authOptions.EmailClaim, authOptions.DevelopmentUserEmail),
            new(authOptions.NameClaim, authOptions.DevelopmentUserName),
            new(ClaimTypes.NameIdentifier, authOptions.DevelopmentUserId),
            new(ClaimTypes.Email, authOptions.DevelopmentUserEmail),
            new(ClaimTypes.Name, authOptions.DevelopmentUserName)
        };

        foreach (var role in authOptions.DevelopmentRoles)
        {
            claims.Add(new Claim(authOptions.RolesClaim, role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, authOptions.NameClaim, authOptions.RolesClaim);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
