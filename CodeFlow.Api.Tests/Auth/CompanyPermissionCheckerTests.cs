using CodeFlow.Api;
using CodeFlow.Api.Auth;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
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
    public void HasPermission_FallsBackToRoleMap_WhenPermissionsApiReturnsEmpty()
    {
        // Models a transient outage — PermissionsApi returns [] so we defer to role defaults.
        var checker = BuildChecker(
            new FakePermissionsApi(),
            baseUrl: "https://permissions.internal/",
            roles: [CodeFlowApiDefaults.Roles.Operator]);

        var granted = checker.HasPermission(
            new FakeCurrentUser("op-1", [CodeFlowApiDefaults.Roles.Operator]),
            CodeFlowApiDefaults.Permissions.OpsRead);

        granted.Should().BeTrue();
    }

    private static CompanyPermissionChecker BuildChecker(
        FakePermissionsApi api,
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
        return new CompanyPermissionChecker(api, fallback, cache, options);
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
