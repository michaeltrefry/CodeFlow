using CodeFlow.Runtime;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class RoleResolutionServiceTests : IAsyncLifetime
{
    private static readonly byte[] MasterKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private string? connectionString;

    public async Task InitializeAsync()
    {
        await mariaDbContainer.StartAsync();
        connectionString = mariaDbContainer.GetConnectionString();
        await using var ctx = CreateDbContext();
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task ResolveAsync_returns_Empty_when_no_roles_assigned()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        await using var ctx = CreateDbContext();

        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var result = await resolver.ResolveAsync(agentKey);

        result.Should().BeSameAs(ResolvedAgentTools.Empty);
    }

    [Fact]
    public async Task ResolveAsync_returns_host_tools_only_when_grants_are_host_only()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        var roleKey = $"role-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(ctx);
        var roleId = await roleRepo.CreateAsync(new AgentRoleCreate(roleKey, "R", null, null));
        await roleRepo.ReplaceGrantsAsync(roleId, new[]
        {
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
        });
        await roleRepo.ReplaceAssignmentsAsync(agentKey, new[] { roleId });

        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var result = await resolver.ResolveAsync(agentKey);

        result.EnableHostTools.Should().BeTrue();
        result.McpTools.Should().BeEmpty();
        result.AllowedToolNames.Should().BeEquivalentTo(new[] { "echo", "now" });
    }

    [Fact]
    public async Task ResolveAsync_materializes_mcp_tools_from_registered_server()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        var roleKey = $"role-{Guid.NewGuid():N}";
        var serverKey = $"svc-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var mcpRepo = new McpServerRepository(ctx, protector);
        var roleRepo = new AgentRoleRepository(ctx);

        var serverId = await mcpRepo.CreateAsync(new McpServerCreate(
            Key: serverKey,
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerTokenPlaintext: null,
            CreatedBy: null));

        await mcpRepo.ReplaceToolsAsync(serverId, new[]
        {
            new McpServerToolWrite("read", "read artifact", """{"type":"object"}""", IsMutating: false),
            new McpServerToolWrite("write", "write artifact", null, IsMutating: true),
        });

        var roleId = await roleRepo.CreateAsync(new AgentRoleCreate(roleKey, "R", null, null));
        await roleRepo.ReplaceGrantsAsync(roleId, new[]
        {
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{serverKey}:read"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{serverKey}:write"),
        });
        await roleRepo.ReplaceAssignmentsAsync(agentKey, new[] { roleId });

        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var result = await resolver.ResolveAsync(agentKey);

        result.EnableHostTools.Should().BeFalse();
        result.McpTools.Should().HaveCount(2);
        result.McpTools.Select(t => t.FullName).Should().BeEquivalentTo(new[]
        {
            $"mcp:{serverKey}:read",
            $"mcp:{serverKey}:write",
        });
        result.McpTools.Single(t => t.ToolName == "write").IsMutating.Should().BeTrue();
        result.AllowedToolNames.Should().BeEquivalentTo(new[]
        {
            $"mcp:{serverKey}:read",
            $"mcp:{serverKey}:write",
        });
    }

    [Fact]
    public async Task ResolveAsync_unions_grants_across_multiple_roles()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(ctx);

        var roleA = await roleRepo.CreateAsync(new AgentRoleCreate($"a-{Guid.NewGuid():N}", "A", null, null));
        await roleRepo.ReplaceGrantsAsync(roleA, new[]
        {
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
        });

        var roleB = await roleRepo.CreateAsync(new AgentRoleCreate($"b-{Guid.NewGuid():N}", "B", null, null));
        await roleRepo.ReplaceGrantsAsync(roleB, new[]
        {
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
        });

        await roleRepo.ReplaceAssignmentsAsync(agentKey, new[] { roleA, roleB });

        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var result = await resolver.ResolveAsync(agentKey);

        result.AllowedToolNames.Should().BeEquivalentTo(new[] { "echo", "now" });
    }

    [Fact]
    public async Task ResolveAsync_filters_out_ghost_mcp_tools_and_unknown_host_tools()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        var roleKey = $"role-{Guid.NewGuid():N}";
        var serverKey = $"svc-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var mcpRepo = new McpServerRepository(ctx, protector);
        var roleRepo = new AgentRoleRepository(ctx);

        var serverId = await mcpRepo.CreateAsync(new McpServerCreate(
            Key: serverKey,
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerTokenPlaintext: null,
            CreatedBy: null));

        await mcpRepo.ReplaceToolsAsync(serverId, new[]
        {
            new McpServerToolWrite("read", "read", null, IsMutating: false),
        });

        var roleId = await roleRepo.CreateAsync(new AgentRoleCreate(roleKey, "R", null, null));
        await roleRepo.ReplaceGrantsAsync(roleId, new[]
        {
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "obsolete-host-tool"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{serverKey}:read"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{serverKey}:removed"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, "mcp:ghost-server:anything"),
        });
        await roleRepo.ReplaceAssignmentsAsync(agentKey, new[] { roleId });

        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var result = await resolver.ResolveAsync(agentKey);

        result.EnableHostTools.Should().BeTrue();
        result.AllowedToolNames.Should().BeEquivalentTo(new[]
        {
            "echo",
            $"mcp:{serverKey}:read",
        });
        result.McpTools.Should().HaveCount(1);
        result.McpTools.Single().ToolName.Should().Be("read");
    }

    [Fact]
    public async Task ResolveAsync_ignores_grants_from_archived_roles()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(ctx);

        var activeRole = await roleRepo.CreateAsync(new AgentRoleCreate($"active-{Guid.NewGuid():N}", "A", null, null));
        await roleRepo.ReplaceGrantsAsync(activeRole, new[]
        {
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
        });

        var archivedRole = await roleRepo.CreateAsync(new AgentRoleCreate($"archived-{Guid.NewGuid():N}", "Z", null, null));
        await roleRepo.ReplaceGrantsAsync(archivedRole, new[]
        {
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
        });
        await roleRepo.ReplaceAssignmentsAsync(agentKey, new[] { activeRole, archivedRole });
        await roleRepo.ArchiveAsync(archivedRole);

        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var result = await resolver.ResolveAsync(agentKey);

        result.AllowedToolNames.Should().BeEquivalentTo(new[] { "echo" });
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
