using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using System.Text.Json;

namespace CodeFlow.Api.Tests.WorkflowPackages;

public sealed class WorkflowPackageResolverTests
{
    [Fact]
    public async Task ResolveAsync_includes_transitive_workflows_and_pins_unversioned_dependencies()
    {
        var rootStartId = Guid.NewGuid();
        var subflowNodeId = Guid.NewGuid();
        var childStartId = Guid.NewGuid();

        var workflows = new Dictionary<(string Key, int Version), Workflow>(StringTupleComparer.Ordinal)
        {
            [("root-flow", 1)] = new(
                Key: "root-flow",
                Version: 1,
                Name: "Root",
                MaxRoundsPerRound: 3,
                CreatedAtUtc: new DateTime(2026, 4, 23, 16, 0, 0, DateTimeKind.Utc),
                Nodes:
                [
                    new WorkflowNode(rootStartId, WorkflowNodeKind.Start, "writer", null, null, ["Completed"], 0, 0),
                    new WorkflowNode(subflowNodeId, WorkflowNodeKind.Subflow, null, null, null, ["Completed"], 200, 0, "child-flow", null),
                ],
                Edges: [],
                Inputs: []),
            [("child-flow", 2)] = new(
                Key: "child-flow",
                Version: 2,
                Name: "Child",
                MaxRoundsPerRound: 2,
                CreatedAtUtc: new DateTime(2026, 4, 23, 16, 1, 0, DateTimeKind.Utc),
                Nodes:
                [
                    new WorkflowNode(childStartId, WorkflowNodeKind.Start, "reviewer", null, null, ["Completed"], 0, 0),
                ],
                Edges: [],
                Inputs: []),
        };

        var agents = new Dictionary<(string Key, int Version), AgentConfig>(StringTupleComparer.Ordinal)
        {
            [("writer", 3)] = CreateAgent("writer", 3, """{"provider":"openai","model":"gpt-5","systemPrompt":"Write"}"""),
            [("reviewer", 5)] = CreateAgent("reviewer", 5, """{"provider":"openai","model":"gpt-5","systemPrompt":"Review"}"""),
        };

        var resolver = CreateResolver(
            workflows,
            agents,
            latestWorkflowVersions: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["child-flow"] = 2,
            },
            latestAgentVersions: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["writer"] = 3,
                ["reviewer"] = 5,
            });

        var package = await resolver.ResolveAsync("root-flow", 1);

        package.EntryPoint.Should().BeEquivalentTo(new WorkflowPackageReference("root-flow", 1));
        package.Workflows.Select(w => (w.Key, w.Version)).Should().BeEquivalentTo(
        [
            ("root-flow", 1),
            ("child-flow", 2),
        ]);
        package.Agents.Select(a => (a.Key, a.Version)).Should().BeEquivalentTo(
        [
            ("writer", 3),
            ("reviewer", 5),
        ]);

        var root = package.Workflows.Single(w => w.Key == "root-flow");
        root.Nodes.Single(n => n.Id == rootStartId).AgentVersion.Should().Be(3);
        root.Nodes.Single(n => n.Id == subflowNodeId).SubflowVersion.Should().Be(2);

        var child = package.Workflows.Single(w => w.Key == "child-flow");
        child.Nodes.Single().AgentVersion.Should().Be(5);
    }

    [Fact]
    public async Task ResolveAsync_preserves_explicitly_pinned_versions()
    {
        var rootStartId = Guid.NewGuid();
        var subflowNodeId = Guid.NewGuid();
        var childStartId = Guid.NewGuid();

        var workflows = new Dictionary<(string Key, int Version), Workflow>(StringTupleComparer.Ordinal)
        {
            [("root-flow", 1)] = new(
                Key: "root-flow",
                Version: 1,
                Name: "Root",
                MaxRoundsPerRound: 3,
                CreatedAtUtc: DateTime.UtcNow,
                Nodes:
                [
                    new WorkflowNode(rootStartId, WorkflowNodeKind.Start, "writer", 1, null, ["Completed"], 0, 0),
                    new WorkflowNode(subflowNodeId, WorkflowNodeKind.Subflow, null, null, null, ["Completed"], 200, 0, "child-flow", 4),
                ],
                Edges: [],
                Inputs: []),
            [("child-flow", 4)] = new(
                Key: "child-flow",
                Version: 4,
                Name: "Child",
                MaxRoundsPerRound: 2,
                CreatedAtUtc: DateTime.UtcNow,
                Nodes:
                [
                    new WorkflowNode(childStartId, WorkflowNodeKind.Start, "reviewer", 2, null, ["Completed"], 0, 0),
                ],
                Edges: [],
                Inputs: []),
        };

        var agents = new Dictionary<(string Key, int Version), AgentConfig>(StringTupleComparer.Ordinal)
        {
            [("writer", 1)] = CreateAgent("writer", 1, """{"provider":"openai","model":"gpt-4.1"}"""),
            [("reviewer", 2)] = CreateAgent("reviewer", 2, """{"provider":"openai","model":"gpt-4.1-mini"}"""),
            [("writer", 9)] = CreateAgent("writer", 9, """{"provider":"openai","model":"gpt-5"}"""),
            [("reviewer", 8)] = CreateAgent("reviewer", 8, """{"provider":"openai","model":"gpt-5"}"""),
        };

        var resolver = CreateResolver(
            workflows,
            agents,
            latestWorkflowVersions: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["child-flow"] = 99,
            },
            latestAgentVersions: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["writer"] = 9,
                ["reviewer"] = 8,
            });

        var package = await resolver.ResolveAsync("root-flow", 1);

        package.Workflows.Select(w => (w.Key, w.Version)).Should().BeEquivalentTo(
        [
            ("root-flow", 1),
            ("child-flow", 4),
        ]);
        package.Agents.Select(a => (a.Key, a.Version)).Should().BeEquivalentTo(
        [
            ("writer", 1),
            ("reviewer", 2),
        ]);
    }

    [Fact]
    public async Task ResolveAsync_expands_role_skill_and_mcp_dependencies_using_stable_identities()
    {
        var workflowId = Guid.NewGuid();
        var role = new AgentRole(
            Id: 41,
            Key: "reviewer",
            DisplayName: "Reviewer",
            Description: "Can review output",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "tester",
            UpdatedAtUtc: DateTime.UtcNow,
            UpdatedBy: "tester",
            IsArchived: false);
        var skill = new Skill(
            Id: 10,
            Name: "triage",
            Body: "Always classify the request first.",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "tester",
            UpdatedAtUtc: DateTime.UtcNow,
            UpdatedBy: "tester",
            IsArchived: false);
        var mcpServer = new McpServer(
            Id: 55,
            Key: "search",
            DisplayName: "Search",
            Transport: McpTransportKind.HttpSse,
            EndpointUrl: "https://search.local/mcp",
            HasBearerToken: true,
            HealthStatus: McpServerHealthStatus.Healthy,
            LastVerifiedAtUtc: new DateTime(2026, 4, 23, 12, 0, 0, DateTimeKind.Utc),
            LastVerificationError: null,
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "tester",
            UpdatedAtUtc: DateTime.UtcNow,
            UpdatedBy: "tester",
            IsArchived: false);

        var resolver = CreateResolver(
            workflows: new Dictionary<(string Key, int Version), Workflow>(StringTupleComparer.Ordinal)
            {
                [("root-flow", 1)] = new(
                    Key: "root-flow",
                    Version: 1,
                    Name: "Root",
                    MaxRoundsPerRound: 3,
                    CreatedAtUtc: DateTime.UtcNow,
                    Nodes:
                    [
                        new WorkflowNode(workflowId, WorkflowNodeKind.Start, "writer", null, null, ["Completed"], 0, 0),
                    ],
                    Edges: [],
                    Inputs: []),
            },
            agents: new Dictionary<(string Key, int Version), AgentConfig>(StringTupleComparer.Ordinal)
            {
                [("writer", 3)] = CreateAgent(
                    "writer",
                    3,
                    """{"provider":"openai","model":"gpt-5","systemPrompt":"Write","outputs":[{"kind":"Completed","payloadExample":{"status":"ok"}}]}""",
                    [new AgentOutputDeclaration("Completed", "Finished", JsonDocument.Parse("""{"status":"ok"}""").RootElement.Clone())]),
            },
            latestWorkflowVersions: new Dictionary<string, int>(StringComparer.Ordinal),
            latestAgentVersions: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["writer"] = 3,
            },
            agentRoleAssignments: new Dictionary<string, IReadOnlyList<AgentRole>>(StringComparer.Ordinal)
            {
                ["writer"] = [role],
            },
            roleToolGrants: new Dictionary<long, IReadOnlyList<AgentRoleToolGrant>>
            {
                [role.Id] =
                [
                    new AgentRoleToolGrant(AgentRoleToolCategory.Host, "read_file"),
                    new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, "mcp:search:query"),
                ],
            },
            roleSkillGrants: new Dictionary<long, IReadOnlyList<long>>
            {
                [role.Id] = [skill.Id],
            },
            skills: new Dictionary<long, Skill>
            {
                [skill.Id] = skill,
            },
            mcpServers: new Dictionary<string, McpServer>(StringComparer.Ordinal)
            {
                [mcpServer.Key] = mcpServer,
            },
            mcpTools: new Dictionary<long, IReadOnlyList<McpServerTool>>
            {
                [mcpServer.Id] =
                [
                    new McpServerTool(1, mcpServer.Id, "query", "Search the index", """{"q":"string"}""", false, DateTime.UtcNow),
                ],
            });

        var package = await resolver.ResolveAsync("root-flow", 1);

        package.AgentRoleAssignments.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new WorkflowPackageAgentRoleAssignment("writer", ["reviewer"]));

        package.Roles.Should().ContainSingle()
            .Which.SkillNames.Should().Equal("triage");

        package.McpServers.Should().ContainSingle();
        package.McpServers.Single().Key.Should().Be("search");
        package.McpServers.Single().HasBearerToken.Should().BeTrue();
        package.McpServers.Single().Tools.Should().ContainSingle(t => t.ToolName == "query");

        var serialized = JsonSerializer.Serialize(package);
        serialized.Should().NotContain("super-secret-token");
        serialized.Should().Contain("search.local/mcp");
    }

    private static WorkflowPackageResolver CreateResolver(
        IReadOnlyDictionary<(string Key, int Version), Workflow> workflows,
        IReadOnlyDictionary<(string Key, int Version), AgentConfig> agents,
        IReadOnlyDictionary<string, int> latestWorkflowVersions,
        IReadOnlyDictionary<string, int> latestAgentVersions,
        IReadOnlyDictionary<string, IReadOnlyList<AgentRole>>? agentRoleAssignments = null,
        IReadOnlyDictionary<long, IReadOnlyList<AgentRoleToolGrant>>? roleToolGrants = null,
        IReadOnlyDictionary<long, IReadOnlyList<long>>? roleSkillGrants = null,
        IReadOnlyDictionary<long, Skill>? skills = null,
        IReadOnlyDictionary<string, McpServer>? mcpServers = null,
        IReadOnlyDictionary<long, IReadOnlyList<McpServerTool>>? mcpTools = null)
    {
        return new WorkflowPackageResolver(
            new FakeWorkflowRepository(workflows, latestWorkflowVersions),
            new FakeAgentConfigRepository(agents, latestAgentVersions),
            new FakeAgentRoleRepository(
                agentRoleAssignments ?? new Dictionary<string, IReadOnlyList<AgentRole>>(StringComparer.Ordinal),
                roleToolGrants ?? new Dictionary<long, IReadOnlyList<AgentRoleToolGrant>>(),
                roleSkillGrants ?? new Dictionary<long, IReadOnlyList<long>>()),
            new FakeSkillRepository(skills ?? new Dictionary<long, Skill>()),
            new FakeMcpServerRepository(
                mcpServers ?? new Dictionary<string, McpServer>(StringComparer.Ordinal),
                mcpTools ?? new Dictionary<long, IReadOnlyList<McpServerTool>>()));
    }

    private static AgentConfig CreateAgent(
        string key,
        int version,
        string configJson,
        IReadOnlyList<AgentOutputDeclaration>? outputs = null)
    {
        return new AgentConfig(
            Key: key,
            Version: version,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5"),
            ConfigJson: configJson,
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "tester",
            Outputs: outputs);
    }

    private sealed class FakeWorkflowRepository(
        IReadOnlyDictionary<(string Key, int Version), Workflow> workflows,
        IReadOnlyDictionary<string, int> latestVersions) : IWorkflowRepository
    {
        public Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult(workflows[(key, version)]);

        public Task<Workflow?> GetLatestAsync(string key, CancellationToken cancellationToken = default)
        {
            if (!latestVersions.TryGetValue(key, out var version))
            {
                return Task.FromResult<Workflow?>(null);
            }

            return Task.FromResult<Workflow?>(workflows[(key, version)]);
        }

        public Task<IReadOnlyList<Workflow>> ListLatestAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Workflow>> ListVersionsAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkflowEdge?> FindNextAsync(string key, int version, Guid fromNodeId, string outputPortName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> GetTerminalPortsAsync(string key, int version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CreateNewVersionAsync(WorkflowDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAgentConfigRepository(
        IReadOnlyDictionary<(string Key, int Version), AgentConfig> agents,
        IReadOnlyDictionary<string, int> latestVersions) : IAgentConfigRepository
    {
        public Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult(agents[(key, version)]);

        public Task<int> CreateNewVersionAsync(string key, string configJson, string? createdBy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(latestVersions[key]);

        public Task<bool> RetireAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentConfig> CreateForkAsync(
            string sourceKey,
            int sourceVersion,
            string workflowKey,
            string configJson,
            string? createdBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CreatePublishedVersionAsync(
            string targetKey,
            string configJson,
            string forkedFromKey,
            int forkedFromVersion,
            string? createdBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAgentRoleRepository(
        IReadOnlyDictionary<string, IReadOnlyList<AgentRole>> assignments,
        IReadOnlyDictionary<long, IReadOnlyList<AgentRoleToolGrant>> roleToolGrants,
        IReadOnlyDictionary<long, IReadOnlyList<long>> roleSkillGrants) : IAgentRoleRepository
    {
        public Task<IReadOnlyList<AgentRole>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentRole?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentRole?> GetByKeyAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> CreateAsync(AgentRoleCreate create, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(long id, AgentRoleUpdate update, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ArchiveAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AgentRoleToolGrant>> GetGrantsAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(roleToolGrants.TryGetValue(id, out var grants)
                ? grants
                : (IReadOnlyList<AgentRoleToolGrant>)Array.Empty<AgentRoleToolGrant>());

        public Task ReplaceGrantsAsync(long id, IReadOnlyList<AgentRoleToolGrant> grants, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AgentRole>> GetRolesForAgentAsync(string agentKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(assignments.TryGetValue(agentKey, out var roles)
                ? roles
                : (IReadOnlyList<AgentRole>)Array.Empty<AgentRole>());

        public Task ReplaceAssignmentsAsync(string agentKey, IReadOnlyList<long> roleIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetSkillGrantsAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(roleSkillGrants.TryGetValue(id, out var skillIds)
                ? skillIds
                : (IReadOnlyList<long>)Array.Empty<long>());

        public Task ReplaceSkillGrantsAsync(long id, IReadOnlyList<long> skillIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSkillRepository(IReadOnlyDictionary<long, Skill> skills) : ISkillRepository
    {
        public Task<IReadOnlyList<Skill>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Skill?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(skills.TryGetValue(id, out var skill) ? skill : null);

        public Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> CreateAsync(SkillCreate create, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(long id, SkillUpdate update, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ArchiveAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMcpServerRepository(
        IReadOnlyDictionary<string, McpServer> serversByKey,
        IReadOnlyDictionary<long, IReadOnlyList<McpServerTool>> toolsByServerId) : IMcpServerRepository
    {
        public Task<IReadOnlyList<McpServer>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<McpServer?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(serversByKey.Values.SingleOrDefault(server => server.Id == id));

        public Task<McpServer?> GetByKeyAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(serversByKey.TryGetValue(key, out var server) ? server : null);

        public Task<long> CreateAsync(McpServerCreate create, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(long id, McpServerUpdate update, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ArchiveAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateHealthAsync(long id, McpServerHealthStatus status, DateTime? lastVerifiedAtUtc, string? lastError, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReplaceToolsAsync(long id, IReadOnlyList<McpServerToolWrite> tools, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<McpServerTool>> GetToolsAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(toolsByServerId.TryGetValue(id, out var tools)
                ? tools
                : (IReadOnlyList<McpServerTool>)Array.Empty<McpServerTool>());

        public Task<McpServerConnectionInfo?> GetConnectionInfoAsync(string serverKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Key, int Version)>
    {
        public static StringTupleComparer Ordinal { get; } = new();

        public bool Equals((string Key, int Version) x, (string Key, int Version) y) =>
            StringComparer.Ordinal.Equals(x.Key, y.Key) && x.Version == y.Version;

        public int GetHashCode((string Key, int Version) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Key), obj.Version);
    }
}
