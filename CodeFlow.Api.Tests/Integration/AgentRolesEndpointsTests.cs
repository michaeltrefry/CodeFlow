using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
[Collection("CodeFlowApi")]
public sealed class AgentRolesEndpointsTests
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
    public async Task Put_agent_roles_bumps_to_new_version_and_returns_assigned_roles()
    {
        using var client = factory.CreateClient();

        var agentKey = $"agent-{Guid.NewGuid():N}";
        var role1 = await CreateRole(client);
        var role2 = await CreateRole(client);
        await CreateAgentAsync(client, agentKey);

        var replace = await client.PutAsJsonAsync($"/api/agents/{agentKey}/roles", new
        {
            roleIds = new[] { role1.Id, role2.Id },
        });
        replace.StatusCode.Should().Be(HttpStatusCode.OK);

        var assignmentsResponse = (await replace.Content.ReadFromJsonAsync<AssignmentsResponseDto>())!;
        assignmentsResponse.AgentKey.Should().Be(agentKey);
        assignmentsResponse.AgentVersion.Should().Be(2, "PUT bumps the agent from v1 to v2");
        assignmentsResponse.AssignedRoles.Select(r => r.Id).Should().BeEquivalentTo(new[] { role1.Id, role2.Id });

        // GET defaults to latest, so it sees the v2 row.
        var fetched = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>($"/api/agents/{agentKey}/roles"))!;
        fetched.Select(r => r.Id).Should().BeEquivalentTo(new[] { role1.Id, role2.Id });

        var clear = await client.PutAsJsonAsync($"/api/agents/{agentKey}/roles", new
        {
            roleIds = Array.Empty<long>(),
        });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var clearResponse = (await clear.Content.ReadFromJsonAsync<AssignmentsResponseDto>())!;
        clearResponse.AgentVersion.Should().Be(3, "second edit bumps to v3");
        clearResponse.AssignedRoles.Should().BeEmpty();

        var after = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>($"/api/agents/{agentKey}/roles"))!;
        after.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_agent_roles_with_unknown_role_id_returns_validation_problem()
    {
        using var client = factory.CreateClient();

        var agentKey = $"agent-{Guid.NewGuid():N}";
        await CreateAgentAsync(client, agentKey);
        var valid = await CreateRole(client);

        var response = await client.PutAsJsonAsync($"/api/agents/{agentKey}/roles", new
        {
            roleIds = new[] { valid.Id, 999999L },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_agent_roles_returns_404_when_agent_does_not_exist()
    {
        using var client = factory.CreateClient();

        // sc-828 / AR-4: bump-on-write requires an existing agent to clone the body from.
        var role = await CreateRole(client);

        var response = await client.PutAsJsonAsync(
            $"/api/agents/missing-{Guid.NewGuid():N}/roles",
            new { roleIds = new[] { role.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_agent_roles_409_on_drift_succeeds_with_acknowledgeDrift()
    {
        using var client = factory.CreateClient();

        var agentKey = $"agent-{Guid.NewGuid():N}";
        var role = await CreateRole(client);
        await CreateAgentAsync(client, agentKey);

        // Land a v2 first via a normal bump.
        var first = await client.PutAsJsonAsync($"/api/agents/{agentKey}/roles", new
        {
            roleIds = new[] { role.Id },
            expectedFromVersion = 1,
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Caller still thinks they're editing v1 — drift gate fires.
        var stale = await client.PutAsJsonAsync($"/api/agents/{agentKey}/roles", new
        {
            roleIds = Array.Empty<long>(),
            expectedFromVersion = 1,
        });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var drift = (await stale.Content.ReadFromJsonAsync<DriftResponseDto>())!;
        drift.AgentKey.Should().Be(agentKey);
        drift.ExpectedFromVersion.Should().Be(1);
        drift.ActualLatestVersion.Should().Be(2);

        // Retry with acknowledgeDrift and the bump lands at v3.
        var retry = await client.PutAsJsonAsync($"/api/agents/{agentKey}/roles", new
        {
            roleIds = Array.Empty<long>(),
            expectedFromVersion = 1,
            acknowledgeDrift = true,
        });
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var retryResponse = (await retry.Content.ReadFromJsonAsync<AssignmentsResponseDto>())!;
        retryResponse.AgentVersion.Should().Be(3);
    }

    [Fact]
    public async Task Post_agents_with_roleIds_creates_v1_with_assignment_atomically()
    {
        // sc-828 / AR-4: optional `roleIds` on POST /api/agents lands the v1 row with the
        // assignment slot filled in — no follow-up PUT (which would bump to v2). Same path
        // the AP-10 round-trip exercises end-to-end.
        using var client = factory.CreateClient();

        var role = await CreateRole(client);
        var agentKey = $"agent-{Guid.NewGuid():N}";

        var create = await client.PostAsJsonAsync("/api/agents", new
        {
            key = agentKey,
            config = new { provider = "openai", model = "gpt-test" },
            roleIds = new[] { role.Id },
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var roles = (await client.GetFromJsonAsync<IReadOnlyList<AgentRoleDto>>($"/api/agents/{agentKey}/roles"))!;
        roles.Select(r => r.Id).Should().Equal(role.Id);

        // No v2 was created — assignment lives at v1.
        var versions = (await client.GetFromJsonAsync<IReadOnlyList<AgentVersionSummaryDto>>($"/api/agents/{agentKey}/versions"))!;
        versions.Should().HaveCount(1);
        versions[0].Version.Should().Be(1);
    }

    private static async Task CreateAgentAsync(HttpClient client, string key)
    {
        var response = await client.PostAsJsonAsync("/api/agents", new
        {
            key,
            config = new { provider = "openai", model = "gpt-test" },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private sealed record AssignmentsResponseDto(string AgentKey, int AgentVersion, IReadOnlyList<AgentRoleDto> AssignedRoles);

    private sealed record DriftResponseDto(string AgentKey, int ExpectedFromVersion, int ActualLatestVersion, string Message);

    private sealed record AgentVersionSummaryDto(int Version);

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
        var systemRole = roles.SingleOrDefault(r => r.Key == "codeflow-assistant" && r.IsSystemManaged);
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
