using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

/// <summary>
/// End-to-end verification that a granted skill flows through the full chain:
/// DB -> AgentRoleRepository (grant) + RoleResolutionService (resolve)
/// -> ContextAssembler (render + inject into system message).
/// Uses the Socratic-interview skill body as the realistic case.
/// </summary>
public sealed class SkillEndToEndTests : IAsyncLifetime
{
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

    public async Task DisposeAsync() => await mariaDbContainer.DisposeAsync();

    [Fact]
    public async Task Granted_skill_appears_in_rendered_system_message_with_variables_substituted()
    {
        const string socraticBody = """
            ## Opening
            Ask exactly one Socratic question that will materially improve the PRD.

            ## Section: Current Understanding
            {{analysis.summary}}

            ## Section: Conversation So Far
            {{conversationSummary}}
            """;

        var agentKey = $"product-designer-{Guid.NewGuid():N}";
        var roleKey = $"interviewer-{Guid.NewGuid():N}";
        var skillName = $"socratic-interview-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(ctx);
        var skillRepo = new SkillRepository(ctx);

        var roleId = await roleRepo.CreateAsync(new AgentRoleCreate(roleKey, "Interviewer", null, null));
        var skillId = await skillRepo.CreateAsync(new SkillCreate(skillName, socraticBody, null));
        await roleRepo.ReplaceSkillGrantsAsync(roleId, new[] { skillId });
        await roleRepo.ReplaceAssignmentsAsync(agentKey, new[] { roleId });

        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var resolved = await resolver.ResolveAsync(agentKey);

        resolved.GrantedSkills.Should().ContainSingle();
        resolved.GrantedSkills[0].Name.Should().Be(skillName);

        var assembler = new ContextAssembler();
        var messages = assembler.Assemble(new ContextAssemblyRequest(
            SystemPrompt: "You are a product designer running a Socratic interview.",
            PromptTemplate: null,
            Input: null,
            Variables: new Dictionary<string, string?>
            {
                ["analysis.summary"] = "Users don't discover the export feature; ~18% abandon mid-flow.",
                ["conversationSummary"] = "Round 1 confirmed the export exists. Round 2 open.",
            },
            Skills: resolved.GrantedSkills));

        messages.Should().ContainSingle();
        var system = messages[0];
        system.Role.Should().Be(ChatMessageRole.System);

        system.Content.Should().StartWith("You are a product designer running a Socratic interview.");
        system.Content.Should().Contain("## Skills");
        system.Content.Should().Contain($"### {skillName}");
        system.Content.Should().Contain("Ask exactly one Socratic question");

        // Variables substituted into the skill body.
        system.Content.Should().Contain("Users don't discover the export feature");
        system.Content.Should().Contain("Round 1 confirmed the export exists");
        system.Content.Should().NotContain("{{analysis.summary}}");
        system.Content.Should().NotContain("{{conversationSummary}}");
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
