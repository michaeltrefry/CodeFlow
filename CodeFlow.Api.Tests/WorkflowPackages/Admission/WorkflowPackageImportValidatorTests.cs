using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Api.WorkflowPackages.Admission;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Authority.Admission;
using FluentAssertions;

namespace CodeFlow.Api.Tests.WorkflowPackages.Admission;

/// <summary>
/// sc-272 PR2 — exercises <see cref="WorkflowPackageImportValidator"/> on its own.
/// The validator-then-execute flow inside <see cref="WorkflowPackageImporter"/> is covered
/// by the existing <c>WorkflowPackageImporterTests</c>; this file covers each rejection
/// axis + the minted <see cref="AdmittedPackageImport"/> contract directly.
/// </summary>
public sealed class WorkflowPackageImportValidatorTests
{
    [Fact]
    public void Validate_HappyPath_AdmitsWithSourcePackage()
    {
        var validator = new WorkflowPackageImportValidator();
        var package = NewPackage();

        var admission = validator.Validate(package);

        var admitted = admission.Should().BeOfType<Accepted<AdmittedPackageImport>>().Subject.Value;
        admitted.Package.Should().BeSameAs(package);
    }

    [Fact]
    public void Validate_SchemaVersionMismatch_RejectsWithSchemaCode()
    {
        var validator = new WorkflowPackageImportValidator();
        var package = NewPackage() with { SchemaVersion = "codeflow.workflow-package.v999" };

        var admission = validator.Validate(package);

        var rejected = admission.Should().BeOfType<Rejected<AdmittedPackageImport>>().Subject;
        rejected.Reason.Code.Should().Be("package-schema-unsupported");
        rejected.Reason.Reason.Should().Contain("v999");
    }

    [Fact]
    public void Validate_EntryPointMissingFromWorkflows_Rejects()
    {
        var validator = new WorkflowPackageImportValidator();
        var package = NewPackage() with
        {
            EntryPoint = new WorkflowPackageReference("missing", 1),
        };

        var admission = validator.Validate(package);

        admission.Should().BeOfType<Rejected<AdmittedPackageImport>>()
            .Which.Reason.Code.Should().Be("package-entry-point-missing");
    }

    [Fact]
    public void Validate_AgentNodeWithoutVersion_Rejects()
    {
        var validator = new WorkflowPackageImportValidator();
        var workflow = NewWorkflow() with
        {
            Nodes = new[]
            {
                new WorkflowPackageWorkflowNode(
                    Id: Guid.NewGuid(),
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: "agentX",
                    AgentVersion: null,
                    OutputScript: null,
                    OutputPorts: Array.Empty<string>(),
                    LayoutX: 0,
                    LayoutY: 0),
            },
        };
        var package = NewPackage() with { Workflows = new[] { workflow } };

        var admission = validator.Validate(package);

        admission.Should().BeOfType<Rejected<AdmittedPackageImport>>()
            .Which.Reason.Code.Should().Be("package-node-missing-agent-version");
    }

    [Fact]
    public void Validate_AgentReferenceMissingFromPackage_Rejects()
    {
        var validator = new WorkflowPackageImportValidator();
        var workflow = NewWorkflow() with
        {
            Nodes = new[]
            {
                new WorkflowPackageWorkflowNode(
                    Id: Guid.NewGuid(),
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: "missingAgent",
                    AgentVersion: 5,
                    OutputScript: null,
                    OutputPorts: Array.Empty<string>(),
                    LayoutX: 0,
                    LayoutY: 0),
            },
        };
        var package = NewPackage() with { Workflows = new[] { workflow } };

        var admission = validator.Validate(package);

        var rejected = admission.Should().BeOfType<Rejected<AdmittedPackageImport>>().Subject;
        rejected.Reason.Code.Should().Be("package-agent-missing");
        rejected.Reason.Path.Should().Be("missingAgent@5");
    }

    [Fact]
    public void Validate_SubflowReferenceMissingFromPackage_Rejects()
    {
        var validator = new WorkflowPackageImportValidator();
        var workflow = NewWorkflow() with
        {
            Nodes = new[]
            {
                new WorkflowPackageWorkflowNode(
                    Id: Guid.NewGuid(),
                    Kind: WorkflowNodeKind.Subflow,
                    AgentKey: null,
                    AgentVersion: null,
                    OutputScript: null,
                    OutputPorts: Array.Empty<string>(),
                    LayoutX: 0,
                    LayoutY: 0,
                    SubflowKey: "missingSubflow",
                    SubflowVersion: 1),
            },
        };
        var package = NewPackage() with { Workflows = new[] { workflow } };

        var admission = validator.Validate(package);

        admission.Should().BeOfType<Rejected<AdmittedPackageImport>>()
            .Which.Reason.Code.Should().Be("package-subflow-missing");
    }

    [Fact]
    public void Validate_SubflowNodeWithoutVersion_Rejects()
    {
        var validator = new WorkflowPackageImportValidator();
        var workflow = NewWorkflow() with
        {
            Nodes = new[]
            {
                new WorkflowPackageWorkflowNode(
                    Id: Guid.NewGuid(),
                    Kind: WorkflowNodeKind.Subflow,
                    AgentKey: null,
                    AgentVersion: null,
                    OutputScript: null,
                    OutputPorts: Array.Empty<string>(),
                    LayoutX: 0,
                    LayoutY: 0,
                    SubflowKey: "child",
                    SubflowVersion: null),
            },
        };
        var package = NewPackage() with { Workflows = new[] { workflow } };

        var admission = validator.Validate(package);

        admission.Should().BeOfType<Rejected<AdmittedPackageImport>>()
            .Which.Reason.Code.Should().Be("package-node-missing-subflow-version");
    }

    [Fact]
    public void Validate_ReMint_SecondCallWithSameInputProducesEquivalentAdmittedValue()
    {
        var fixedNow = DateTimeOffset.Parse("2026-04-30T15:00:00Z");
        var validator = new WorkflowPackageImportValidator(nowProvider: () => fixedNow);
        var package = NewPackage();

        var first = validator.Validate(package);
        var second = validator.Validate(package);

        var firstAdmitted = first.Should().BeOfType<Accepted<AdmittedPackageImport>>().Subject.Value;
        var secondAdmitted = second.Should().BeOfType<Accepted<AdmittedPackageImport>>().Subject.Value;
        secondAdmitted.Package.Should().BeSameAs(firstAdmitted.Package);
        secondAdmitted.AdmittedAt.Should().Be(firstAdmitted.AdmittedAt);
    }

    private static WorkflowPackageWorkflow NewWorkflow() =>
        new(
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

    private static WorkflowPackage NewPackage()
    {
        var workflow = NewWorkflow();
        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(workflow.Key, workflow.Version),
            Workflows: new[] { workflow },
            Agents: Array.Empty<WorkflowPackageAgent>(),
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }
}
