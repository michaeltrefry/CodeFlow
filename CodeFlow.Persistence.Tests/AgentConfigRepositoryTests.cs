using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class AgentConfigRepositoryTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

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

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldCreateAndFetchVersionedAgentConfigs()
    {
        var agentKey = $"writer-{Guid.NewGuid():N}";
        var v1Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5.4",
            SystemPrompt: "Write clearly.",
            PromptTemplate: "Draft: {{input}}",
            EnableHostTools: true), SerializerOptions);
        var v2Json = JsonSerializer.Serialize(new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-sonnet-4.5",
            SystemPrompt: "Revise carefully.",
            PromptTemplate: "Review: {{input}}",
            MaxTokens: 2048,
            EnableHostTools: false), SerializerOptions);

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
        version1.Configuration.EnableHostTools.Should().BeTrue();

        version2.Version.Should().Be(2);
        version2.ConfigJson.Should().Be(v2Json);
        version2.Configuration.Provider.Should().Be("anthropic");
        version2.Configuration.Model.Should().Be("claude-sonnet-4.5");
        version2.Configuration.MaxTokens.Should().Be(2048);
        version2.Configuration.EnableHostTools.Should().BeFalse();

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

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
