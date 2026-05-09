using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Api.WorkflowPackages.Admission;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Authority.Admission;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Tests.WorkflowPackages.Admission;

/// <summary>
/// AP-2 (sc-833) — exercises <see cref="AgentPackageImportValidator"/> on its own. The
/// validator-then-execute flow inside <see cref="AgentPackageImporter"/> is covered by the
/// integration tests in <c>AgentsEndpointsTests</c>; this file covers each rejection axis +
/// the minted <see cref="AdmittedPackageImport"/> contract directly.
/// </summary>
public sealed class AgentPackageImportValidatorTests
{
    [Fact]
    public void Validate_HappyPath_AdmitsWithSourcePackage()
    {
        var validator = new AgentPackageImportValidator();
        var package = SyntheticAgentWorkflowPackage();

        var admission = validator.Validate(package);

        var admitted = admission.Should().BeOfType<Accepted<AdmittedPackageImport>>().Subject.Value;
        admitted.Package.Should().BeSameAs(package);
    }

    [Fact]
    public void Validate_SchemaVersionMismatch_RejectsWithSchemaCode()
    {
        var validator = new AgentPackageImportValidator();
        var package = SyntheticAgentWorkflowPackage() with { SchemaVersion = "codeflow.workflow-package.v1" };

        var admission = validator.Validate(package);

        var rejected = admission.Should().BeOfType<Rejected<AdmittedPackageImport>>().Subject;
        rejected.Reason.Code.Should().Be("package-schema-unsupported");
        rejected.Reason.Reason.Should().Contain("workflow-package.v1");
    }

    [Fact]
    public void Validate_EntryPointMissingFromAgents_Rejects()
    {
        var validator = new AgentPackageImportValidator();
        var package = SyntheticAgentWorkflowPackage() with
        {
            EntryPoint = new WorkflowPackageReference("missing-agent", 1),
        };

        var admission = validator.Validate(package);

        var rejected = admission.Should().BeOfType<Rejected<AdmittedPackageImport>>().Subject;
        rejected.Reason.Code.Should().Be("package-entry-point-missing");
        rejected.Reason.Reason.Should().Contain("missing-agent");
    }

    [Fact]
    public void Validate_EmptyAgents_Rejects()
    {
        var validator = new AgentPackageImportValidator();
        var package = SyntheticAgentWorkflowPackage() with
        {
            Agents = Array.Empty<WorkflowPackageAgent>(),
        };

        var admission = validator.Validate(package);

        admission.Should().BeOfType<Rejected<AdmittedPackageImport>>()
            .Which.Reason.Code.Should().Be("package-entry-point-missing");
    }

    [Fact]
    public void Validate_DoesNotRequireWorkflows()
    {
        // Distinguishes agent admission from workflow admission — the agent package
        // contract has zero workflow rows.
        var validator = new AgentPackageImportValidator();
        var package = SyntheticAgentWorkflowPackage();
        package.Workflows.Should().BeEmpty();

        validator.Validate(package).Should().BeOfType<Accepted<AdmittedPackageImport>>();
    }

    /// <summary>
    /// Returns a <see cref="WorkflowPackage"/> shaped exactly like
    /// <see cref="AgentPackageImporter"/> synthesizes from an <see cref="AgentPackage"/>:
    /// agent-package schema string, no workflows, one entry-point agent.
    /// </summary>
    private static WorkflowPackage SyntheticAgentWorkflowPackage()
    {
        var agent = new WorkflowPackageAgent(
            Key: "writer",
            Version: 3,
            Kind: AgentKind.Agent,
            Config: JsonNode.Parse("""{"provider":"openai","model":"gpt-5"}"""),
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: "tester",
            Outputs: Array.Empty<WorkflowPackageAgentOutput>(),
            Tags: Array.Empty<string>());

        return new WorkflowPackage(
            SchemaVersion: AgentPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(agent.Key, agent.Version),
            Workflows: Array.Empty<WorkflowPackageWorkflow>(),
            Agents: new[] { agent },
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }
}
