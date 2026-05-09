using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Api.WorkflowPackages.Admission;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Authority.Admission;
using FluentAssertions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Api.Tests.WorkflowPackages;

/// <summary>
/// AP-9 (sc-840): structural lints for first-party agent-package examples shipped in
/// `workflows/agents/`. Each library file must:
/// <list type="bullet">
///   <item>Deserialize cleanly into <see cref="AgentPackage"/> via the same JSON options
///     the API's import path uses (web defaults + string-enum converter).</item>
///   <item>Pass <see cref="AgentPackageImportValidator"/> admission — schema string match,
///     entry-point in <c>agents[]</c>.</item>
///   <item>Be self-contained — every <c>Mcp</c> tool grant references a server embedded in
///     <c>mcpServers[]</c>; every skill name listed on a role appears in <c>skills[]</c>.</item>
/// </list>
/// </summary>
public sealed class LibraryAgentPackageExamplesTests
{
    private static readonly JsonSerializerOptions DeserializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData("code-reviewer-v1-agent-package.json")]
    public void Library_AgentPackage_Deserializes_AdmissionPasses_AndIsSelfContained(string fileName)
    {
        var path = LocateLibraryAgentPackage(fileName);
        var json = File.ReadAllText(path);

        var package = JsonSerializer.Deserialize<AgentPackage>(json, DeserializerOptions);
        package.Should().NotBeNull(because: $"{fileName} must round-trip through AgentPackage");

        package!.SchemaVersion.Should().Be(AgentPackageDefaults.SchemaVersion);

        // Convert to the internal `WorkflowPackage` shape that the AgentPackageImportValidator
        // operates on (mirrors AgentPackageImporter.SynthesizeWorkflowPackage). Validates that
        // the file would clear admission on the live import path.
        var synthetic = new WorkflowPackage(
            SchemaVersion: package.SchemaVersion,
            Metadata: package.Metadata,
            EntryPoint: package.EntryPoint,
            Workflows: Array.Empty<WorkflowPackageWorkflow>(),
            Agents: package.Agents,
            AgentRoleAssignments: package.AgentRoleAssignments,
            Roles: package.Roles,
            Skills: package.Skills,
            McpServers: package.McpServers);
        new AgentPackageImportValidator()
            .Validate(synthetic)
            .Should().BeOfType<Accepted<AdmittedPackageImport>>(
                because: $"{fileName} must pass admission with no rejection axes");

        // Self-containment: every Mcp grant references a server in mcpServers[].
        var embeddedServers = package.McpServers.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var role in package.Roles)
        {
            foreach (var grant in role.ToolGrants.Where(g => g.Category == AgentRoleToolCategory.Mcp))
            {
                var serverKey = grant.ToolIdentifier.Split(':')[1];
                embeddedServers.Should().Contain(serverKey,
                    because: $"role '{role.Key}' grants `{grant.ToolIdentifier}` so server '{serverKey}' must be embedded in {fileName}");
            }
        }

        // Self-containment: every skill name listed on a role appears in skills[].
        var embeddedSkills = package.Skills.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var role in package.Roles)
        {
            foreach (var skillName in role.SkillNames)
            {
                embeddedSkills.Should().Contain(skillName,
                    because: $"role '{role.Key}' lists skill '{skillName}' so it must be embedded in {fileName}");
            }
        }

        // Self-containment: every role assignment lists role keys that appear in roles[].
        var embeddedRoles = package.Roles.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var assignment in package.AgentRoleAssignments)
        {
            foreach (var roleKey in assignment.RoleKeys)
            {
                embeddedRoles.Should().Contain(roleKey,
                    because: $"agent '{assignment.AgentKey}' is assigned role '{roleKey}' which must be embedded in {fileName}");
            }
        }

        // Manifest matches entry-point.
        package.Manifest.Should().NotBeNull();
        package.Manifest!.Agent.Key.Should().Be(package.EntryPoint.Key);
        package.Manifest.Agent.Version.Should().Be(package.EntryPoint.Version);
    }

    /// <summary>
    /// Walk up from the test bin directory to find the repo root + the library file. Mirrors
    /// the workflow-side <c>LocateLibraryPackage</c> in <c>WorkflowsEndpointsTests</c>.
    /// </summary>
    private static string LocateLibraryAgentPackage(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "workflows", "agents", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            $"Could not locate workflows/agents/{fileName} by walking up from {AppContext.BaseDirectory}.");
    }
}
