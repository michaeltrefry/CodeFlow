using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class SkillsEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public SkillsEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Post_then_get_returns_created_skill()
    {
        using var client = factory.CreateClient();

        var name = $"skill-{Guid.NewGuid():N}";
        var create = await client.PostAsJsonAsync("/api/skills", new
        {
            name,
            body = "## Opening\nAsk one question.",
        });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await create.Content.ReadFromJsonAsync<SkillDto>())!;
        created.Name.Should().Be(name);
        created.Body.Should().Be("## Opening\nAsk one question.");

        var fetched = await client.GetFromJsonAsync<SkillDto>($"/api/skills/{created.Id}");
        fetched!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task Post_with_duplicate_name_returns_conflict()
    {
        using var client = factory.CreateClient();

        var name = $"skill-{Guid.NewGuid():N}";
        (await client.PostAsJsonAsync("/api/skills", new { name, body = "a" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var dup = await client.PostAsJsonAsync("/api/skills", new { name, body = "b" });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Put_updates_name_and_body()
    {
        using var client = factory.CreateClient();

        var created = await CreateSkill(client);
        var newName = $"renamed-{Guid.NewGuid():N}";

        var update = await client.PutAsJsonAsync($"/api/skills/{created.Id}", new
        {
            name = newName,
            body = "new body",
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await update.Content.ReadFromJsonAsync<SkillDto>())!;
        updated.Name.Should().Be(newName);
        updated.Body.Should().Be("new body");
    }

    [Fact]
    public async Task Delete_archives_skill_and_excludes_it_from_default_list()
    {
        using var client = factory.CreateClient();

        var created = await CreateSkill(client);

        var archive = await client.DeleteAsync($"/api/skills/{created.Id}");
        archive.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var listed = (await client.GetFromJsonAsync<IReadOnlyList<SkillDto>>("/api/skills"))!;
        listed.Should().NotContain(s => s.Id == created.Id);

        var listedWithArchived = (await client.GetFromJsonAsync<IReadOnlyList<SkillDto>>("/api/skills?includeArchived=true"))!;
        listedWithArchived.Should().Contain(s => s.Id == created.Id);
    }

    [Fact]
    public async Task Put_role_skills_replaces_grants_and_get_reads_them_back()
    {
        using var client = factory.CreateClient();

        var role = await CreateRole(client);
        var s1 = await CreateSkill(client);
        var s2 = await CreateSkill(client);

        var replace = await client.PutAsJsonAsync($"/api/agent-roles/{role.Id}/skills", new
        {
            skillIds = new[] { s1.Id, s2.Id },
        });
        replace.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = (await client.GetFromJsonAsync<SkillGrantsDto>($"/api/agent-roles/{role.Id}/skills"))!;
        fetched.SkillIds.Should().BeEquivalentTo(new[] { s1.Id, s2.Id });

        var clear = await client.PutAsJsonAsync($"/api/agent-roles/{role.Id}/skills", new
        {
            skillIds = Array.Empty<long>(),
        });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = (await client.GetFromJsonAsync<SkillGrantsDto>($"/api/agent-roles/{role.Id}/skills"))!;
        after.SkillIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_role_skills_rejects_unknown_skill_id()
    {
        using var client = factory.CreateClient();

        var role = await CreateRole(client);
        var response = await client.PutAsJsonAsync($"/api/agent-roles/{role.Id}/skills", new
        {
            skillIds = new[] { 999999L },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_role_skills_rejects_archived_skill_id()
    {
        using var client = factory.CreateClient();

        var role = await CreateRole(client);
        var skill = await CreateSkill(client);
        (await client.DeleteAsync($"/api/skills/{skill.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await client.PutAsJsonAsync($"/api/agent-roles/{role.Id}/skills", new
        {
            skillIds = new[] { skill.Id },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<SkillDto> CreateSkill(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/skills", new
        {
            name = $"skill-{Guid.NewGuid():N}",
            body = "body text",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<SkillDto>())!;
    }

    private static async Task<RoleDto> CreateRole(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/agent-roles", new
        {
            key = $"role-{Guid.NewGuid():N}",
            displayName = "R",
            description = (string?)null,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RoleDto>())!;
    }

    private sealed record SkillDto(
        long Id,
        string Name,
        string Body,
        DateTime CreatedAtUtc,
        string? CreatedBy,
        DateTime UpdatedAtUtc,
        string? UpdatedBy,
        bool IsArchived);

    private sealed record SkillGrantsDto(IReadOnlyList<long> SkillIds);

    private sealed record RoleDto(long Id, string Key, string DisplayName);
}
