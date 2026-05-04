using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class AgentRolesEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public AgentRolesEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Post_then_get_returns_created_role()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/agent-roles", new
        {
            key = $"reader-{Guid.NewGuid():N}",
            displayName = "Reader",
            description = "Read-only role",
        });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await create.Content.ReadFromJsonAsync<AgentRoleDto>())!;
        created.DisplayName.Should().Be("Reader");

        var fetched = await client.GetFromJsonAsync<AgentRoleDto>($"/api/agent-roles/{created.Id}");
        fetched!.Key.Should().Be(created.Key);
    }

    [Fact]
    public async Task Post_then_get_returns_normalized_tags()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/agent-roles", new
        {
            key = $"tagged-reader-{Guid.NewGuid():N}",
            displayName = "Tagged reader",
            description = (string?)null,
            tags = new[] { " Ops ", "ops", "Research" },
        });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await create.Content.ReadFromJsonAsync<AgentRoleDto>())!;
        created.Tags.Should().Equal("Ops", "Research");

        var fetched = await client.GetFromJsonAsync<AgentRoleDto>($"/api/agent-roles/{created.Id}");
        fetched!.Tags.Should().Equal("Ops", "Research");
    }

    [Fact]
    public async Task List_with_tag_filters_returns_roles_matching_all_requested_tags()
    {
        using var client = factory.CreateClient();

        var sharedTag = $"shared-{Guid.NewGuid():N}";
        var narrowTag = $"narrow-{Guid.NewGuid():N}";

        var matching = await CreateRole(client, tags: [sharedTag, narrowTag]);
        await CreateRole(client, tags: [sharedTag]);
        await CreateRole(client, tags: [$"other-{Guid.NewGuid():N}"]);

        var roles = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>(
            $"/api/agent-roles?tags={sharedTag},{narrowTag}"))!;

        roles.Select(role => role.Id).Should().ContainSingle().Which.Should().Be(matching.Id);
    }

    [Fact]
    public async Task Put_with_tags_updates_role_tags()
    {
        using var client = factory.CreateClient();

        var role = await CreateRole(client, tags: ["old-tag"]);

        var update = await client.PutAsJsonAsync($"/api/agent-roles/{role.Id}", new
        {
            displayName = "Updated role",
            description = "Updated",
            tags = new[] { "new-tag", "NEW-TAG", "ops" },
        });

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await update.Content.ReadFromJsonAsync<AgentRoleDto>())!;
        updated.Tags.Should().Equal("new-tag", "ops");

        var fetched = await client.GetFromJsonAsync<AgentRoleDto>($"/api/agent-roles/{role.Id}");
        fetched!.Tags.Should().Equal("new-tag", "ops");
    }

    [Fact]
    public async Task Retire_HidesFromListAndBlocksNewAssignments()
    {
        using var client = factory.CreateClient();

        var role = await CreateRole(client);

        var retire = await client.PostAsync($"/api/agent-roles/{role.Id}/retire", content: null);
        retire.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>("/api/agent-roles"))!;
        list.Select(r => r.Id).Should().NotContain(role.Id);

        var includeRetired = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>(
            "/api/agent-roles?includeRetired=true"))!;
        includeRetired.Single(r => r.Id == role.Id).IsRetired.Should().BeTrue();

        var assign = await client.PutAsJsonAsync($"/api/agents/role-retire-{Guid.NewGuid():N}/roles", new
        {
            roleIds = new[] { role.Id },
        });
        assign.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkRetire_HidesSelectedRoles()
    {
        using var client = factory.CreateClient();

        var roleA = await CreateRole(client);
        var roleB = await CreateRole(client);

        var retire = await client.PostAsJsonAsync("/api/agent-roles/retire", new
        {
            ids = new[] { roleA.Id, roleB.Id, 999999999L },
        });
        retire.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await retire.Content.ReadFromJsonAsync<BulkRoleRetireDto>())!;
        result.RetiredIds.Should().BeEquivalentTo(new[] { roleA.Id, roleB.Id });
        result.MissingIds.Should().Contain(999999999L);

        var list = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>("/api/agent-roles"))!;
        list.Select(r => r.Id).Should().NotContain(new[] { roleA.Id, roleB.Id });
    }

    [Fact]
    public async Task Put_tools_with_host_and_mcp_grants_succeeds_when_server_exists()
    {
        using var client = factory.CreateClient();

        var mcpKey = $"svc-{Guid.NewGuid():N}";
        var mcpCreate = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            key = mcpKey,
            displayName = "Svc",
            transport = "StreamableHttp",
            endpointUrl = "https://svc.local/mcp",
            bearerToken = (string?)null,
        });
        mcpCreate.StatusCode.Should().Be(HttpStatusCode.Created);

        var roleCreate = await client.PostAsJsonAsync("/api/agent-roles", new
        {
            key = $"role-{Guid.NewGuid():N}",
            displayName = "R",
            description = (string?)null,
        });
        var role = (await roleCreate.Content.ReadFromJsonAsync<AgentRoleDto>())!;

        var update = await client.PutAsJsonAsync($"/api/agent-roles/{role.Id}/tools", new object[]
        {
            new { category = "Host", toolIdentifier = "echo" },
            new { category = "Mcp", toolIdentifier = $"mcp:{mcpKey}:read" },
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var persisted = (await update.Content.ReadFromJsonAsync<IReadOnlyList<GrantDto>>())!;
        persisted.Should().HaveCount(2);
    }

    [Fact]
    public async Task Put_tools_accepts_new_workspace_host_tools()
    {
        using var client = factory.CreateClient();

        var role = await CreateRole(client);

        var update = await client.PutAsJsonAsync($"/api/agent-roles/{role.Id}/tools", new object[]
        {
            new { category = "Host", toolIdentifier = "read_file" },
            new { category = "Host", toolIdentifier = "apply_patch" },
            new { category = "Host", toolIdentifier = "run_command" },
        });

        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var persisted = (await update.Content.ReadFromJsonAsync<IReadOnlyList<GrantDto>>())!;
        persisted.Select(g => g.ToolIdentifier).Should().BeEquivalentTo(new[]
        {
            "read_file",
            "apply_patch",
            "run_command",
        });
    }

    [Fact]
    public async Task Put_tools_rejects_unknown_host_tool()
    {
        using var client = factory.CreateClient();

        var role = await CreateRole(client);
        var response = await client.PutAsJsonAsync($"/api/agent-roles/{role.Id}/tools", new object[]
        {
            new { category = "Host", toolIdentifier = "not-a-real-host-tool" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_tools_rejects_mcp_identifier_referencing_unknown_server()
    {
        using var client = factory.CreateClient();

        var role = await CreateRole(client);
        var response = await client.PutAsJsonAsync($"/api/agent-roles/{role.Id}/tools", new object[]
        {
            new { category = "Mcp", toolIdentifier = "mcp:ghost-server:tool" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_agent_roles_replaces_assignments_and_get_reads_them_back()
    {
        using var client = factory.CreateClient();

        var agentKey = $"agent-{Guid.NewGuid():N}";
        var role1 = await CreateRole(client);
        var role2 = await CreateRole(client);

        var replace = await client.PutAsJsonAsync($"/api/agents/{agentKey}/roles", new
        {
            roleIds = new[] { role1.Id, role2.Id },
        });
        replace.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>($"/api/agents/{agentKey}/roles"))!;
        fetched.Select(r => r.Id).Should().BeEquivalentTo(new[] { role1.Id, role2.Id });

        var clear = await client.PutAsJsonAsync($"/api/agents/{agentKey}/roles", new
        {
            roleIds = Array.Empty<long>(),
        });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>($"/api/agents/{agentKey}/roles"))!;
        after.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_agent_roles_with_unknown_role_id_returns_validation_problem()
    {
        using var client = factory.CreateClient();

        var agentKey = $"agent-{Guid.NewGuid():N}";
        var valid = await CreateRole(client);

        var response = await client.PutAsJsonAsync($"/api/agents/{agentKey}/roles", new
        {
            roleIds = new[] { valid.Id, 999999L },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<AgentRoleDto> CreateRole(HttpClient client, IReadOnlyList<string>? tags = null)
    {
        var response = await client.PostAsJsonAsync("/api/agent-roles", new
        {
            key = $"role-{Guid.NewGuid():N}",
            displayName = "R",
            description = (string?)null,
            tags,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<AgentRoleDto>())!;
    }

    [Fact]
    public async Task SystemManagedRole_PutTools_Succeeds()
    {
        // Seeded roles ship as starting points; operators can edit grants without forking.
        using var client = factory.CreateClient();

        var roles = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>("/api/agent-roles"))!;
        var systemRole = roles.SingleOrDefault(r => r.Key == "code-worker" && r.IsSystemManaged);
        systemRole.Should().NotBeNull("the seeder runs at startup and must populate code-worker");

        var response = await client.PutAsJsonAsync(
            $"/api/agent-roles/{systemRole!.Id}/tools",
            new object[] { new { category = "Host", toolIdentifier = "echo" } });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SystemManagedRole_Put_Succeeds()
    {
        using var client = factory.CreateClient();

        var roles = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>("/api/agent-roles"))!;
        var systemRole = roles.SingleOrDefault(r => r.Key == "kanban-worker" && r.IsSystemManaged);
        systemRole.Should().NotBeNull();

        var response = await client.PutAsJsonAsync(
            $"/api/agent-roles/{systemRole!.Id}",
            new { displayName = "Renamed", description = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SystemManagedRole_Delete_Succeeds()
    {
        using var client = factory.CreateClient();

        var roles = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>("/api/agent-roles"))!;
        var systemRole = roles.SingleOrDefault(r => r.Key == "read-only-shell" && r.IsSystemManaged);
        systemRole.Should().NotBeNull();

        var response = await client.DeleteAsync($"/api/agent-roles/{systemRole!.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private sealed record AgentRoleDto(
        long Id,
        string Key,
        string DisplayName,
        string? Description,
        DateTime CreatedAtUtc,
        string? CreatedBy,
        DateTime UpdatedAtUtc,
        string? UpdatedBy,
        bool IsArchived,
        bool IsRetired,
        bool IsSystemManaged,
        IReadOnlyList<string> Tags);

    private sealed record GrantDto(string Category, string ToolIdentifier);
    private sealed record BulkRoleRetireDto(IReadOnlyList<long> RetiredIds, IReadOnlyList<long> MissingIds);
}
