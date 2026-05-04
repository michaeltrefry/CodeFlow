using CodeFlow.Api.Mcp;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Tests.WorkflowPackages;

public sealed class WorkflowPackageImporterTests
{
    [Fact]
    public async Task PreviewAsync_ReturnsConflict_WhenMcpEndpointPolicyRejectsPackageServer()
    {
        await using var dbContext = CreateDbContext();
        var importer = new WorkflowPackageImporter(
            dbContext,
            new WorkflowRepository(dbContext),
            new AgentConfigRepository(dbContext),
            new AgentRoleRepository(dbContext),
            new SkillRepository(dbContext),
            new UnusedMcpServerRepository(),
            new RejectingMcpEndpointPolicy("Host is not allowed."));

        var preview = await importer.PreviewAsync(CreatePackageWithMcpServer("http://127.0.0.1/mcp"));

        preview.CanApply.Should().BeFalse();
        preview.Items.Should().ContainSingle(item =>
            item.Kind == WorkflowPackageImportResourceKind.McpServer &&
            item.Action == WorkflowPackageImportAction.Conflict &&
            item.Message == "Host is not allowed.");
    }

    /// <summary>
    /// sc-393: a stale-package conflict (package agent v2, library agent v3) must surface the
    /// structured `SourceVersion` (2) and `ExistingMaxVersion` (3) so the imports-page resolver
    /// (sc-396) can compute Bump / UseExisting / Copy choices without parsing the message string.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_PopulatesSourceAndExistingMaxVersion_OnAgentVersionConflict()
    {
        await using var dbContext = CreateDbContext();

        dbContext.Agents.Add(new AgentConfigEntity
        {
            Key = "writer",
            Version = 3,
            ConfigJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "sc-393-test",
            IsActive = true,
        });
        await dbContext.SaveChangesAsync();

        var importer = new WorkflowPackageImporter(
            dbContext,
            new WorkflowRepository(dbContext),
            new AgentConfigRepository(dbContext),
            new AgentRoleRepository(dbContext),
            new SkillRepository(dbContext),
            new UnusedMcpServerRepository(),
            new AllowingMcpEndpointPolicy());

        var preview = await importer.PreviewAsync(CreatePackageWithAgent("writer", agentVersion: 2));

        preview.CanApply.Should().BeFalse();
        var conflict = preview.Items.Should().ContainSingle(item =>
            item.Kind == WorkflowPackageImportResourceKind.Agent &&
            item.Action == WorkflowPackageImportAction.Conflict).Subject;
        conflict.Version.Should().Be(2);
        conflict.SourceVersion.Should().Be(2);
        conflict.ExistingMaxVersion.Should().Be(3);
    }

    /// <summary>
    /// sc-394 UseExisting (Agent): the conflicted agent is dropped from the package and every
    /// workflow node that pinned it now references the library's higher version. ApplyAsync
    /// persists no new agent row; the library row stays untouched.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_AppliesUseExistingResolution_DropsAgentAndRewritesNodeRefs()
    {
        await using var dbContext = CreateDbContext();
        SeedAgent(dbContext, "writer", version: 3, isActive: true);
        await dbContext.SaveChangesAsync();

        var importer = NewImporter(dbContext);
        var package = CreatePackageWithAgentReferencedByThreeNodes("writer", agentVersion: 2);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2)]
                = new WorkflowPackageImportResolution(
                    new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2),
                    WorkflowPackageImportResolutionMode.UseExisting),
        };

        var preview = await importer.PreviewAsync(package, resolutions, CancellationToken.None);

        preview.CanApply.Should().BeTrue("UseExisting drops the conflicted agent so no Conflict rows remain");
        preview.Items.Should().NotContain(item =>
            item.Kind == WorkflowPackageImportResourceKind.Agent &&
            item.Action == WorkflowPackageImportAction.Conflict);

        await importer.ApplyAsync(package, resolutions, CancellationToken.None);

        // Library still has only the original v3 row — UseExisting writes nothing.
        var writerRows = await dbContext.Agents.Where(a => a.Key == "writer").ToListAsync();
        writerRows.Should().ContainSingle();
        writerRows.Single().Version.Should().Be(3);

        // Persisted workflow nodes all reference writer v3 (rewritten from v2).
        var imported = await dbContext.Workflows
            .Include(w => w.Nodes)
            .SingleAsync(w => w.Key == "root" && w.Version == 1);
        imported.Nodes.Should().HaveCount(3);
        imported.Nodes.Should().OnlyContain(node => node.AgentKey == "writer" && node.AgentVersion == 3);
    }

    /// <summary>
    /// sc-394 UseExisting refuses if the library has no version for the target key — there's
    /// nothing to point the rewritten refs at.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_RejectsUseExisting_WhenLibraryHasNoVersionForKey()
    {
        await using var dbContext = CreateDbContext();
        // No 'writer' rows seeded.
        var importer = NewImporter(dbContext);
        var package = CreatePackageWithAgentReferencedByThreeNodes("writer", agentVersion: 2);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2)]
                = new WorkflowPackageImportResolution(
                    new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2),
                    WorkflowPackageImportResolutionMode.UseExisting),
        };

        var attempt = async () => await importer.PreviewAsync(package, resolutions, CancellationToken.None);

        await attempt.Should().ThrowAsync<WorkflowPackageResolutionException>()
            .WithMessage("*UseExisting requires the library to already contain an agent*");
    }

    /// <summary>
    /// sc-394 Bump (Agent): rewrites the agent's version to existingMax+1 and writes
    /// ForkedFromKey/Version lineage on the new row. Workflow nodes pinning the source
    /// version are rewritten to the bumped target version.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_AppliesBumpResolution_PersistsAtBumpedVersionWithLineage()
    {
        await using var dbContext = CreateDbContext();
        SeedAgent(dbContext, "writer", version: 3, isActive: true);
        await dbContext.SaveChangesAsync();

        var importer = NewImporter(dbContext);
        var package = CreatePackageWithAgentReferencedByThreeNodes("writer", agentVersion: 2);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2)]
                = new WorkflowPackageImportResolution(
                    new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2),
                    WorkflowPackageImportResolutionMode.Bump),
        };

        await importer.ApplyAsync(package, resolutions, CancellationToken.None);

        var writerRows = await dbContext.Agents
            .Where(a => a.Key == "writer")
            .OrderBy(a => a.Version)
            .ToListAsync();
        writerRows.Should().HaveCount(2);
        writerRows[0].Version.Should().Be(3);
        writerRows[0].IsActive.Should().BeFalse("the bumped row takes Active");
        writerRows[1].Version.Should().Be(4, "Bump targets existingMax + 1");
        writerRows[1].IsActive.Should().BeTrue();
        writerRows[1].ForkedFromKey.Should().Be("writer");
        writerRows[1].ForkedFromVersion.Should().Be(2);

        var imported = await dbContext.Workflows
            .Include(w => w.Nodes)
            .SingleAsync(w => w.Key == "root" && w.Version == 1);
        imported.Nodes.Should().OnlyContain(node => node.AgentKey == "writer" && node.AgentVersion == 4);
    }

    /// <summary>
    /// sc-394 Copy (Agent): renames the agent to NewKey at v1, writes lineage to the original
    /// (key, version), rewrites every workflow-node ref, and renames the package's role
    /// assignment for the old agent key onto the new copy.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_AppliesCopyResolution_PersistsNewKeyV1WithLineageAndRewrites()
    {
        await using var dbContext = CreateDbContext();
        SeedAgent(dbContext, "writer", version: 3, isActive: true);
        await dbContext.SaveChangesAsync();

        var importer = NewImporter(dbContext);
        var package = CreatePackageWithAgentRoleAssignment("writer", agentVersion: 2, roleKey: "writer-tools");
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2)]
                = new WorkflowPackageImportResolution(
                    new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2),
                    WorkflowPackageImportResolutionMode.Copy,
                    NewKey: "writer-copy"),
        };

        await importer.ApplyAsync(package, resolutions, CancellationToken.None);

        // Original 'writer' library row is untouched.
        var writerRows = await dbContext.Agents.Where(a => a.Key == "writer").ToListAsync();
        writerRows.Should().ContainSingle();
        writerRows.Single().Version.Should().Be(3);

        // New copy persists at v1 with lineage to (writer, 2).
        var copyRows = await dbContext.Agents.Where(a => a.Key == "writer-copy").ToListAsync();
        copyRows.Should().ContainSingle();
        var copy = copyRows.Single();
        copy.Version.Should().Be(1);
        copy.IsActive.Should().BeTrue();
        copy.ForkedFromKey.Should().Be("writer");
        copy.ForkedFromVersion.Should().Be(2);

        // Workflow node ref rewritten to writer-copy v1.
        var imported = await dbContext.Workflows
            .Include(w => w.Nodes)
            .SingleAsync(w => w.Key == "root" && w.Version == 1);
        var startNode = imported.Nodes.Single();
        startNode.AgentKey.Should().Be("writer-copy");
        startNode.AgentVersion.Should().Be(1);

        // Role assignment renamed onto the new copy.
        var assignments = await dbContext.AgentRoleAssignments.ToListAsync();
        assignments.Should().ContainSingle();
        assignments.Single().AgentKey.Should().Be("writer-copy");
    }

    /// <summary>
    /// sc-394 transitive-ref test (story acceptance): a single agent referenced by three
    /// distinct workflow nodes — all three rewrite consistently under each mode.
    /// </summary>
    [Theory]
    [InlineData(WorkflowPackageImportResolutionMode.UseExisting, /*newKey*/ null, /*expectedKey*/ "writer", /*expectedVersion*/ 3)]
    [InlineData(WorkflowPackageImportResolutionMode.Bump,        /*newKey*/ null, /*expectedKey*/ "writer", /*expectedVersion*/ 4)]
    [InlineData(WorkflowPackageImportResolutionMode.Copy,        /*newKey*/ "writer-copy", /*expectedKey*/ "writer-copy", /*expectedVersion*/ 1)]
    public async Task ApplyAsync_RewritesAllThreeTransitiveRefs_UnderEachResolutionMode(
        WorkflowPackageImportResolutionMode mode,
        string? newKey,
        string expectedKey,
        int expectedVersion)
    {
        await using var dbContext = CreateDbContext();
        SeedAgent(dbContext, "writer", version: 3, isActive: true);
        await dbContext.SaveChangesAsync();

        var importer = NewImporter(dbContext);
        var package = CreatePackageWithAgentReferencedByThreeNodes("writer", agentVersion: 2);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2)]
                = new WorkflowPackageImportResolution(
                    new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2),
                    mode,
                    newKey),
        };

        await importer.ApplyAsync(package, resolutions, CancellationToken.None);

        var imported = await dbContext.Workflows
            .Include(w => w.Nodes)
            .SingleAsync(w => w.Key == "root" && w.Version == 1);
        imported.Nodes.Should().HaveCount(3);
        imported.Nodes.Should().OnlyContain(node =>
            node.AgentKey == expectedKey && node.AgentVersion == expectedVersion);
    }

    /// <summary>
    /// sc-394 Workflow Bump rewrites the entry-point reference if the resolution targets it.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_AppliesWorkflowBump_RewritesEntryPointAndPersistsAtBumpedVersion()
    {
        await using var dbContext = CreateDbContext();
        SeedWorkflow(dbContext, "root", version: 3);
        await dbContext.SaveChangesAsync();

        var importer = NewImporter(dbContext);
        var package = CreatePackageWithEntryPointWorkflow("root", workflowVersion: 2);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Workflow, "root", 2)]
                = new WorkflowPackageImportResolution(
                    new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Workflow, "root", 2),
                    WorkflowPackageImportResolutionMode.Bump),
        };

        var result = await importer.ApplyAsync(package, resolutions, CancellationToken.None);

        result.EntryPoint.Key.Should().Be("root");
        result.EntryPoint.Version.Should().Be(4, "entry-point reference follows the Bump");

        var rows = await dbContext.Workflows
            .Where(w => w.Key == "root")
            .OrderBy(w => w.Version)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows.Last().Version.Should().Be(4);
    }

    /// <summary>
    /// sc-394 validation: UseExisting on the entry-point workflow is rejected — admission would
    /// fail post-resolution because the entry-point reference no longer resolves.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_RejectsUseExistingOnEntryPointWorkflow()
    {
        await using var dbContext = CreateDbContext();
        SeedWorkflow(dbContext, "root", version: 3);
        await dbContext.SaveChangesAsync();

        var importer = NewImporter(dbContext);
        var package = CreatePackageWithEntryPointWorkflow("root", workflowVersion: 2);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Workflow, "root", 2)]
                = new WorkflowPackageImportResolution(
                    new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Workflow, "root", 2),
                    WorkflowPackageImportResolutionMode.UseExisting),
        };

        var attempt = async () => await importer.PreviewAsync(package, resolutions, CancellationToken.None);

        await attempt.Should().ThrowAsync<WorkflowPackageResolutionException>()
            .WithMessage("*UseExisting is not valid for the entry-point workflow*");
    }

    /// <summary>
    /// sc-394 validation: Copy without a NewKey is rejected.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_RejectsCopyWithoutNewKey()
    {
        await using var dbContext = CreateDbContext();
        var importer = NewImporter(dbContext);
        var package = CreatePackageWithAgentReferencedByThreeNodes("writer", agentVersion: 2);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2)]
                = new WorkflowPackageImportResolution(
                    new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2),
                    WorkflowPackageImportResolutionMode.Copy,
                    NewKey: null),
        };

        var attempt = async () => await importer.PreviewAsync(package, resolutions, CancellationToken.None);

        await attempt.Should().ThrowAsync<WorkflowPackageResolutionException>()
            .WithMessage("*NewKey is required for Copy resolutions*");
    }

    /// <summary>
    /// sc-394 validation: Bump on an unversioned kind (Skill) is rejected.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_RejectsBumpOnUnversionedKind()
    {
        await using var dbContext = CreateDbContext();
        var importer = NewImporter(dbContext);
        var package = CreatePackageWithSkill("note", "body");
        var key = new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Skill, "note", null);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [key] = new WorkflowPackageImportResolution(key, WorkflowPackageImportResolutionMode.Bump),
        };

        var attempt = async () => await importer.PreviewAsync(package, resolutions, CancellationToken.None);

        await attempt.Should().ThrowAsync<WorkflowPackageResolutionException>()
            .WithMessage("*Bump is only valid for Agent or Workflow*");
    }

    /// <summary>
    /// sc-394 validation: resolution target must exist in the package.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_RejectsResolution_WhenTargetIsNotInPackage()
    {
        await using var dbContext = CreateDbContext();
        var importer = NewImporter(dbContext);
        var package = CreatePackageWithAgentReferencedByThreeNodes("writer", agentVersion: 2);
        var key = new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "ghost", 1);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [key] = new WorkflowPackageImportResolution(key, WorkflowPackageImportResolutionMode.Bump),
        };

        var attempt = async () => await importer.PreviewAsync(package, resolutions, CancellationToken.None);

        await attempt.Should().ThrowAsync<WorkflowPackageResolutionException>()
            .WithMessage("*does not exist in the package's Agents collection*");
    }

    /// <summary>
    /// sc-394 validation: two Copy resolutions cannot pick the same NewKey — would collide.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_RejectsTwoCopyResolutions_WithSameNewKey()
    {
        await using var dbContext = CreateDbContext();
        var importer = NewImporter(dbContext);
        var package = CreatePackageWithTwoAgents("writer", 2, "reviewer", 2);
        var keyA = new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "writer", 2);
        var keyB = new WorkflowPackageImportResolutionKey(WorkflowPackageImportResourceKind.Agent, "reviewer", 2);
        var resolutions = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>
        {
            [keyA] = new WorkflowPackageImportResolution(keyA, WorkflowPackageImportResolutionMode.Copy, NewKey: "shared"),
            [keyB] = new WorkflowPackageImportResolution(keyB, WorkflowPackageImportResolutionMode.Copy, NewKey: "shared"),
        };

        var attempt = async () => await importer.PreviewAsync(package, resolutions, CancellationToken.None);

        await attempt.Should().ThrowAsync<WorkflowPackageResolutionException>()
            .WithMessage("*used by more than one resolution*");
    }

    [Fact]
    public async Task ApplyAsync_PersistsAgentAndRoleTagsFromPackage()
    {
        await using var dbContext = CreateDbContext();
        var importer = new WorkflowPackageImporter(
            dbContext,
            new WorkflowRepository(dbContext),
            new AgentConfigRepository(dbContext),
            new AgentRoleRepository(dbContext),
            new SkillRepository(dbContext),
            new UnusedMcpServerRepository(),
            new AllowingMcpEndpointPolicy());

        await importer.ApplyAsync(CreatePackageWithTaggedAgentAndRole());

        var importedAgent = await dbContext.Agents.SingleAsync(agent => agent.Key == "writer");
        WorkflowJson.DeserializeTags(importedAgent.TagsJson).Should().Equal("author", "ops");

        var importedRole = await dbContext.AgentRoles.SingleAsync(role => role.Key == "writer-tools");
        WorkflowJson.DeserializeTags(importedRole.TagsJson).Should().Equal("author", "tools");
    }

    private static WorkflowPackageImporter NewImporter(CodeFlowDbContext dbContext) =>
        new(
            dbContext,
            new WorkflowRepository(dbContext),
            new AgentConfigRepository(dbContext),
            new AgentRoleRepository(dbContext),
            new SkillRepository(dbContext),
            new UnusedMcpServerRepository(),
            new AllowingMcpEndpointPolicy());

    private static void SeedAgent(CodeFlowDbContext dbContext, string key, int version, bool isActive)
    {
        // ConfigJson must declare a `Completed` output so the legacy WorkflowValidator
        // accepts UseExisting rewrites: when a Start node's port-route gets re-pinned to this
        // library row at apply-time, the validator looks at this row's DeclaredOutputs and
        // refuses if the routed port isn't there.
        dbContext.Agents.Add(new AgentConfigEntity
        {
            Key = key,
            Version = version,
            ConfigJson = SimpleAgentConfig().ToJsonString(),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "sc-394-test",
            IsActive = isActive,
        });
    }

    private static void SeedWorkflow(CodeFlowDbContext dbContext, string key, int version)
    {
        dbContext.Workflows.Add(new WorkflowEntity
        {
            Key = key,
            Version = version,
            Name = key,
            MaxRoundsPerRound = 1,
            Category = WorkflowCategory.Workflow,
            TagsJson = "[]",
            CreatedAtUtc = DateTime.UtcNow,
            Nodes = new List<WorkflowNodeEntity>(),
            Edges = new List<WorkflowEdgeEntity>(),
            Inputs = new List<WorkflowInputEntity>(),
        });
    }

    private static WorkflowPackage CreatePackageWithAgentReferencedByThreeNodes(string agentKey, int agentVersion)
    {
        // 1 Start + 2 Agent nodes connected linearly, all three pinned to the same (key,
        // version). Satisfies the legacy WorkflowValidator (exactly one Start, all reachable
        // from Start) so ApplyAsync can persist and we can assert post-resolution refs in the DB.
        var startNode = NodePinned(agentKey, agentVersion, WorkflowNodeKind.Start);
        var agent1 = NodePinned(agentKey, agentVersion, WorkflowNodeKind.Agent);
        var agent2 = NodePinned(agentKey, agentVersion, WorkflowNodeKind.Agent);

        var workflow = new WorkflowPackageWorkflow(
            Key: "root",
            Version: 1,
            Name: "Root",
            MaxRoundsPerRound: 1,
            Category: WorkflowCategory.Workflow,
            Tags: Array.Empty<string>(),
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[] { startNode, agent1, agent2 },
            Edges: new[]
            {
                new WorkflowPackageWorkflowEdge(startNode.Id, "Completed", agent1.Id, "in", false, 0),
                new WorkflowPackageWorkflowEdge(agent1.Id, "Completed", agent2.Id, "in", false, 1),
            },
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        var agent = new WorkflowPackageAgent(
            Key: agentKey,
            Version: agentVersion,
            Kind: AgentKind.Agent,
            Config: SimpleAgentConfig(),
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "sc-394-test",
            Outputs: new[] { new WorkflowPackageAgentOutput("Completed", "Done", null) });

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(workflow.Key, workflow.Version),
            Workflows: new[] { workflow },
            Agents: new[] { agent },
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }

    private static WorkflowPackage CreatePackageWithAgentRoleAssignment(string agentKey, int agentVersion, string roleKey)
    {
        var node = NodePinned(agentKey, agentVersion);
        var workflow = new WorkflowPackageWorkflow(
            Key: "root",
            Version: 1,
            Name: "Root",
            MaxRoundsPerRound: 1,
            Category: WorkflowCategory.Workflow,
            Tags: Array.Empty<string>(),
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[] { node },
            Edges: Array.Empty<WorkflowPackageWorkflowEdge>(),
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        var agent = new WorkflowPackageAgent(
            Key: agentKey,
            Version: agentVersion,
            Kind: AgentKind.Agent,
            Config: SimpleAgentConfig(),
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "sc-394-test",
            Outputs: new[] { new WorkflowPackageAgentOutput("Completed", "Done", null) });

        // Embed the role too: ImportRoleAssignmentsAsync only resolves role IDs from the
        // package's Roles collection (not unembedded references), so the assignment requires
        // its role to ride along.
        var role = new WorkflowPackageRole(
            Key: roleKey,
            DisplayName: roleKey,
            Description: null,
            IsArchived: false,
            ToolGrants: Array.Empty<WorkflowPackageRoleGrant>(),
            SkillNames: Array.Empty<string>());

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(workflow.Key, workflow.Version),
            Workflows: new[] { workflow },
            Agents: new[] { agent },
            AgentRoleAssignments: new[] { new WorkflowPackageAgentRoleAssignment(agentKey, new[] { roleKey }) },
            Roles: new[] { role },
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }

    private static WorkflowPackage CreatePackageWithEntryPointWorkflow(string workflowKey, int workflowVersion)
    {
        // Single Start node referencing an embedded agent — minimal valid graph that lets
        // ApplyAsync persist (the legacy validator requires exactly one Start node).
        var startNode = NodePinned("writer", 1);
        var agent = new WorkflowPackageAgent(
            Key: "writer",
            Version: 1,
            Kind: AgentKind.Agent,
            Config: SimpleAgentConfig(),
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "sc-394-test",
            Outputs: new[] { new WorkflowPackageAgentOutput("Completed", "Done", null) });

        var workflow = new WorkflowPackageWorkflow(
            Key: workflowKey,
            Version: workflowVersion,
            Name: workflowKey,
            MaxRoundsPerRound: 1,
            Category: WorkflowCategory.Workflow,
            Tags: Array.Empty<string>(),
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[] { startNode },
            Edges: Array.Empty<WorkflowPackageWorkflowEdge>(),
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(workflow.Key, workflow.Version),
            Workflows: new[] { workflow },
            Agents: new[] { agent },
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }

    private static WorkflowPackage CreatePackageWithSkill(string skillName, string body)
    {
        var workflow = new WorkflowPackageWorkflow(
            Key: "root",
            Version: 1,
            Name: "Root",
            MaxRoundsPerRound: 1,
            Category: WorkflowCategory.Workflow,
            Tags: Array.Empty<string>(),
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: Array.Empty<WorkflowPackageWorkflowNode>(),
            Edges: Array.Empty<WorkflowPackageWorkflowEdge>(),
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(workflow.Key, workflow.Version),
            Workflows: new[] { workflow },
            Agents: Array.Empty<WorkflowPackageAgent>(),
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: new[]
            {
                new WorkflowPackageSkill(
                    Name: skillName,
                    Body: body,
                    IsArchived: false,
                    CreatedAtUtc: DateTime.UtcNow,
                    CreatedBy: "sc-394-test",
                    UpdatedAtUtc: DateTime.UtcNow,
                    UpdatedBy: null),
            },
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }

    private static WorkflowPackage CreatePackageWithTwoAgents(string keyA, int versionA, string keyB, int versionB)
    {
        var workflow = new WorkflowPackageWorkflow(
            Key: "root",
            Version: 1,
            Name: "Root",
            MaxRoundsPerRound: 1,
            Category: WorkflowCategory.Workflow,
            Tags: Array.Empty<string>(),
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[] { NodePinned(keyA, versionA), NodePinned(keyB, versionB) },
            Edges: Array.Empty<WorkflowPackageWorkflowEdge>(),
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(workflow.Key, workflow.Version),
            Workflows: new[] { workflow },
            Agents: new[]
            {
                new WorkflowPackageAgent(keyA, versionA, AgentKind.Agent, SimpleAgentConfig(),
                    DateTime.UtcNow, "sc-394-test",
                    new[] { new WorkflowPackageAgentOutput("Completed", "Done", null) }),
                new WorkflowPackageAgent(keyB, versionB, AgentKind.Agent, SimpleAgentConfig(),
                    DateTime.UtcNow, "sc-394-test",
                    new[] { new WorkflowPackageAgentOutput("Completed", "Done", null) }),
            },
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }

    private static WorkflowPackageWorkflowNode NodePinned(
        string agentKey,
        int agentVersion,
        WorkflowNodeKind kind = WorkflowNodeKind.Start) =>
        new(
            Id: Guid.NewGuid(),
            Kind: kind,
            AgentKey: agentKey,
            AgentVersion: agentVersion,
            OutputScript: null,
            OutputPorts: new[] { "Completed" },
            LayoutX: 0,
            LayoutY: 0);

    private static JsonNode SimpleAgentConfig() =>
        JsonNode.Parse("""
            {
              "type": "agent",
              "name": "Writer",
              "provider": "openai",
              "model": "gpt-5",
              "systemPrompt": "Write the response, then submit.",
              "promptTemplate": "{{ input }}",
              "outputs": [
                { "kind": "Completed", "description": "Done" }
              ]
            }
            """)!;

    private static WorkflowPackage CreatePackageWithAgent(string agentKey, int agentVersion)
    {
        var workflow = new WorkflowPackageWorkflow(
            Key: "root",
            Version: 1,
            Name: "Root",
            MaxRoundsPerRound: 1,
            Category: WorkflowCategory.Workflow,
            Tags: Array.Empty<string>(),
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: Array.Empty<WorkflowPackageWorkflowNode>(),
            Edges: Array.Empty<WorkflowPackageWorkflowEdge>(),
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        var agent = new WorkflowPackageAgent(
            Key: agentKey,
            Version: agentVersion,
            Kind: AgentKind.Agent,
            Config: null,
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "sc-393-test",
            Outputs: Array.Empty<WorkflowPackageAgentOutput>());

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(workflow.Key, workflow.Version),
            Workflows: new[] { workflow },
            Agents: new[] { agent },
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }

    private static WorkflowPackage CreatePackageWithMcpServer(string endpointUrl)
    {
        var workflow = new WorkflowPackageWorkflow(
            Key: "root",
            Version: 1,
            Name: "Root",
            MaxRoundsPerRound: 1,
            Category: WorkflowCategory.Workflow,
            Tags: Array.Empty<string>(),
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: Array.Empty<WorkflowPackageWorkflowNode>(),
            Edges: Array.Empty<WorkflowPackageWorkflowEdge>(),
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(workflow.Key, workflow.Version),
            Workflows: new[] { workflow },
            Agents: Array.Empty<WorkflowPackageAgent>(),
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: new[]
            {
                new WorkflowPackageMcpServer(
                    Key: "search",
                    DisplayName: "Search",
                    Transport: McpTransportKind.StreamableHttp,
                    EndpointUrl: endpointUrl,
                    HasBearerToken: false,
                    HealthStatus: McpServerHealthStatus.Unverified,
                    LastVerifiedAtUtc: null,
                    LastVerificationError: null,
                    IsArchived: false,
                    Tools: Array.Empty<WorkflowPackageMcpServerTool>()),
            });
    }

    private static WorkflowPackage CreatePackageWithTaggedAgentAndRole()
    {
        var startNodeId = Guid.NewGuid();
        var config = JsonNode.Parse("""
            {
              "type": "agent",
              "name": "Writer",
              "provider": "openai",
              "model": "gpt-5",
              "systemPrompt": "Write the response, then submit.",
              "promptTemplate": "{{ input }}",
              "outputs": [
                { "kind": "Completed", "description": "Done" }
              ]
            }
            """)!;

        var workflow = new WorkflowPackageWorkflow(
            Key: "tagged-flow",
            Version: 1,
            Name: "Tagged Flow",
            MaxRoundsPerRound: 1,
            Category: WorkflowCategory.Workflow,
            Tags: ["demo"],
            CreatedAtUtc: DateTime.UtcNow,
            Nodes:
            [
                new WorkflowPackageWorkflowNode(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: "writer",
                    AgentVersion: 1,
                    OutputScript: null,
                    OutputPorts: ["Completed"],
                    LayoutX: 0,
                    LayoutY: 0),
            ],
            Edges: Array.Empty<WorkflowPackageWorkflowEdge>(),
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        var agent = new WorkflowPackageAgent(
            Key: "writer",
            Version: 1,
            Kind: AgentKind.Agent,
            Config: config,
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "sc-620-test",
            Outputs: [new WorkflowPackageAgentOutput("Completed", "Done", null)],
            Tags: ["author", "ops"]);

        var role = new WorkflowPackageRole(
            Key: "writer-tools",
            DisplayName: "Writer Tools",
            Description: "Tools for the writer.",
            IsArchived: false,
            ToolGrants: Array.Empty<WorkflowPackageRoleGrant>(),
            SkillNames: Array.Empty<string>(),
            Tags: ["author", "tools"]);

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(workflow.Key, workflow.Version),
            Workflows: [workflow],
            Agents: [agent],
            AgentRoleAssignments: [new WorkflowPackageAgentRoleAssignment("writer", ["writer-tools"])],
            Roles: [role],
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }

    private static CodeFlowDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
            .UseInMemoryDatabase($"workflow-package-importer-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CodeFlowDbContext(options);
    }

    private sealed class RejectingMcpEndpointPolicy(string reason) : IMcpEndpointPolicy
    {
        public ValueTask<McpEndpointPolicyResult> ValidateAsync(Uri endpoint, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new McpEndpointPolicyResult(false, reason));
    }

    private sealed class AllowingMcpEndpointPolicy : IMcpEndpointPolicy
    {
        public ValueTask<McpEndpointPolicyResult> ValidateAsync(Uri endpoint, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new McpEndpointPolicyResult(true, null));
    }

    private sealed class UnusedMcpServerRepository : IMcpServerRepository
    {
        public Task<IReadOnlyList<McpServer>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<McpServer?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<McpServer?> GetByKeyAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> CreateAsync(McpServerCreate create, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(long id, McpServerUpdate update, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ArchiveAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateHealthAsync(
            long id,
            McpServerHealthStatus status,
            DateTime? lastVerifiedAtUtc,
            string? lastError,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReplaceToolsAsync(
            long id,
            IReadOnlyList<McpServerToolWrite> tools,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<McpServerTool>> GetToolsAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<McpServerConnectionInfo?> GetConnectionInfoAsync(string serverKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
