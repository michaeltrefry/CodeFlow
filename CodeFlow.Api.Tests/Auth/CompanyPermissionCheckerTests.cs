using CodeFlow.Api;
using CodeFlow.Api.Auth;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Tests.Auth;

public sealed class CompanyPermissionCheckerTests
{
    [Fact]
    public void HasPermission_ReturnsTrue_WhenPermissionsApiGrantsPermission()
    {
        var checker = BuildChecker(
            new FakePermissionsApi
            {
                PermissionsByUser =
                {
                    ["user-42"] = new[] { "agents:read", "traces:read" }
                }
            },
            baseUrl: "https://permissions.internal/",
            roles: ["viewer"]);

        var granted = checker.HasPermission(
            new FakeCurrentUser("user-42", ["viewer"]),
            "agents:read");

        granted.Should().BeTrue();
    }

    [Fact]
    public void HasPermission_FallsBackToRoleMap_WhenPermissionsApiIsNotConfigured()
    {
        var checker = BuildChecker(
            new FakePermissionsApi(),
            baseUrl: null,
            roles: [CodeFlowApiDefaults.Roles.Admin]);

        var granted = checker.HasPermission(
            new FakeCurrentUser("admin-1", [CodeFlowApiDefaults.Roles.Admin]),
            CodeFlowApiDefaults.Permissions.OpsWrite);

        granted.Should().BeTrue("fallback should grant Admin role permissions when PermissionsApi URL is absent");
    }

    [Fact]
    public void HasPermission_CachesResults_AndOnlyCallsPermissionsApiOncePerUser()
    {
        var fake = new FakePermissionsApi
        {
            PermissionsByUser = { ["user-42"] = new[] { "agents:read" } }
        };
        var checker = BuildChecker(fake, baseUrl: "https://permissions.internal/", roles: ["viewer"]);
        var user = new FakeCurrentUser("user-42", ["viewer"]);

        checker.HasPermission(user, "agents:read").Should().BeTrue();
        checker.HasPermission(user, "traces:read").Should().BeFalse();
        checker.HasPermission(user, "agents:read").Should().BeTrue();

        fake.CallCountForUser("user-42").Should().Be(1);
    }

    [Fact]
    public void HasPermission_Denies_WhenPermissionsApiReturnsEmpty()
    {
        // Fail-closed: PermissionsApi is configured, so an empty result must deny rather than
        // silently widen access through the role map fallback.
        var checker = BuildChecker(
            new FakePermissionsApi(),
            baseUrl: "https://permissions.internal/",
            roles: [CodeFlowApiDefaults.Roles.Operator]);

        var granted = checker.HasPermission(
            new FakeCurrentUser("op-1", [CodeFlowApiDefaults.Roles.Operator]),
            CodeFlowApiDefaults.Permissions.OpsRead);

        granted.Should().BeFalse("PermissionsApi is authoritative when configured; empty result is deny");
    }

    [Fact]
    public void HasPermission_Denies_WhenPermissionsApiThrows()
    {
        // Fail-closed on backend error. Must also not cache the failure.
        var api = new ThrowingPermissionsApi();
        var checker = BuildChecker(
            api,
            baseUrl: "https://permissions.internal/",
            roles: [CodeFlowApiDefaults.Roles.Admin]);

        var user = new FakeCurrentUser("admin-1", [CodeFlowApiDefaults.Roles.Admin]);

        checker.HasPermission(user, CodeFlowApiDefaults.Permissions.OpsRead).Should().BeFalse();
        checker.HasPermission(user, CodeFlowApiDefaults.Permissions.OpsRead).Should().BeFalse();

        api.CallCount.Should().Be(2, "errors must not be cached so the next request retries the backend");
    }

    private static CompanyPermissionChecker BuildChecker(
        IPermissionsApiClient api,
        string? baseUrl,
        IReadOnlyList<string> roles)
    {
        _ = roles;

        var options = Options.Create(new AuthOptions
        {
            Mode = AuthMode.Company,
            Company = new CompanyAuthOptions
            {
                PermissionsApiBaseUrl = baseUrl,
                PermissionsCacheSeconds = 30
            }
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var fallback = new RoleBasedPermissionChecker(options);
        return new CompanyPermissionChecker(
            api,
            fallback,
            cache,
            NullLogger<CompanyPermissionChecker>.Instance,
            options);
    }

    private sealed class ThrowingPermissionsApi : IPermissionsApiClient
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<string>> GetPermissionsAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new HttpRequestException("simulated backend failure");
        }
    }

    private sealed class FakePermissionsApi : IPermissionsApiClient
    {
        private readonly Dictionary<string, int> callCounts = new();

        public Dictionary<string, IReadOnlyList<string>> PermissionsByUser { get; } = new();

        public int CallCountForUser(string userId)
            => callCounts.TryGetValue(userId, out var count) ? count : 0;

        public Task<IReadOnlyList<string>> GetPermissionsAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            callCounts[userId] = CallCountForUser(userId) + 1;
            var permissions = PermissionsByUser.TryGetValue(userId, out var list)
                ? list
                : Array.Empty<string>();
            return Task.FromResult(permissions);
        }
    }

    private sealed class FakeCurrentUser(string id, IReadOnlyList<string> roles) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public string? Id => id;
        public string? Email => null;
        public string? Name => null;
        public IReadOnlyList<string> Roles => roles;
    }
}
