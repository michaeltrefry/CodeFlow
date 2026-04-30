using CodeFlow.Api.Mcp;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

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

    private static CodeFlowDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
            .UseInMemoryDatabase($"workflow-package-importer-tests-{Guid.NewGuid():N}")
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
