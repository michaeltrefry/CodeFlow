using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class WorkflowRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_workflow_tests")
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
    public async Task GetAsync_AndFindNextAsync_ShouldSupportSpecificFallbackAndOrderedWorkflowEdges()
    {
        var workflowKey = $"article-flow-{Guid.NewGuid():N}";

        await using var seedContext = CreateDbContext();
        seedContext.Workflows.Add(new WorkflowEntity
        {
            Key = workflowKey,
            Version = 2,
            Name = "Article flow",
            StartAgentKey = "writer",
            EscalationAgentKey = "editor-in-chief",
            MaxRoundsPerRound = 3,
            CreatedAtUtc = DateTime.UtcNow,
            Edges =
            [
                new WorkflowEdgeEntity
                {
                    FromAgentKey = "reviewer",
                    Decision = AgentDecisionKind.Rejected,
                    DiscriminatorJson = """{"stage":"legal"}""",
                    ToAgentKey = "legal-review",
                    RotatesRound = false,
                    SortOrder = 0
                },
                new WorkflowEdgeEntity
                {
                    FromAgentKey = "reviewer",
                    Decision = AgentDecisionKind.Rejected,
                    DiscriminatorJson = null,
                    ToAgentKey = "writer",
                    RotatesRound = true,
                    SortOrder = 1
                },
                new WorkflowEdgeEntity
                {
                    FromAgentKey = "publisher",
                    Decision = AgentDecisionKind.Completed,
                    DiscriminatorJson = null,
                    ToAgentKey = "archive-secondary",
                    RotatesRound = false,
                    SortOrder = 1
                },
                new WorkflowEdgeEntity
                {
                    FromAgentKey = "publisher",
                    Decision = AgentDecisionKind.Completed,
                    DiscriminatorJson = null,
                    ToAgentKey = "archive-primary",
                    RotatesRound = false,
                    SortOrder = 0
                }
            ]
        });
        await seedContext.SaveChangesAsync();

        await using var readContext = CreateDbContext();
        var repository = new WorkflowRepository(readContext);
        var workflow = await repository.GetAsync(workflowKey, 2);
        var legalDiscriminator = JsonDocument.Parse("""{"stage":"legal"}""").RootElement.Clone();
        var contentDiscriminator = JsonDocument.Parse("""{"stage":"content"}""").RootElement.Clone();

        workflow.Name.Should().Be("Article flow");
        workflow.StartAgentKey.Should().Be("writer");
        workflow.EscalationAgentKey.Should().Be("editor-in-chief");
        workflow.Edges.Should().HaveCount(4);

        var specificEdge = await repository.FindNextAsync(
            workflowKey,
            2,
            "reviewer",
            new RejectedDecision(["Needs legal signoff"]),
            legalDiscriminator);

        var fallbackEdge = await repository.FindNextAsync(
            workflowKey,
            2,
            "reviewer",
            new RejectedDecision(["Needs substantive edits"]),
            contentDiscriminator);

        var orderedEdge = await repository.FindNextAsync(
            workflowKey,
            2,
            "publisher",
            new CompletedDecision());

        specificEdge.Should().NotBeNull();
        specificEdge!.ToAgentKey.Should().Be("legal-review");
        specificEdge.RotatesRound.Should().BeFalse();

        fallbackEdge.Should().NotBeNull();
        fallbackEdge!.ToAgentKey.Should().Be("writer");
        fallbackEdge.RotatesRound.Should().BeTrue();

        orderedEdge.Should().NotBeNull();
        orderedEdge!.ToAgentKey.Should().Be("archive-primary");
        orderedEdge.SortOrder.Should().Be(0);
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
