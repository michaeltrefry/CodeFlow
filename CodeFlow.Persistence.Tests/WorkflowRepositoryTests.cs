using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
    public async Task GetAsync_AndFindNextAsync_ShouldRoundTripNodeEdgeModel()
    {
        var workflowKey = $"article-flow-{Guid.NewGuid():N}";
        var startNode = Guid.NewGuid();
        var reviewerNode = Guid.NewGuid();
        var writerNode = Guid.NewGuid();
        var legalNode = Guid.NewGuid();
        var publisherNode = Guid.NewGuid();
        var archivePrimaryNode = Guid.NewGuid();
        var archiveSecondaryNode = Guid.NewGuid();
        var escalationNode = Guid.NewGuid();

        await using var seedContext = CreateDbContext();
        seedContext.Workflows.Add(new WorkflowEntity
        {
            Key = workflowKey,
            Version = 2,
            Name = "Article flow",
            MaxRoundsPerRound = 3,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                NodeEntity(startNode, WorkflowNodeKind.Start, "writer"),
                NodeEntity(reviewerNode, WorkflowNodeKind.Agent, "reviewer"),
                NodeEntity(writerNode, WorkflowNodeKind.Agent, "writer"),
                NodeEntity(legalNode, WorkflowNodeKind.Agent, "legal-review"),
                NodeEntity(publisherNode, WorkflowNodeKind.Agent, "publisher"),
                NodeEntity(archivePrimaryNode, WorkflowNodeKind.Agent, "archive-primary"),
                NodeEntity(archiveSecondaryNode, WorkflowNodeKind.Agent, "archive-secondary"),
                NodeEntity(escalationNode, WorkflowNodeKind.Escalation, "editor-in-chief")
            ],
            Edges =
            [
                EdgeEntity(reviewerNode, "Rejected", legalNode, sortOrder: 0),
                EdgeEntity(reviewerNode, "Approved", writerNode, rotatesRound: true, sortOrder: 1),
                EdgeEntity(publisherNode, "Completed", archivePrimaryNode, sortOrder: 0),
                EdgeEntity(publisherNode, "Completed", archiveSecondaryNode, sortOrder: 1)
            ],
            Inputs =
            [
                new WorkflowInputEntity
                {
                    Key = "topic",
                    DisplayName = "Article topic",
                    Kind = WorkflowInputKind.Text,
                    Required = true,
                    Ordinal = 0
                }
            ]
        });
        await seedContext.SaveChangesAsync();

        await using var readContext = CreateDbContext();
        var repository = new WorkflowRepository(readContext);
        var workflow = await repository.GetAsync(workflowKey, 2);

        workflow.Name.Should().Be("Article flow");
        workflow.Nodes.Should().HaveCount(8);
        workflow.StartNode.AgentKey.Should().Be("writer");
        workflow.EscalationNode.Should().NotBeNull();
        workflow.EscalationNode!.AgentKey.Should().Be("editor-in-chief");
        workflow.Inputs.Should().ContainSingle(input => input.Key == "topic");

        var rejectedEdge = await repository.FindNextAsync(workflowKey, 2, reviewerNode, "Rejected");
        rejectedEdge.Should().NotBeNull();
        rejectedEdge!.ToNodeId.Should().Be(legalNode);
        rejectedEdge.RotatesRound.Should().BeFalse();

        var approvedEdge = await repository.FindNextAsync(workflowKey, 2, reviewerNode, "Approved");
        approvedEdge.Should().NotBeNull();
        approvedEdge!.ToNodeId.Should().Be(writerNode);
        approvedEdge.RotatesRound.Should().BeTrue();

        var orderedEdge = await repository.FindNextAsync(workflowKey, 2, publisherNode, "Completed");
        orderedEdge.Should().NotBeNull();
        orderedEdge!.ToNodeId.Should().Be(archivePrimaryNode);
        orderedEdge.SortOrder.Should().Be(0);

        var missing = await repository.FindNextAsync(workflowKey, 2, reviewerNode, "Completed");
        missing.Should().BeNull();
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldPersistNodesEdgesAndInputs()
    {
        var workflowKey = $"draft-flow-{Guid.NewGuid():N}";
        var startNodeId = Guid.NewGuid();
        var finalNodeId = Guid.NewGuid();

        var draft = new WorkflowDraft(
            Key: workflowKey,
            Name: "Draft flow",
            MaxRoundsPerRound: 2,
            Nodes:
            [
                new WorkflowNodeDraft(startNodeId, WorkflowNodeKind.Start, "writer", 1, null, new[] { "Completed", "Failed" }, 0, 0),
                new WorkflowNodeDraft(finalNodeId, WorkflowNodeKind.Agent, "reviewer", 1, null, new[] { "Completed", "Failed" }, 250, 0)
            ],
            Edges:
            [
                new WorkflowEdgeDraft(startNodeId, "Completed", finalNodeId, WorkflowEdge.DefaultInputPort, false, 0)
            ],
            Inputs:
            [
                new WorkflowInputDraft("settings", "Runtime settings", WorkflowInputKind.Json, false, """{"tone":"casual"}""", "Optional tone override", 0)
            ]);

        await using var writeContext = CreateDbContext();
        var repository = new WorkflowRepository(writeContext);
        var version = await repository.CreateNewVersionAsync(draft);

        version.Should().Be(1);

        await using var readContext = CreateDbContext();
        var reloaded = await new WorkflowRepository(readContext).GetAsync(workflowKey, 1);
        reloaded.Nodes.Should().HaveCount(2);
        reloaded.Edges.Should().ContainSingle()
            .Which.FromNodeId.Should().Be(startNodeId);
        reloaded.Inputs.Should().ContainSingle()
            .Which.Kind.Should().Be(WorkflowInputKind.Json);
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldRoundTripAgentNodeWithScriptAndCustomPorts()
    {
        // Covers Slice 3 of the agent-attached routing scripts epic: an Agent/HITL node
        // may carry a Script + custom OutputPorts list that survives persistence round-trip.
        var workflowKey = $"scripted-agent-{Guid.NewGuid():N}";
        var startNodeId = Guid.NewGuid();
        var hitlNodeId = Guid.NewGuid();
        var acceptNodeId = Guid.NewGuid();
        var exitNodeId = Guid.NewGuid();

        const string interviewerScript = """
            var prior = (context.transcript || []).slice();
            prior.push({ q: input.question, a: input.answer });
            setContext('transcript', prior);
            setNodePath(input.answer === 'end' ? 'NextTurn' : 'NextTurn');
            """;
        const string hitlScript = """
            setNodePath(input.decision === 'Approved' ? 'Answer' : 'Exit');
            """;

        var draft = new WorkflowDraft(
            Key: workflowKey,
            Name: "Scripted interviewer",
            MaxRoundsPerRound: 5,
            Nodes:
            [
                new WorkflowNodeDraft(startNodeId, WorkflowNodeKind.Start, "interviewer", 1,
                    interviewerScript, new[] { "NextTurn" }, 0, 0),
                new WorkflowNodeDraft(hitlNodeId, WorkflowNodeKind.Hitl, "interviewee", 1,
                    hitlScript, new[] { "Answer", "Exit" }, 250, 0),
                new WorkflowNodeDraft(acceptNodeId, WorkflowNodeKind.Agent, "continueFlow", 1,
                    null, new[] { "Completed", "Failed" }, 500, 0),
                new WorkflowNodeDraft(exitNodeId, WorkflowNodeKind.Agent, "exitFlow", 1,
                    null, new[] { "Completed", "Failed" }, 500, 0)
            ],
            Edges:
            [
                new WorkflowEdgeDraft(startNodeId, "NextTurn", hitlNodeId, WorkflowEdge.DefaultInputPort, false, 0),
                new WorkflowEdgeDraft(hitlNodeId, "Answer", acceptNodeId, WorkflowEdge.DefaultInputPort, false, 1),
                new WorkflowEdgeDraft(hitlNodeId, "Exit", exitNodeId, WorkflowEdge.DefaultInputPort, false, 2)
            ],
            Inputs: Array.Empty<WorkflowInputDraft>());

        await using var writeContext = CreateDbContext();
        var version = await new WorkflowRepository(writeContext).CreateNewVersionAsync(draft);
        version.Should().Be(1);

        await using var readContext = CreateDbContext();
        var reloaded = await new WorkflowRepository(readContext).GetAsync(workflowKey, 1);

        var reloadedStart = reloaded.Nodes.Single(n => n.Id == startNodeId);
        reloadedStart.Kind.Should().Be(WorkflowNodeKind.Start);
        reloadedStart.Script.Should().Be(interviewerScript);
        reloadedStart.OutputPorts.Should().Equal("NextTurn");

        var reloadedHitl = reloaded.Nodes.Single(n => n.Id == hitlNodeId);
        reloadedHitl.Kind.Should().Be(WorkflowNodeKind.Hitl);
        reloadedHitl.Script.Should().Be(hitlScript);
        reloadedHitl.OutputPorts.Should().Equal("Answer", "Exit");

        var reloadedAccept = reloaded.Nodes.Single(n => n.Id == acceptNodeId);
        reloadedAccept.Script.Should().BeNull("unscripted agent nodes must round-trip with null Script");
    }

    private static WorkflowNodeEntity NodeEntity(Guid nodeId, WorkflowNodeKind kind, string agentKey)
    {
        return new WorkflowNodeEntity
        {
            NodeId = nodeId,
            Kind = kind,
            AgentKey = agentKey,
            AgentVersion = 1,
            OutputPortsJson = """["Completed","Approved","ApprovedWithActions","Rejected","Failed"]""",
            LayoutX = 0,
            LayoutY = 0
        };
    }

    private static WorkflowEdgeEntity EdgeEntity(
        Guid fromNodeId,
        string fromPort,
        Guid toNodeId,
        bool rotatesRound = false,
        int sortOrder = 0)
    {
        return new WorkflowEdgeEntity
        {
            FromNodeId = fromNodeId,
            FromPort = fromPort,
            ToNodeId = toNodeId,
            ToPort = WorkflowEdge.DefaultInputPort,
            RotatesRound = rotatesRound,
            SortOrder = sortOrder
        };
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
