using CodeFlow.Api.Assistant;
using CodeFlow.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for <see cref="AssistantUserResolver"/> — the surface that lets HAA-6 demo mode
/// scope assistant conversations to a stable browser-cookie identity for anonymous visitors,
/// while still preferring the claims subject id for authenticated callers.
/// </summary>
public sealed class AssistantUserResolverTests
{
    [Fact]
    public void Resolve_Authenticated_ReturnsClaimsId()
    {
        var resolver = new AssistantUserResolver(
            new FakeCurrentUser(isAuthenticated: true, id: "auth0|abc123"),
            new FakeHostEnvironment(Environments.Development));
        var ctx = new DefaultHttpContext();

        var result = resolver.Resolve(ctx, allowAnonymous: true);

        result.Should().Be("auth0|abc123");
        ctx.Response.Headers.SetCookie.Should().BeEmpty(
            because: "authenticated callers must not have an anon cookie minted");
    }

    [Fact]
    public void Resolve_AnonymousAllowed_NoCookie_MintsFreshIdAndSetsCookie()
    {
        var resolver = new AssistantUserResolver(
            new FakeCurrentUser(isAuthenticated: false, id: null),
            new FakeHostEnvironment(Environments.Development));
        var ctx = new DefaultHttpContext();

        var result = resolver.Resolve(ctx, allowAnonymous: true);

        result.Should().StartWith("anon:");
        Guid.TryParse(result!["anon:".Length..], out _).Should().BeTrue();
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain("cf_anon_id=").And.Contain("httponly", because: "anon cookie must be HttpOnly");
    }

    [Fact]
    public void Resolve_AnonymousAllowed_WithExistingCookie_ReusesId()
    {
        var existingId = Guid.NewGuid().ToString("D");
        var resolver = new AssistantUserResolver(
            new FakeCurrentUser(isAuthenticated: false, id: null),
            new FakeHostEnvironment(Environments.Development));
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"cf_anon_id={existingId}";

        var result = resolver.Resolve(ctx, allowAnonymous: true);

        result.Should().Be("anon:" + existingId);
        ctx.Response.Headers.SetCookie.Should().BeEmpty(
            because: "an existing valid cookie must not be rewritten");
    }

    [Fact]
    public void Resolve_AnonymousDisallowed_NoCookie_ReturnsNull()
    {
        var resolver = new AssistantUserResolver(
            new FakeCurrentUser(isAuthenticated: false, id: null),
            new FakeHostEnvironment(Environments.Development));
        var ctx = new DefaultHttpContext();

        var result = resolver.Resolve(ctx, allowAnonymous: false);

        result.Should().BeNull();
        ctx.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_AnonymousAllowed_MalformedCookie_MintsFresh()
    {
        var resolver = new AssistantUserResolver(
            new FakeCurrentUser(isAuthenticated: false, id: null),
            new FakeHostEnvironment(Environments.Development));
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = "cf_anon_id=not-a-guid";

        var result = resolver.Resolve(ctx, allowAnonymous: true);

        result.Should().StartWith("anon:");
        Guid.TryParse(result!["anon:".Length..], out _).Should().BeTrue();
        ctx.Response.Headers.SetCookie.ToString().Should().Contain("cf_anon_id=");
    }

    [Fact]
    public void Resolve_Production_SetsSecureCookie()
    {
        var resolver = new AssistantUserResolver(
            new FakeCurrentUser(isAuthenticated: false, id: null),
            new FakeHostEnvironment(Environments.Production));
        var ctx = new DefaultHttpContext();

        resolver.Resolve(ctx, allowAnonymous: true);

        ctx.Response.Headers.SetCookie.ToString().Should().Contain("secure",
            because: "anon cookies must be Secure in production");
    }

    [Theory]
    [InlineData("anon:b3f5e7a4-1234-4567-89ab-cdef01234567", true)]
    [InlineData("auth0|abc123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDemoUser_PrefixCheck(string? userId, bool expected)
    {
        var resolver = new AssistantUserResolver(
            new FakeCurrentUser(isAuthenticated: false, id: null),
            new FakeHostEnvironment(Environments.Development));

        resolver.IsDemoUser(userId).Should().Be(expected);
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public FakeCurrentUser(bool isAuthenticated, string? id)
        {
            IsAuthenticated = isAuthenticated;
            Id = id;
        }

        public bool IsAuthenticated { get; }
        public string? Id { get; }
        public string? Email => null;
        public string? Name => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
