using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
namespace CodeFlow.Persistence.Tests;

[Collection(PersistenceMariaDbCollection.Name)]
public sealed class AgentConfigRepositoryTests : IAsyncLifetime
{
    private readonly SharedMariaDbFixture mariaDb;
    private const string DatabaseName = "test_agentconfigrepositorytests";
    private string? connectionString;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);


    public AgentConfigRepositoryTests(SharedMariaDbFixture mariaDb)

    {

        this.mariaDb = mariaDb;

    }


    public async Task InitializeAsync()
    {
        connectionString = await mariaDb.EnsureDatabaseAsync(DatabaseName);

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDb.DropDatabaseAsync(DatabaseName);
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldCreateAndFetchVersionedAgentConfigs()
    {
        var agentKey = $"writer-{Guid.NewGuid():N}";
        var v1Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "Write clearly.",
            PromptTemplate: "Draft: {{input}}"), SerializerOptions);
        var v2Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-sonnet-4.5",
            SystemPrompt: "Revise carefully.",
            PromptTemplate: "Review: {{input}}",
            MaxTokens: 2048), SerializerOptions);

        await using var writeContext = CreateDbContext();
        var repository = new AgentConfigRepository(writeContext);

        var v1 = await repository.CreateNewVersionAsync(agentKey, v1Json, "codex");
        var v2 = await repository.CreateNewVersionAsync(agentKey, v2Json, "codex");

        v1.Should().Be(1);
        v2.Should().Be(2);

        await using var readContext = CreateDbContext();
        var readRepository = new AgentConfigRepository(readContext);

        var version1 = await readRepository.GetAsync(agentKey, 1);
        var version2 = await readRepository.GetAsync(agentKey, 2);

        version1.Key.Should().Be(agentKey);
        version1.Version.Should().Be(1);
        version1.CreatedBy.Should().Be("codex");
        version1.ConfigJson.Should().Be(v1Json);
        version1.Configuration.Provider.Should().Be("openai");
        version1.Configuration.Model.Should().Be("gpt-5.4");
        version1.TagsOrEmpty.Should().BeEmpty();

        version2.Version.Should().Be(2);
        version2.ConfigJson.Should().Be(v2Json);
        version2.Configuration.Provider.Should().Be("anthropic");
        version2.Configuration.Model.Should().Be("claude-sonnet-4.5");
        version2.Configuration.MaxTokens.Should().Be(2048);
        version2.TagsOrEmpty.Should().BeEmpty();

        var persistedStates = await readContext.Agents
            .AsNoTracking()
            .Where(agent => agent.Key == agentKey)
            .OrderBy(agent => agent.Version)
            .Select(agent => new { agent.Version, agent.IsActive })
            .ToListAsync();

        persistedStates.Should().BeEquivalentTo(
            [
                new { Version = 1, IsActive = false },
                new { Version = 2, IsActive = true }
            ]);
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldPreserveLatestVersionTags()
    {
        var agentKey = $"writer-{Guid.NewGuid():N}";
        var v1Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "Write clearly.",
            PromptTemplate: "Draft: {{input}}"), SerializerOptions);
        var v2Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "Revise carefully.",
            PromptTemplate: "Draft: {{input}}"), SerializerOptions);

        await using var writeContext = CreateDbContext();
        writeContext.Agents.Add(new AgentConfigEntity
        {
            Key = agentKey,
            Version = 1,
            ConfigJson = v1Json,
            TagsJson = WorkflowJson.SerializeTags(["writing", "review"]),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "tester",
            IsActive = true
        });
        await writeContext.SaveChangesAsync();

        var repository = new AgentConfigRepository(writeContext);
        var version = await repository.CreateNewVersionAsync(agentKey, v2Json, "tester");

        version.Should().Be(2);

        await using var readContext = CreateDbContext();
        var readRepository = new AgentConfigRepository(readContext);
        var fetched = await readRepository.GetAsync(agentKey, 2);

        fetched.TagsOrEmpty.Should().Equal("writing", "review");
    }

    [Fact]
    public async Task GetAsync_ShouldRoundTripForkLineageColumns()
    {
        var sourceKey = $"source-{Guid.NewGuid():N}";
        var forkKey = $"__fork_{Guid.NewGuid():N}";
        var workflowKey = $"wf-{Guid.NewGuid():N}";
        var configJson = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "test",
            PromptTemplate: "{{input}}"), SerializerOptions);

        await using var writeContext = CreateDbContext();
        writeContext.Agents.Add(new AgentConfigEntity
        {
            Key = forkKey,
            Version = 1,
            ConfigJson = configJson,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "tester",
            TagsJson = WorkflowJson.SerializeTags(["forked"]),
            IsActive = true,
            OwningWorkflowKey = workflowKey,
            ForkedFromKey = sourceKey,
            ForkedFromVersion = 3
        });
        await writeContext.SaveChangesAsync();

        await using var readContext = CreateDbContext();
        var repository = new AgentConfigRepository(readContext);
        var fetched = await repository.GetAsync(forkKey, 1);

        fetched.OwningWorkflowKey.Should().Be(workflowKey);
        fetched.ForkedFromKey.Should().Be(sourceKey);
        fetched.ForkedFromVersion.Should().Be(3);
        fetched.TagsOrEmpty.Should().Equal("forked");
        fetched.IsWorkflowScoped.Should().BeTrue();
        fetched.IsFork.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNullLineageForLibraryAgents()
    {
        var key = $"library-{Guid.NewGuid():N}";
        var configJson = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "test",
            PromptTemplate: "{{input}}"), SerializerOptions);

        await using var writeContext = CreateDbContext();
        var repository = new AgentConfigRepository(writeContext);
        await repository.CreateNewVersionAsync(key, configJson, "tester");

        await using var readContext = CreateDbContext();
        var readRepository = new AgentConfigRepository(readContext);
        var fetched = await readRepository.GetAsync(key, 1);

        fetched.OwningWorkflowKey.Should().BeNull();
        fetched.ForkedFromKey.Should().BeNull();
        fetched.ForkedFromVersion.Should().BeNull();
        fetched.IsWorkflowScoped.Should().BeFalse();
        fetched.IsFork.Should().BeFalse();
    }

    [Fact]
    public async Task CreateForkAsync_ShouldCopySourceTags()
    {
        var sourceKey = $"source-{Guid.NewGuid():N}";
        var workflowKey = $"wf-{Guid.NewGuid():N}";
        var sourceJson = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "source",
            PromptTemplate: "{{input}}"), SerializerOptions);
        var forkJson = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "fork",
            PromptTemplate: "{{input}}"), SerializerOptions);

        await using var writeContext = CreateDbContext();
        writeContext.Agents.Add(new AgentConfigEntity
        {
            Key = sourceKey,
            Version = 1,
            ConfigJson = sourceJson,
            TagsJson = WorkflowJson.SerializeTags(["source-tag", "shared"]),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "tester",
            IsActive = true
        });
        await writeContext.SaveChangesAsync();

        var repository = new AgentConfigRepository(writeContext);

        var fork = await repository.CreateForkAsync(sourceKey, 1, workflowKey, forkJson, "tester");

        fork.TagsOrEmpty.Should().Equal("source-tag", "shared");
        fork.IsFork.Should().BeTrue();
    }

    [Fact]
    public async Task CreatePublishedVersionAsync_ShouldPreserveTargetTags()
    {
        var targetKey = $"target-{Guid.NewGuid():N}";
        var configJson = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "test",
            PromptTemplate: "{{input}}"), SerializerOptions);

        await using var writeContext = CreateDbContext();
        writeContext.Agents.Add(new AgentConfigEntity
        {
            Key = targetKey,
            Version = 1,
            ConfigJson = configJson,
            TagsJson = WorkflowJson.SerializeTags(["library", "stable"]),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "tester",
            IsActive = true
        });
        await writeContext.SaveChangesAsync();

        var repository = new AgentConfigRepository(writeContext);
        var version = await repository.CreatePublishedVersionAsync(
            targetKey,
            configJson,
            forkedFromKey: "source",
            forkedFromVersion: 1,
            createdBy: "tester");

        version.Should().Be(2);

        await using var readContext = CreateDbContext();
        var readRepository = new AgentConfigRepository(readContext);
        var fetched = await readRepository.GetAsync(targetKey, 2);

        fetched.TagsOrEmpty.Should().Equal("library", "stable");
        fetched.IsWorkflowScoped.Should().BeFalse();
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldPreserveForkLineage()
    {
        var sourceKey = $"source-{Guid.NewGuid():N}";
        var forkKey = $"__fork_{Guid.NewGuid():N}";
        var workflowKey = $"wf-{Guid.NewGuid():N}";
        var v1Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "first",
            PromptTemplate: "{{input}}"), SerializerOptions);
        var v2Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "second",
            PromptTemplate: "{{input}}"), SerializerOptions);

        await using var writeContext = CreateDbContext();
        writeContext.Agents.Add(new AgentConfigEntity
        {
            Key = forkKey,
            Version = 1,
            ConfigJson = v1Json,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "tester",
            TagsJson = WorkflowJson.SerializeTags(["workflow-local"]),
            IsActive = true,
            OwningWorkflowKey = workflowKey,
            ForkedFromKey = sourceKey,
            ForkedFromVersion = 7
        });
        await writeContext.SaveChangesAsync();

        var repository = new AgentConfigRepository(writeContext);
        var version = await repository.CreateNewVersionAsync(forkKey, v2Json, "tester");

        version.Should().Be(2);

        await using var readContext = CreateDbContext();
        var readRepository = new AgentConfigRepository(readContext);
        var fetched = await readRepository.GetAsync(forkKey, 2);

        fetched.OwningWorkflowKey.Should().Be(workflowKey);
        fetched.ForkedFromKey.Should().Be(sourceKey);
        fetched.ForkedFromVersion.Should().Be(7);
        fetched.TagsOrEmpty.Should().Equal("workflow-local");
        fetched.IsWorkflowScoped.Should().BeTrue();
        fetched.IsFork.Should().BeTrue();
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldCarryForwardRoleAssignmentsFromPriorVersion()
    {
        // Regression: agent role assignments are keyed (agent_key, agent_version, role_id) since
        // sc-825. Before this fix, a body edit landed at v_N+1 with no assignment rows, so the
        // agent appeared roleless and the user had to re-assign through the role-only endpoint
        // — which itself bumps the version, leaving v_N+1 stranded as an empty intermediate.
        var agentKey = $"writer-{Guid.NewGuid():N}";
        var v1Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "Original.",
            PromptTemplate: "{{input}}"), SerializerOptions);
        var v2Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "Edited.",
            PromptTemplate: "{{input}}"), SerializerOptions);

        await using var writeContext = CreateDbContext();
        var role = new AgentRoleEntity
        {
            Key = $"role-{Guid.NewGuid():N}",
            DisplayName = "Carry-Forward Test Role",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            TagsJson = "[]",
        };
        writeContext.AgentRoles.Add(role);
        writeContext.Agents.Add(new AgentConfigEntity
        {
            Key = agentKey,
            Version = 1,
            ConfigJson = v1Json,
            TagsJson = "[]",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "tester",
            IsActive = true,
        });
        await writeContext.SaveChangesAsync();

        writeContext.AgentRoleAssignments.Add(new AgentRoleAssignmentEntity
        {
            AgentKey = agentKey,
            AgentVersion = 1,
            RoleId = role.Id,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await writeContext.SaveChangesAsync();

        var repository = new AgentConfigRepository(writeContext);
        var version = await repository.CreateNewVersionAsync(agentKey, v2Json, "tester");

        version.Should().Be(2);

        await using var readContext = CreateDbContext();
        var assignments = await readContext.AgentRoleAssignments
            .AsNoTracking()
            .Where(a => a.AgentKey == agentKey)
            .OrderBy(a => a.AgentVersion)
            .ToListAsync();

        assignments.Should().HaveCount(2, "the prior version's row plus a copy at the new version");
        assignments[0].AgentVersion.Should().Be(1);
        assignments[1].AgentVersion.Should().Be(2);
        assignments.Should().OnlyContain(a => a.RoleId == role.Id);
    }

    [Fact]
    public async Task CreatePublishedVersionAsync_ShouldCarryForwardRoleAssignmentsFromPriorTargetVersion()
    {
        // Same regression as the edit-save path, applied to the publish-back-from-fork path.
        // The fork's source key has its own assignment story; the *target* of the publish keeps
        // its own role assignments and they should ride along to the bumped version.
        var targetKey = $"target-{Guid.NewGuid():N}";
        var configJson = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "test",
            PromptTemplate: "{{input}}"), SerializerOptions);

        await using var writeContext = CreateDbContext();
        var role = new AgentRoleEntity
        {
            Key = $"role-{Guid.NewGuid():N}",
            DisplayName = "Publish Carry-Forward Role",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            TagsJson = "[]",
        };
        writeContext.AgentRoles.Add(role);
        writeContext.Agents.Add(new AgentConfigEntity
        {
            Key = targetKey,
            Version = 1,
            ConfigJson = configJson,
            TagsJson = "[]",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "tester",
            IsActive = true,
        });
        await writeContext.SaveChangesAsync();

        writeContext.AgentRoleAssignments.Add(new AgentRoleAssignmentEntity
        {
            AgentKey = targetKey,
            AgentVersion = 1,
            RoleId = role.Id,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await writeContext.SaveChangesAsync();

        var repository = new AgentConfigRepository(writeContext);
        var version = await repository.CreatePublishedVersionAsync(
            targetKey,
            configJson,
            forkedFromKey: "source",
            forkedFromVersion: 1,
            createdBy: "tester");

        version.Should().Be(2);

        await using var readContext = CreateDbContext();
        var assignments = await readContext.AgentRoleAssignments
            .AsNoTracking()
            .Where(a => a.AgentKey == targetKey)
            .OrderBy(a => a.AgentVersion)
            .ToListAsync();

        assignments.Should().HaveCount(2);
        assignments[0].AgentVersion.Should().Be(1);
        assignments[1].AgentVersion.Should().Be(2);
        assignments.Should().OnlyContain(a => a.RoleId == role.Id);
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
