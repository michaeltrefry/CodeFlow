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
                NodeEntity(archiveSecondaryNode, WorkflowNodeKind.Agent, "archive-secondary")
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
        workflow.Nodes.Should().HaveCount(7);
        workflow.StartNode.AgentKey.Should().Be("writer");
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
        reloadedStart.OutputScript.Should().Be(interviewerScript);
        reloadedStart.OutputPorts.Should().Equal("NextTurn");

        var reloadedHitl = reloaded.Nodes.Single(n => n.Id == hitlNodeId);
        reloadedHitl.Kind.Should().Be(WorkflowNodeKind.Hitl);
        reloadedHitl.OutputScript.Should().Be(hitlScript);
        reloadedHitl.OutputPorts.Should().Equal("Answer", "Exit");

        var reloadedAccept = reloaded.Nodes.Single(n => n.Id == acceptNodeId);
        reloadedAccept.OutputScript.Should().BeNull("unscripted agent nodes must round-trip with null OutputScript");
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldRoundTripSubflowNodeWithKeyAndVersion()
    {
        // Covers Slice S1 of the Subworkflow Composition epic: a Subflow node carries a
        // SubflowKey + nullable SubflowVersion that survive persistence round-trip. Both an
        // explicit-version and a "latest at save" (null version) variant are checked.
        var workflowKey = $"composer-{Guid.NewGuid():N}";
        var startNodeId = Guid.NewGuid();
        var pinnedSubflowNodeId = Guid.NewGuid();
        var latestSubflowNodeId = Guid.NewGuid();

        var draft = new WorkflowDraft(
            Key: workflowKey,
            Name: "Composer",
            MaxRoundsPerRound: 3,
            Nodes:
            [
                new WorkflowNodeDraft(startNodeId, WorkflowNodeKind.Start, "kickoff", 1,
                    null, new[] { "Completed", "Failed" }, 0, 0),
                new WorkflowNodeDraft(pinnedSubflowNodeId, WorkflowNodeKind.Subflow, AgentKey: null,
                    AgentVersion: null, OutputScript: null, OutputPorts: new[] { "Completed", "Failed", "Escalated" },
                    LayoutX: 250, LayoutY: 0, SubflowKey: "child-flow", SubflowVersion: 7),
                new WorkflowNodeDraft(latestSubflowNodeId, WorkflowNodeKind.Subflow, AgentKey: null,
                    AgentVersion: null, OutputScript: null, OutputPorts: new[] { "Completed", "Failed", "Escalated" },
                    LayoutX: 500, LayoutY: 0, SubflowKey: "shared-utility", SubflowVersion: null)
            ],
            Edges:
            [
                new WorkflowEdgeDraft(startNodeId, "Completed", pinnedSubflowNodeId, WorkflowEdge.DefaultInputPort, false, 0),
                new WorkflowEdgeDraft(pinnedSubflowNodeId, "Completed", latestSubflowNodeId, WorkflowEdge.DefaultInputPort, false, 1)
            ],
            Inputs: Array.Empty<WorkflowInputDraft>());

        await using var writeContext = CreateDbContext();
        var version = await new WorkflowRepository(writeContext).CreateNewVersionAsync(draft);
        version.Should().Be(1);

        await using var readContext = CreateDbContext();
        var reloaded = await new WorkflowRepository(readContext).GetAsync(workflowKey, 1);

        var pinned = reloaded.Nodes.Single(n => n.Id == pinnedSubflowNodeId);
        pinned.Kind.Should().Be(WorkflowNodeKind.Subflow);
        pinned.SubflowKey.Should().Be("child-flow");
        pinned.SubflowVersion.Should().Be(7);
        pinned.AgentKey.Should().BeNull();
        pinned.OutputPorts.Should().Equal("Completed", "Failed", "Escalated");

        var latest = reloaded.Nodes.Single(n => n.Id == latestSubflowNodeId);
        latest.Kind.Should().Be(WorkflowNodeKind.Subflow);
        latest.SubflowKey.Should().Be("shared-utility");
        latest.SubflowVersion.Should().BeNull("null SubflowVersion encodes 'latest at save' until S9 resolution");

        var nonSubflowStart = reloaded.Nodes.Single(n => n.Id == startNodeId);
        nonSubflowStart.SubflowKey.Should().BeNull("non-Subflow nodes must round-trip with null SubflowKey");
        nonSubflowStart.SubflowVersion.Should().BeNull();
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldRoundTripReviewLoopNodeWithMaxRounds()
    {
        // Covers Slice 1 of the ReviewLoop Node epic: a ReviewLoop node reuses the
        // SubflowKey + SubflowVersion columns from Subflow and adds a ReviewMaxRounds
        // setting. All three fields must survive a persistence round-trip, and non-
        // ReviewLoop nodes must keep ReviewMaxRounds null.
        var workflowKey = $"review-loop-{Guid.NewGuid():N}";
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();
        var plainSubflowNodeId = Guid.NewGuid();

        var draft = new WorkflowDraft(
            Key: workflowKey,
            Name: "Review-loop composer",
            MaxRoundsPerRound: 3,
            Nodes:
            [
                new WorkflowNodeDraft(startNodeId, WorkflowNodeKind.Start, "kickoff", 1,
                    null, new[] { "Completed", "Failed" }, 0, 0),
                new WorkflowNodeDraft(reviewLoopNodeId, WorkflowNodeKind.ReviewLoop, AgentKey: null,
                    AgentVersion: null, OutputScript: null,
                    OutputPorts: new[] { "Approved", "Exhausted", "Failed" },
                    LayoutX: 250, LayoutY: 0,
                    SubflowKey: "draft-critique-revise", SubflowVersion: 2,
                    ReviewMaxRounds: 3),
                new WorkflowNodeDraft(plainSubflowNodeId, WorkflowNodeKind.Subflow, AgentKey: null,
                    AgentVersion: null, OutputScript: null,
                    OutputPorts: new[] { "Completed", "Failed", "Escalated" },
                    LayoutX: 500, LayoutY: 0,
                    SubflowKey: "follow-up", SubflowVersion: null)
            ],
            Edges:
            [
                new WorkflowEdgeDraft(startNodeId, "Completed", reviewLoopNodeId, WorkflowEdge.DefaultInputPort, false, 0),
                new WorkflowEdgeDraft(reviewLoopNodeId, "Approved", plainSubflowNodeId, WorkflowEdge.DefaultInputPort, false, 1)
            ],
            Inputs: Array.Empty<WorkflowInputDraft>());

        await using var writeContext = CreateDbContext();
        var version = await new WorkflowRepository(writeContext).CreateNewVersionAsync(draft);
        version.Should().Be(1);

        await using var readContext = CreateDbContext();
        var reloaded = await new WorkflowRepository(readContext).GetAsync(workflowKey, 1);

        var reviewLoop = reloaded.Nodes.Single(n => n.Id == reviewLoopNodeId);
        reviewLoop.Kind.Should().Be(WorkflowNodeKind.ReviewLoop);
        reviewLoop.SubflowKey.Should().Be("draft-critique-revise");
        reviewLoop.SubflowVersion.Should().Be(2);
        reviewLoop.ReviewMaxRounds.Should().Be(3);
        reviewLoop.OutputPorts.Should().Equal("Approved", "Exhausted", "Failed");

        var plainSubflow = reloaded.Nodes.Single(n => n.Id == plainSubflowNodeId);
        plainSubflow.Kind.Should().Be(WorkflowNodeKind.Subflow);
        plainSubflow.ReviewMaxRounds.Should().BeNull("non-ReviewLoop nodes must round-trip with null ReviewMaxRounds");

        var nonReviewStart = reloaded.Nodes.Single(n => n.Id == startNodeId);
        nonReviewStart.ReviewMaxRounds.Should().BeNull();
    }

    [Fact]
    public async Task CreateNewVersionAsync_ShouldRoundTripOptOutLastRoundReminderFlag()
    {
        // P2 (Workflow Authoring DX): the workflow node carries an OptOutLastRoundReminder flag
        // that suppresses runtime auto-injection of @codeflow/last-round-reminder for agents
        // dispatched inside ReviewLoop child sagas. Default false means new agents get the
        // reminder; setting true must survive a save/load cycle.
        var workflowKey = $"opt-out-reminder-{Guid.NewGuid():N}";
        var startNodeId = Guid.NewGuid();
        var defaultReviewerId = Guid.NewGuid();
        var optedOutReviewerId = Guid.NewGuid();

        var draft = new WorkflowDraft(
            Key: workflowKey,
            Name: "Opt-out reminder flow",
            MaxRoundsPerRound: 3,
            Nodes:
            [
                new WorkflowNodeDraft(startNodeId, WorkflowNodeKind.Start, "kickoff", 1,
                    null, new[] { "Completed", "Failed" }, 0, 0),
                new WorkflowNodeDraft(defaultReviewerId, WorkflowNodeKind.Agent, "default-reviewer", 1,
                    null, new[] { "Approved", "Rejected", "Failed" }, 250, 0),
                new WorkflowNodeDraft(optedOutReviewerId, WorkflowNodeKind.Agent, "opted-out-reviewer", 1,
                    null, new[] { "Approved", "Rejected", "Failed" }, 500, 0,
                    OptOutLastRoundReminder: true)
            ],
            Edges:
            [
                new WorkflowEdgeDraft(startNodeId, "Completed", defaultReviewerId, WorkflowEdge.DefaultInputPort, false, 0),
                new WorkflowEdgeDraft(defaultReviewerId, "Approved", optedOutReviewerId, WorkflowEdge.DefaultInputPort, false, 1)
            ],
            Inputs: Array.Empty<WorkflowInputDraft>());

        await using var writeContext = CreateDbContext();
        var version = await new WorkflowRepository(writeContext).CreateNewVersionAsync(draft);
        version.Should().Be(1);

        await using var readContext = CreateDbContext();
        var reloaded = await new WorkflowRepository(readContext).GetAsync(workflowKey, 1);

        var defaultReviewer = reloaded.Nodes.Single(n => n.Id == defaultReviewerId);
        defaultReviewer.OptOutLastRoundReminder.Should().BeFalse(
            "agents that don't set the flag must default to opt-in (auto-injection enabled)");

        var optedOutReviewer = reloaded.Nodes.Single(n => n.Id == optedOutReviewerId);
        optedOutReviewer.OptOutLastRoundReminder.Should().BeTrue();

        var startNode = reloaded.Nodes.Single(n => n.Id == startNodeId);
        startNode.OptOutLastRoundReminder.Should().BeFalse(
            "non-agent nodes carry the column too but always default to false");
    }

    [Fact]
    public async Task GetTerminalPortsAsync_ShouldReturnUnwiredDeclaredPortsAcrossNodes()
    {
        var workflowKey = $"terminal-ports-{Guid.NewGuid():N}";
        var startNode = Guid.NewGuid();
        var fanOutNode = Guid.NewGuid();
        var leftLeaf = Guid.NewGuid();
        var rightLeaf = Guid.NewGuid();

        await using var seedContext = CreateDbContext();
        seedContext.Workflows.Add(new WorkflowEntity
        {
            Key = workflowKey,
            Version = 1,
            Name = "Terminal-port flow",
            MaxRoundsPerRound = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                NodeEntityWithPorts(startNode, WorkflowNodeKind.Start, "kickoff", new[] { "Completed" }),
                NodeEntityWithPorts(fanOutNode, WorkflowNodeKind.Agent, "router", new[] { "Left", "Right" }),
                NodeEntityWithPorts(leftLeaf, WorkflowNodeKind.Agent, "leftLeaf", new[] { "Approved", "Rejected" }),
                NodeEntityWithPorts(rightLeaf, WorkflowNodeKind.Agent, "rightLeaf", new[] { "Done" }),
            ],
            Edges =
            [
                EdgeEntity(startNode, "Completed", fanOutNode, sortOrder: 0),
                EdgeEntity(fanOutNode, "Left", leftLeaf, sortOrder: 1),
                EdgeEntity(fanOutNode, "Right", rightLeaf, sortOrder: 2),
            ],
            Inputs = []
        });
        await seedContext.SaveChangesAsync();

        await using var readContext = CreateDbContext();
        var repository = new WorkflowRepository(readContext);

        var terminals = await repository.GetTerminalPortsAsync(workflowKey, 1);

        terminals.Should().BeEquivalentTo(new[] { "Approved", "Rejected", "Done" });
    }

    private static WorkflowNodeEntity NodeEntityWithPorts(
        Guid nodeId,
        WorkflowNodeKind kind,
        string agentKey,
        IReadOnlyList<string> ports)
    {
        return new WorkflowNodeEntity
        {
            NodeId = nodeId,
            Kind = kind,
            AgentKey = agentKey,
            AgentVersion = 1,
            OutputPortsJson = System.Text.Json.JsonSerializer.Serialize(ports),
            LayoutX = 0,
            LayoutY = 0,
        };
    }

    private static WorkflowNodeEntity NodeEntity(Guid nodeId, WorkflowNodeKind kind, string agentKey)
    {
        return new WorkflowNodeEntity
        {
            NodeId = nodeId,
            Kind = kind,
            AgentKey = agentKey,
            AgentVersion = 1,
            OutputPortsJson = """["Completed","Approved","Rejected","Failed"]""",
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
