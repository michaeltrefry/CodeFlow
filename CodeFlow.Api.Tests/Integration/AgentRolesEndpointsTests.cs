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

    private static async Task<AgentRoleDto> CreateRole(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/agent-roles", new
        {
            key = $"role-{Guid.NewGuid():N}",
            displayName = "R",
            description = (string?)null,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<AgentRoleDto>())!;
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
        bool IsArchived);

    private sealed record GrantDto(string Category, string ToolIdentifier);
}
