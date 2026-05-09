using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using System.Text.Json;

namespace CodeFlow.Api.Tests.WorkflowPackages;

public sealed class AgentPackageResolverTests
{
    [Fact]
    public async Task ResolveAsync_LoadsEntryPointAgent_AndExpandsRoleSkillMcpClosure()
    {
        var role = new AgentRole(
            Id: 41,
            Key: "reviewer",
            DisplayName: "Reviewer",
            Description: "Can review output",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "tester",
            UpdatedAtUtc: DateTime.UtcNow,
            UpdatedBy: "tester",
            IsArchived: false,
            Tags: ["review", "governance"]);
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
            LastVerifiedAtUtc: new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc),
            LastVerificationError: null,
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "tester",
            UpdatedAtUtc: DateTime.UtcNow,
            UpdatedBy: "tester",
            IsArchived: false);

        var resolver = CreateResolver(
            agents: new Dictionary<(string Key, int Version), AgentConfig>(StringTupleComparer.Ordinal)
            {
                [("writer", 3)] = CreateAgent(
                    "writer",
                    3,
                    """{"provider":"openai","model":"gpt-5","systemPrompt":"Write","outputs":[{"kind":"Completed","payloadExample":{"status":"ok"}}]}""",
                    [new AgentOutputDeclaration("Completed", "Finished", JsonDocument.Parse("""{"status":"ok"}""").RootElement.Clone())],
                    ["author", "ops"]),
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

        var package = await resolver.ResolveAsync("writer", 3);

        package.SchemaVersion.Should().Be(AgentPackageDefaults.SchemaVersion);
        package.EntryPoint.Should().BeEquivalentTo(new WorkflowPackageReference("writer", 3));

        package.Agents.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Key = "writer",
                Version = 3,
                Tags = new[] { "author", "ops" },
            });

        package.AgentRoleAssignments.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new WorkflowPackageAgentRoleAssignment("writer", ["reviewer"]));

        package.Roles.Should().ContainSingle()
            .Which.SkillNames.Should().Equal("triage");
        package.Roles.Single().Tags.Should().Equal("review", "governance");

        package.Skills.Should().ContainSingle().Which.Name.Should().Be("triage");

        package.McpServers.Should().ContainSingle();
        package.McpServers.Single().Key.Should().Be("search");
        package.McpServers.Single().HasBearerToken.Should().BeTrue();
        package.McpServers.Single().Tools.Should().ContainSingle(t => t.ToolName == "query");

        var serialized = JsonSerializer.Serialize(package);
        serialized.Should().Contain("search.local/mcp");
        serialized.Should().Contain("codeflow.agent-package.v1");
    }

    [Fact]
    public async Task ResolveAsync_MissingEntryPointAgent_PropagatesAgentConfigNotFound()
    {
        // The entry point is a 404, not a self-containment failure — let
        // AgentConfigNotFoundException propagate so the endpoint can map it cleanly.
        var resolver = CreateResolver(
            agents: new Dictionary<(string Key, int Version), AgentConfig>(StringTupleComparer.Ordinal));

        var act = async () => await resolver.ResolveAsync("missing-agent", 7);

        await act.Should().ThrowAsync<AgentConfigNotFoundException>();
    }

    [Fact]
    public async Task ResolveAsync_AccumulatesEveryMissingReference_AndThrowsOnce()
    {
        var role = new AgentRole(
            Id: 91,
            Key: "ops",
            DisplayName: "Ops",
            Description: null,
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: null,
            UpdatedAtUtc: DateTime.UtcNow,
            UpdatedBy: null,
            IsArchived: false,
            Tags: []);

        var resolver = CreateResolver(
            agents: new Dictionary<(string Key, int Version), AgentConfig>(StringTupleComparer.Ordinal)
            {
                [("writer", 3)] = CreateAgent("writer", 3, """{"provider":"openai","model":"gpt-5"}"""),
            },
            agentRoleAssignments: new Dictionary<string, IReadOnlyList<AgentRole>>(StringComparer.Ordinal)
            {
                ["writer"] = [role],
            },
            roleToolGrants: new Dictionary<long, IReadOnlyList<AgentRoleToolGrant>>
            {
                // Reference an MCP server that the fake repo doesn't know about.
                [role.Id] =
                [
                    new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, "mcp:ghost-server:do-thing"),
                ],
            },
            roleSkillGrants: new Dictionary<long, IReadOnlyList<long>>
            {
                // Reference a skill id whose row was deleted.
                [role.Id] = [777L],
            });

        var act = async () => await resolver.ResolveAsync("writer", 3);

        var exception = (await act.Should().ThrowAsync<WorkflowPackageResolutionException>()).Which;
        exception.MissingReferences.Should().HaveCount(2);
        exception.MissingReferences.Should().Contain(r =>
            r.Kind == PackageReferenceKind.Skill && r.Key == "777");
        exception.MissingReferences.Should().Contain(r =>
            r.Kind == PackageReferenceKind.McpServer && r.Key == "ghost-server");
    }

    [Fact]
    public async Task ResolveAsync_ProducesManifest_EnumeratingEveryIncludedEntity()
    {
        var role = new AgentRole(
            Id: 7,
            Key: "reviewer",
            DisplayName: "Reviewer",
            Description: null,
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: null,
            UpdatedAtUtc: DateTime.UtcNow,
            UpdatedBy: null,
            IsArchived: false,
            Tags: []);
        var skill = new Skill(
            Id: 11,
            Name: "redact",
            Body: "Redact PII before sharing.",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: null,
            UpdatedAtUtc: DateTime.UtcNow,
            UpdatedBy: null,
            IsArchived: false);
        var mcpServer = new McpServer(
            Id: 22,
            Key: "vault",
            DisplayName: "Vault",
            Transport: McpTransportKind.HttpSse,
            EndpointUrl: "https://vault.local/mcp",
            HasBearerToken: false,
            HealthStatus: McpServerHealthStatus.Healthy,
            LastVerifiedAtUtc: null,
            LastVerificationError: null,
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: null,
            UpdatedAtUtc: DateTime.UtcNow,
            UpdatedBy: null,
            IsArchived: false);

        var resolver = CreateResolver(
            agents: new Dictionary<(string Key, int Version), AgentConfig>(StringTupleComparer.Ordinal)
            {
                [("writer", 4)] = CreateAgent("writer", 4, """{"provider":"openai","model":"gpt-5"}"""),
            },
            agentRoleAssignments: new Dictionary<string, IReadOnlyList<AgentRole>>(StringComparer.Ordinal)
            {
                ["writer"] = [role],
            },
            roleToolGrants: new Dictionary<long, IReadOnlyList<AgentRoleToolGrant>>
            {
                [role.Id] =
                [
                    new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, "mcp:vault:read"),
                ],
            },
            roleSkillGrants: new Dictionary<long, IReadOnlyList<long>>
            {
                [role.Id] = [skill.Id],
            },
            skills: new Dictionary<long, Skill> { [skill.Id] = skill },
            mcpServers: new Dictionary<string, McpServer>(StringComparer.Ordinal)
            {
                [mcpServer.Key] = mcpServer,
            },
            mcpTools: new Dictionary<long, IReadOnlyList<McpServerTool>>
            {
                [mcpServer.Id] = [],
            });

        var package = await resolver.ResolveAsync("writer", 4);

        package.Manifest.Should().NotBeNull();
        package.Manifest!.Agent.Should().BeEquivalentTo(new WorkflowPackageReference("writer", 4));
        package.Manifest.Roles.Should().Equal("reviewer");
        package.Manifest.Skills.Should().Equal("redact");
        package.Manifest.McpServers.Should().Equal("vault");
    }

    [Fact]
    public async Task ResolveAsync_AgentWithoutRoles_ProducesPackageWithEmptyDependencyClosure()
    {
        var resolver = CreateResolver(
            agents: new Dictionary<(string Key, int Version), AgentConfig>(StringTupleComparer.Ordinal)
            {
                [("solo", 1)] = CreateAgent("solo", 1, """{"provider":"openai","model":"gpt-5"}"""),
            });

        var package = await resolver.ResolveAsync("solo", 1);

        package.Agents.Should().ContainSingle();
        package.Roles.Should().BeEmpty();
        package.Skills.Should().BeEmpty();
        package.McpServers.Should().BeEmpty();
        package.AgentRoleAssignments.Should().ContainSingle();
        package.AgentRoleAssignments.Single().RoleKeys.Should().BeEmpty();
        package.Manifest!.Roles.Should().BeEmpty();
        package.Manifest.Skills.Should().BeEmpty();
        package.Manifest.McpServers.Should().BeEmpty();
    }

    private static AgentPackageResolver CreateResolver(
        IReadOnlyDictionary<(string Key, int Version), AgentConfig> agents,
        IReadOnlyDictionary<string, IReadOnlyList<AgentRole>>? agentRoleAssignments = null,
        IReadOnlyDictionary<long, IReadOnlyList<AgentRoleToolGrant>>? roleToolGrants = null,
        IReadOnlyDictionary<long, IReadOnlyList<long>>? roleSkillGrants = null,
        IReadOnlyDictionary<long, Skill>? skills = null,
        IReadOnlyDictionary<string, McpServer>? mcpServers = null,
        IReadOnlyDictionary<long, IReadOnlyList<McpServerTool>>? mcpTools = null)
    {
        return new AgentPackageResolver(
            new FakeAgentConfigRepository(agents),
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
        IReadOnlyList<AgentOutputDeclaration>? outputs = null,
        IReadOnlyList<string>? tags = null)
    {
        return new AgentConfig(
            Key: key,
            Version: version,
            Kind: AgentKind.Agent,
            Configuration: new AgentInvocationConfiguration("openai", "gpt-5"),
            ConfigJson: configJson,
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "tester",
            Outputs: outputs,
            Tags: tags);
    }

    private sealed class FakeAgentConfigRepository(
        IReadOnlyDictionary<(string Key, int Version), AgentConfig> agents) : IAgentConfigRepository
    {
        public Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default)
        {
            if (!agents.TryGetValue((key, version), out var agent))
            {
                throw new AgentConfigNotFoundException(key, version);
            }
            return Task.FromResult(agent);
        }

        public Task<int> CreateNewVersionAsync(string key, string configJson, string? createdBy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

        public Task<IReadOnlyList<AgentRole>> GetRolesForAgentAsync(string agentKey, int agentVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(assignments.TryGetValue(agentKey, out var roles)
                ? roles
                : (IReadOnlyList<AgentRole>)Array.Empty<AgentRole>());

        public Task<IReadOnlyList<AgentRole>> GetRolesForAgentLatestAsync(string agentKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(assignments.TryGetValue(agentKey, out var roles)
                ? roles
                : (IReadOnlyList<AgentRole>)Array.Empty<AgentRole>());

        public Task ReplaceAssignmentsAsync(string agentKey, int agentVersion, IReadOnlyList<long> roleIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReplaceAssignmentsForLatestAsync(string agentKey, IReadOnlyList<long> roleIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> BumpAgentForRoleAssignmentChangeAsync(string agentKey, IReadOnlyList<long> roleIds, int? expectedFromVersion, string? createdBy, CancellationToken cancellationToken = default) =>
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
