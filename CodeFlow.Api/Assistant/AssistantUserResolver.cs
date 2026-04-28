using CodeFlow.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace CodeFlow.Api.Assistant;

/// <summary>
/// Resolves the user id used to scope assistant conversations. Authenticated callers get their
/// claims subject id. Anonymous callers get a stable, browser-scoped synthetic id from the
/// <c>cf_anon_id</c> cookie (created on first visit). This drives HAA-6 demo mode: the homepage
/// route lets logged-out users chat with the assistant without tool access, but conversations
/// persist across reloads in the same browser.
/// </summary>
public interface IAssistantUserResolver
{
    /// <summary>
    /// Resolves the assistant user id for the current request. Returns null only if
    /// <paramref name="allowAnonymous"/> is false AND the user is unauthenticated.
    /// </summary>
    string? Resolve(HttpContext httpContext, bool allowAnonymous);

    /// <summary>True when the id was minted for an anonymous demo-mode caller.</summary>
    bool IsDemoUser(string? userId);
}

public sealed class AssistantUserResolver : IAssistantUserResolver
{
    public const string AnonymousIdPrefix = "anon:";
    public const string AnonymousCookieName = "cf_anon_id";
    private static readonly TimeSpan AnonymousCookieLifetime = TimeSpan.FromDays(30);

    private readonly ICurrentUser currentUser;
    private readonly IHostEnvironment environment;

    public AssistantUserResolver(ICurrentUser currentUser, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(environment);
        this.currentUser = currentUser;
        this.environment = environment;
    }

    public string? Resolve(HttpContext httpContext, bool allowAnonymous)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (currentUser.IsAuthenticated && !string.IsNullOrWhiteSpace(currentUser.Id))
        {
            return currentUser.Id;
        }

        if (!allowAnonymous)
        {
            return null;
        }

        return ResolveOrCreateAnonymousId(httpContext);
    }

    public bool IsDemoUser(string? userId) =>
        !string.IsNullOrEmpty(userId) && userId.StartsWith(AnonymousIdPrefix, StringComparison.Ordinal);

    private string ResolveOrCreateAnonymousId(HttpContext httpContext)
    {
        if (httpContext.Request.Cookies.TryGetValue(AnonymousCookieName, out var existing) &&
            Guid.TryParse(existing, out _))
        {
            return AnonymousIdPrefix + existing;
        }

        var fresh = Guid.NewGuid().ToString("D");
        httpContext.Response.Cookies.Append(AnonymousCookieName, fresh, new CookieOptions
        {
            HttpOnly = true,
            // Tests and local dev run over plain HTTP; only require Secure in Production so the
            // cookie isn't dropped by the dev server.
            Secure = environment.IsProduction(),
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.Add(AnonymousCookieLifetime),
            IsEssential = true,
            Path = "/"
        });
        return AnonymousIdPrefix + fresh;
    }
}
