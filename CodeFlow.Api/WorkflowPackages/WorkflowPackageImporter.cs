using CodeFlow.Api.Dtos;
using CodeFlow.Api.Mcp;
using CodeFlow.Api.Validation;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.WorkflowPackages.Admission;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Authority.Admission;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.WorkflowPackages;

public sealed class WorkflowPackageImporter(
    CodeFlowDbContext dbContext,
    IWorkflowRepository workflowRepository,
    IAgentConfigRepository agentConfigRepository,
    IAgentRoleRepository agentRoleRepository,
    ISkillRepository skillRepository,
    IMcpServerRepository mcpServerRepository,
    IMcpEndpointPolicy mcpEndpointPolicy,
    WorkflowValidationPipeline? validationPipeline = null,
    IAuthoringTelemetry? telemetry = null,
    WorkflowPackageImportValidator? admissionValidator = null) : IWorkflowPackageImporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly WorkflowPackageImportValidator admissionValidator = admissionValidator ?? new WorkflowPackageImportValidator();

    public async Task<WorkflowPackageImportPreview> PreviewAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var plan = await BuildImportPlanAsync(NormalizeNulls(package), cancellationToken);
        return plan.Preview;
    }

    public async Task<WorkflowPackageImportApplyResult> ApplyAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var plan = await BuildImportPlanAsync(NormalizeNulls(package), cancellationToken);
        var preview = plan.Preview;
        if (!preview.CanApply)
        {
            throw new WorkflowPackageResolutionException(
                "Workflow package import has conflicts. Preview the package and resolve conflicts before applying it.");
        }

        var importPackage = plan.Package;
        var validationErrors = new List<WorkflowPackageValidationError>();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            validationErrors.Clear();
            await RunImportInTransactionAsync(importPackage, commit: true, validationErrors, cancellationToken);
        });

        if (validationErrors.Count > 0)
        {
            // RunImportInTransactionAsync rolled back when commit=true encounters validation
            // errors; surface as the same exception type the endpoint already maps to a 400.
            throw new WorkflowPackageResolutionException(FormatValidationFailure(validationErrors));
        }

        return new WorkflowPackageImportApplyResult(preview.EntryPoint, preview.Items, preview.Warnings);
    }

    public async Task<WorkflowPackageValidationResult> ValidateAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var plan = await BuildImportPlanAsync(NormalizeNulls(package), cancellationToken);
        if (!plan.Preview.CanApply)
        {
            // Conflict-only packages: caller (PreviewAsync) already reports the conflicts;
            // validation is moot until the conflicts are resolved.
            return WorkflowPackageValidationResult.Valid;
        }

        var importPackage = plan.Package;
        var errors = new List<WorkflowPackageValidationError>();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            errors.Clear();
            await RunImportInTransactionAsync(importPackage, commit: false, errors, cancellationToken);
        });

        return errors.Count == 0
            ? WorkflowPackageValidationResult.Valid
            : new WorkflowPackageValidationResult(false, errors);
    }

    private async Task RunImportInTransactionAsync(
        WorkflowPackage importPackage,
        bool commit,
        List<WorkflowPackageValidationError> validationErrors,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var now = DateTime.UtcNow;

        await ImportSkillsAsync(importPackage, now, cancellationToken);
        await ImportMcpServersAsync(importPackage, now, cancellationToken);
        await ImportRolesAsync(importPackage, now, cancellationToken);
        await ImportAgentsAsync(importPackage, now, cancellationToken);
        await ImportRoleAssignmentsAsync(importPackage, now, cancellationToken);
        await ImportWorkflowsAsync(importPackage, now, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await CollectValidationErrorsAsync(importPackage, validationErrors, cancellationToken);

        if (commit && validationErrors.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private async Task CollectValidationErrorsAsync(
        WorkflowPackage package,
        List<WorkflowPackageValidationError> errors,
        CancellationToken cancellationToken)
    {
        foreach (var workflow in package.Workflows)
        {
            var nodes = workflow.Nodes.Select(ToDto).ToArray();
            var edges = workflow.Edges.Select(ToDto).ToArray();
            var inputs = workflow.Inputs.Select(ToDto).ToArray();

            var legacyValidation = await WorkflowValidator.ValidateAsync(
                workflow.Key,
                workflow.Name,
                workflow.MaxRoundsPerRound,
                nodes,
                edges,
                inputs,
                dbContext,
                workflowRepository,
                agentConfigRepository,
                cancellationToken);

            if (!legacyValidation.IsValid)
            {
                telemetry?.ValidatorBlockedSave(workflow.Key, new[] { "workflow-validator" });
                errors.Add(new WorkflowPackageValidationError(
                    workflow.Key,
                    legacyValidation.Error!,
                    new[] { "workflow-validator" }));
                continue;
            }

            if (validationPipeline is null)
            {
                continue;
            }

            var context = new WorkflowValidationContext(
                Key: workflow.Key,
                Name: workflow.Name,
                MaxRoundsPerRound: workflow.MaxRoundsPerRound,
                Nodes: nodes,
                Edges: edges,
                Inputs: inputs,
                DbContext: dbContext,
                WorkflowRepository: workflowRepository,
                AgentRepository: agentConfigRepository,
                AgentRoleRepository: agentRoleRepository,
                WorkflowVarsReads: workflow.WorkflowVarsReads,
                WorkflowVarsWrites: workflow.WorkflowVarsWrites);

            var report = await validationPipeline.RunAsync(context, cancellationToken);
            if (!report.HasErrors)
            {
                continue;
            }

            var pipelineErrors = report.Findings
                .Where(f => f.Severity == WorkflowValidationSeverity.Error)
                .ToArray();
            var ruleIds = pipelineErrors
                .Select(f => f.RuleId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            telemetry?.ValidatorBlockedSave(workflow.Key, ruleIds);

            errors.Add(new WorkflowPackageValidationError(
                workflow.Key,
                string.Join("; ", pipelineErrors.Select(f => f.Message)),
                ruleIds));
        }
    }

    private static string FormatValidationFailure(IReadOnlyList<WorkflowPackageValidationError> errors)
    {
        var first = errors[0];
        return errors.Count == 1
            ? $"Workflow package import failed validation for workflow '{first.WorkflowKey}': {first.Message}"
            : "Workflow package import failed validation: "
                + string.Join(" | ", errors.Select(e => $"'{e.WorkflowKey}': {e.Message}"));
    }

    private static WorkflowNodeDto ToDto(WorkflowPackageWorkflowNode node) =>
        new(
            Id: node.Id,
            Kind: node.Kind,
            AgentKey: node.AgentKey,
            AgentVersion: node.AgentVersion,
            OutputScript: node.OutputScript,
            OutputPorts: node.OutputPorts,
            LayoutX: node.LayoutX,
            LayoutY: node.LayoutY,
            SubflowKey: node.SubflowKey,
            SubflowVersion: node.SubflowVersion,
            ReviewMaxRounds: node.ReviewMaxRounds,
            LoopDecision: node.LoopDecision,
            InputScript: node.InputScript,
            OptOutLastRoundReminder: node.OptOutLastRoundReminder,
            RejectionHistory: node.RejectionHistory,
            MirrorOutputToWorkflowVar: node.MirrorOutputToWorkflowVar,
            OutputPortReplacements: node.OutputPortReplacements,
            Template: node.Template,
            OutputType: node.OutputType,
            SwarmProtocol: node.SwarmProtocol,
            SwarmN: node.SwarmN,
            ContributorAgentKey: node.ContributorAgentKey,
            ContributorAgentVersion: node.ContributorAgentVersion,
            SynthesizerAgentKey: node.SynthesizerAgentKey,
            SynthesizerAgentVersion: node.SynthesizerAgentVersion,
            CoordinatorAgentKey: node.CoordinatorAgentKey,
            CoordinatorAgentVersion: node.CoordinatorAgentVersion,
            SwarmTokenBudget: node.SwarmTokenBudget);

    private static WorkflowEdgeDto ToDto(WorkflowPackageWorkflowEdge edge) =>
        new(
            FromNodeId: edge.FromNodeId,
            FromPort: edge.FromPort,
            ToNodeId: edge.ToNodeId,
            ToPort: edge.ToPort,
            RotatesRound: edge.RotatesRound,
            SortOrder: edge.SortOrder);

    private static WorkflowInputDto ToDto(WorkflowPackageWorkflowInput input) =>
        new(
            Key: input.Key,
            DisplayName: input.DisplayName,
            Kind: input.Kind,
            Required: input.Required,
            DefaultValueJson: input.DefaultValueJson,
            Description: input.Description,
            Ordinal: input.Ordinal);

    private async Task<WorkflowPackageImportPlan> BuildImportPlanAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken)
    {
        // sc-272 PR2: package import is the canonical "self-contained dependency closure"
        // boundary. The validator replaces the static ValidatePackage throw path so partial
        // packages never reach the apply loop. The exception message is preserved on
        // rejection for back-compat with API callers that match on it.
        var admission = admissionValidator.Validate(package);
        if (admission is Rejected<AdmittedPackageImport> rejected)
        {
            throw new WorkflowPackageResolutionException(rejected.Reason.Reason);
        }

        // Future: thread `_ = ((Accepted<AdmittedPackageImport>)admission).Value` through the
        // plan so the apply loop's contract is "consume admitted package only". For PR2 the
        // executor still operates on `package` because the admitted value is structurally
        // equivalent and the change is invisible at this level.
        var items = new List<WorkflowPackageImportItem>();
        var warnings = new List<string>();

        var agentPlan = await PlanAgentImportsAsync(package, cancellationToken);
        items.AddRange(agentPlan.Items);

        var unembeddedAgentPlan = await PlanUnembeddedAgentReferencesAsync(package, cancellationToken);
        items.AddRange(unembeddedAgentPlan);

        var unembeddedSubflowPlan = await PlanUnembeddedSubflowReferencesAsync(package, cancellationToken);
        items.AddRange(unembeddedSubflowPlan);

        var workflowPlan = await PlanWorkflowImportsAsync(package, agentPlan.VersionMap, cancellationToken);
        items.AddRange(workflowPlan.Items);

        var remappedPackage = RemapPackage(package, agentPlan.VersionMap, workflowPlan.VersionMap);

        foreach (var assignment in remappedPackage.AgentRoleAssignments)
        {
            items.Add(await PreviewAgentRoleAssignmentAsync(assignment, cancellationToken));
        }

        foreach (var role in remappedPackage.Roles)
        {
            items.Add(await PreviewRoleAsync(role, cancellationToken));
        }

        foreach (var skill in remappedPackage.Skills)
        {
            items.Add(await PreviewSkillAsync(skill, cancellationToken));
        }

        foreach (var server in remappedPackage.McpServers)
        {
            items.Add(await PreviewMcpServerAsync(server, warnings, cancellationToken));
        }

        var preview = new WorkflowPackageImportPreview(
            remappedPackage.EntryPoint,
            items
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .ThenBy(item => item.Version)
                .ToArray(),
            warnings.ToArray());

        return new WorkflowPackageImportPlan(remappedPackage, preview);
    }

    private async Task<VersionedImportPlan> PlanAgentImportsAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken)
    {
        var items = new List<WorkflowPackageImportItem>();
        var versionMap = new Dictionary<PackageVersionKey, int>();

        foreach (var group in package.Agents.GroupBy(agent => agent.Key, StringComparer.Ordinal))
        {
            var packageAgents = group.OrderBy(agent => agent.Version).ToArray();
            var maxExistingVersion = await MaxAgentVersionAsync(group.Key, cancellationToken);
            var nextVersion = Math.Max(maxExistingVersion ?? 0, packageAgents.Max(agent => agent.Version)) + 1;

            foreach (var agent in packageAgents)
            {
                var key = new PackageVersionKey(agent.Key, agent.Version);

                if (maxExistingVersion is int existingVersion && existingVersion > agent.Version)
                {
                    versionMap[key] = agent.Version;
                    items.Add(Conflict(
                        WorkflowPackageImportResourceKind.Agent,
                        agent.Key,
                        agent.Version,
                        $"Target already has agent '{agent.Key}' v{existingVersion}, which is higher than imported v{agent.Version}.",
                        sourceVersion: agent.Version,
                        existingMaxVersion: existingVersion));
                    continue;
                }

                var existing = await agentConfigRepository.TryGetAsync(agent.Key, agent.Version, cancellationToken);
                if (existing is null)
                {
                    versionMap[key] = agent.Version;
                    items.Add(Create(
                        WorkflowPackageImportResourceKind.Agent,
                        agent.Key,
                        agent.Version,
                        sourceVersion: agent.Version,
                        existingMaxVersion: maxExistingVersion));
                }
                else if (Equivalent(agent, existing))
                {
                    versionMap[key] = agent.Version;
                    items.Add(Reuse(
                        WorkflowPackageImportResourceKind.Agent,
                        agent.Key,
                        agent.Version,
                        sourceVersion: agent.Version,
                        existingMaxVersion: maxExistingVersion));
                }
                else
                {
                    var targetVersion = nextVersion++;
                    versionMap[key] = targetVersion;
                    items.Add(CreateVersionBump(
                        WorkflowPackageImportResourceKind.Agent,
                        agent.Key,
                        agent.Version,
                        targetVersion,
                        "configuration or outputs",
                        existingMaxVersion: maxExistingVersion));
                }
            }
        }

        return new VersionedImportPlan(versionMap, items);
    }

    private async Task<VersionedImportPlan> PlanWorkflowImportsAsync(
        WorkflowPackage package,
        IReadOnlyDictionary<PackageVersionKey, int> agentVersionMap,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<PackageVersionKey, int> workflowVersionMap = package.Workflows
            .ToDictionary(
                workflow => new PackageVersionKey(workflow.Key, workflow.Version),
                workflow => workflow.Version);
        IReadOnlyList<WorkflowPackageImportItem> items = Array.Empty<WorkflowPackageImportItem>();

        for (var attempt = 0; attempt <= package.Workflows.Count; attempt++)
        {
            var plan = await PlanWorkflowImportsOnceAsync(
                package,
                agentVersionMap,
                workflowVersionMap,
                cancellationToken);

            if (VersionMapsEqual(workflowVersionMap, plan.VersionMap))
            {
                return plan;
            }

            workflowVersionMap = plan.VersionMap;
            items = plan.Items;
        }

        return new VersionedImportPlan(workflowVersionMap, items);
    }

    private async Task<VersionedImportPlan> PlanWorkflowImportsOnceAsync(
        WorkflowPackage package,
        IReadOnlyDictionary<PackageVersionKey, int> agentVersionMap,
        IReadOnlyDictionary<PackageVersionKey, int> workflowVersionMap,
        CancellationToken cancellationToken)
    {
        var items = new List<WorkflowPackageImportItem>();
        var nextWorkflowVersionMap = new Dictionary<PackageVersionKey, int>();

        foreach (var group in package.Workflows.GroupBy(workflow => workflow.Key, StringComparer.Ordinal))
        {
            var packageWorkflows = group.OrderBy(workflow => workflow.Version).ToArray();
            var maxExistingVersion = await MaxWorkflowVersionAsync(group.Key, cancellationToken);
            var nextVersion = Math.Max(maxExistingVersion ?? 0, packageWorkflows.Max(workflow => workflow.Version)) + 1;

            foreach (var workflow in packageWorkflows)
            {
                var key = new PackageVersionKey(workflow.Key, workflow.Version);
                var remappedWorkflow = RemapWorkflow(workflow, agentVersionMap, workflowVersionMap);
                var referencesRemapped = WorkflowReferencesRemapped(workflow, agentVersionMap, workflowVersionMap);

                if (maxExistingVersion is int existingVersion && existingVersion > workflow.Version)
                {
                    nextWorkflowVersionMap[key] = workflow.Version;
                    items.Add(Conflict(
                        WorkflowPackageImportResourceKind.Workflow,
                        workflow.Key,
                        workflow.Version,
                        $"Target already has workflow '{workflow.Key}' v{existingVersion}, which is higher than imported v{workflow.Version}.",
                        sourceVersion: workflow.Version,
                        existingMaxVersion: existingVersion));
                    continue;
                }

                var existing = await workflowRepository.TryGetAsync(workflow.Key, workflow.Version, cancellationToken);
                if (existing is null)
                {
                    nextWorkflowVersionMap[key] = workflow.Version;
                    items.Add(Create(
                        WorkflowPackageImportResourceKind.Workflow,
                        workflow.Key,
                        workflow.Version,
                        sourceVersion: workflow.Version,
                        existingMaxVersion: maxExistingVersion));
                }
                else if (!referencesRemapped && Equivalent(remappedWorkflow, existing))
                {
                    nextWorkflowVersionMap[key] = workflow.Version;
                    items.Add(Reuse(
                        WorkflowPackageImportResourceKind.Workflow,
                        workflow.Key,
                        workflow.Version,
                        sourceVersion: workflow.Version,
                        existingMaxVersion: maxExistingVersion));
                }
                else
                {
                    var targetVersion = nextVersion++;
                    nextWorkflowVersionMap[key] = targetVersion;
                    items.Add(CreateVersionBump(
                        WorkflowPackageImportResourceKind.Workflow,
                        workflow.Key,
                        workflow.Version,
                        targetVersion,
                        "graph metadata",
                        existingMaxVersion: maxExistingVersion));
                }
            }
        }

        return new VersionedImportPlan(nextWorkflowVersionMap, items);
    }

    // sc-272 PR2: structural validation lives in WorkflowPackageImportValidator (schema
    // version, entry-point presence, version-pin completeness). Closure validation —
    // every node ref resolves to either an embedded copy or an existing DB row — happens
    // here in the planner so the import surface can fall back to the local library
    // instead of demanding byte-perfect re-embedding of unchanged dependencies.

    /// <summary>
    /// Walk every workflow node's agent slots (the generic AgentKey plus the three Swarm
    /// slots). For each ref that isn't represented in <c>package.Agents</c>, resolve it
    /// against the local DB: a hit becomes a <c>Reuse</c> plan item; a miss becomes a
    /// <c>Conflict</c>. Embedded agents are handled by <see cref="PlanAgentImportsAsync"/>.
    /// </summary>
    private async Task<IReadOnlyList<WorkflowPackageImportItem>> PlanUnembeddedAgentReferencesAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken)
    {
        var embedded = package.Agents
            .Select(agent => new PackageVersionKey(agent.Key, agent.Version))
            .ToHashSet();

        var unembedded = new HashSet<PackageVersionKey>();
        foreach (var workflow in package.Workflows)
        {
            foreach (var node in workflow.Nodes)
            {
                CollectAgentRef(node.AgentKey, node.AgentVersion, embedded, unembedded);
                CollectAgentRef(node.ContributorAgentKey, node.ContributorAgentVersion, embedded, unembedded);
                CollectAgentRef(node.SynthesizerAgentKey, node.SynthesizerAgentVersion, embedded, unembedded);
                CollectAgentRef(node.CoordinatorAgentKey, node.CoordinatorAgentVersion, embedded, unembedded);
            }
        }

        if (unembedded.Count == 0)
        {
            return Array.Empty<WorkflowPackageImportItem>();
        }

        var items = new List<WorkflowPackageImportItem>(unembedded.Count);
        foreach (var reference in unembedded.OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Version))
        {
            var existing = await agentConfigRepository.TryGetAsync(reference.Key, reference.Version, cancellationToken);
            // Resolve the library's max version for this key so a Conflict row carries the
            // structured datum a Bump / UseExisting resolution needs (sc-393). For ReuseExternal
            // we also surface it because the resolver UI may want to flag "library has a newer
            // version available" even when the requested version exists.
            var existingMaxVersion = await MaxAgentVersionAsync(reference.Key, cancellationToken);
            items.Add(existing is null
                ? Conflict(
                    WorkflowPackageImportResourceKind.Agent,
                    reference.Key,
                    reference.Version,
                    $"Workflow node references agent '{reference.Key}' v{reference.Version} but the package omits it and the target library has no such version.",
                    sourceVersion: reference.Version,
                    existingMaxVersion: existingMaxVersion)
                : ReuseExternal(
                    WorkflowPackageImportResourceKind.Agent,
                    reference.Key,
                    reference.Version,
                    "Reusing existing target-library agent (not embedded in package).",
                    existingMaxVersion: existingMaxVersion));
        }

        return items;

        static void CollectAgentRef(
            string? key,
            int? version,
            HashSet<PackageVersionKey> embedded,
            HashSet<PackageVersionKey> unembedded)
        {
            if (string.IsNullOrWhiteSpace(key) || version is null) return;
            var pvk = new PackageVersionKey(key, version.Value);
            if (embedded.Contains(pvk)) return;
            unembedded.Add(pvk);
        }
    }

    /// <summary>
    /// Walk every workflow node's subflow ref. For each ref that isn't represented in
    /// <c>package.Workflows</c>, resolve against the local DB: hit → <c>Reuse</c>,
    /// miss → <c>Conflict</c>. Embedded workflows are handled by
    /// <see cref="PlanWorkflowImportsAsync"/>.
    /// </summary>
    private async Task<IReadOnlyList<WorkflowPackageImportItem>> PlanUnembeddedSubflowReferencesAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken)
    {
        var embedded = package.Workflows
            .Select(workflow => new PackageVersionKey(workflow.Key, workflow.Version))
            .ToHashSet();

        var unembedded = new HashSet<PackageVersionKey>();
        foreach (var workflow in package.Workflows)
        {
            foreach (var node in workflow.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.SubflowKey) || node.SubflowVersion is null) continue;
                var pvk = new PackageVersionKey(node.SubflowKey!, node.SubflowVersion.Value);
                if (embedded.Contains(pvk)) continue;
                unembedded.Add(pvk);
            }
        }

        if (unembedded.Count == 0)
        {
            return Array.Empty<WorkflowPackageImportItem>();
        }

        var items = new List<WorkflowPackageImportItem>(unembedded.Count);
        foreach (var reference in unembedded.OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Version))
        {
            var existing = await workflowRepository.TryGetAsync(reference.Key, reference.Version, cancellationToken);
            var existingMaxVersion = await MaxWorkflowVersionAsync(reference.Key, cancellationToken);
            items.Add(existing is null
                ? Conflict(
                    WorkflowPackageImportResourceKind.Workflow,
                    reference.Key,
                    reference.Version,
                    $"Workflow node references subflow '{reference.Key}' v{reference.Version} but the package omits it and the target library has no such version.",
                    sourceVersion: reference.Version,
                    existingMaxVersion: existingMaxVersion)
                : ReuseExternal(
                    WorkflowPackageImportResourceKind.Workflow,
                    reference.Key,
                    reference.Version,
                    "Reusing existing target-library workflow (not embedded in package).",
                    existingMaxVersion: existingMaxVersion));
        }

        return items;
    }

    private async Task<int?> MaxWorkflowVersionAsync(string workflowKey, CancellationToken cancellationToken) =>
        await dbContext.Workflows
            .Where(workflow => workflow.Key == workflowKey)
            .Select(workflow => (int?)workflow.Version)
            .MaxAsync(cancellationToken);

    private async Task<int?> MaxAgentVersionAsync(string agentKey, CancellationToken cancellationToken) =>
        await dbContext.Agents
            .Where(agent => agent.Key == agentKey)
            .Select(agent => (int?)agent.Version)
            .MaxAsync(cancellationToken);

    private static WorkflowPackage RemapPackage(
        WorkflowPackage package,
        IReadOnlyDictionary<PackageVersionKey, int> agentVersionMap,
        IReadOnlyDictionary<PackageVersionKey, int> workflowVersionMap)
    {
        var entryPoint = new WorkflowPackageReference(
            package.EntryPoint.Key,
            RemapVersion(workflowVersionMap, package.EntryPoint.Key, package.EntryPoint.Version));

        return package with
        {
            EntryPoint = entryPoint,
            Workflows = package.Workflows
                .Select(workflow => RemapWorkflow(workflow, agentVersionMap, workflowVersionMap) with
                {
                    Version = RemapVersion(workflowVersionMap, workflow.Key, workflow.Version),
                })
                .ToArray(),
            Agents = package.Agents
                .Select(agent => agent with
                {
                    Version = RemapVersion(agentVersionMap, agent.Key, agent.Version),
                })
                .ToArray(),
            Manifest = package.Manifest is null
                ? null
                : RemapManifest(package.Manifest, agentVersionMap, workflowVersionMap),
        };
    }

    private static WorkflowPackageWorkflow RemapWorkflow(
        WorkflowPackageWorkflow workflow,
        IReadOnlyDictionary<PackageVersionKey, int> agentVersionMap,
        IReadOnlyDictionary<PackageVersionKey, int> workflowVersionMap) =>
        workflow with
        {
            Nodes = workflow.Nodes
                .Select(node => node with
                {
                    AgentVersion = node.AgentKey is null || node.AgentVersion is null
                        ? node.AgentVersion
                        : RemapVersion(agentVersionMap, node.AgentKey, node.AgentVersion.Value),
                    SubflowVersion = node.SubflowKey is null || node.SubflowVersion is null
                        ? node.SubflowVersion
                        : RemapVersion(workflowVersionMap, node.SubflowKey, node.SubflowVersion.Value),
                })
                .ToArray(),
        };

    private static bool WorkflowReferencesRemapped(
        WorkflowPackageWorkflow workflow,
        IReadOnlyDictionary<PackageVersionKey, int> agentVersionMap,
        IReadOnlyDictionary<PackageVersionKey, int> workflowVersionMap) =>
        workflow.Nodes.Any(node =>
            (node.AgentKey is not null &&
             node.AgentVersion is int agentVersion &&
             RemapVersion(agentVersionMap, node.AgentKey, agentVersion) != agentVersion) ||
            (node.SubflowKey is not null &&
             node.SubflowVersion is int subflowVersion &&
             RemapVersion(workflowVersionMap, node.SubflowKey, subflowVersion) != subflowVersion));

    private static WorkflowPackageManifest RemapManifest(
        WorkflowPackageManifest manifest,
        IReadOnlyDictionary<PackageVersionKey, int> agentVersionMap,
        IReadOnlyDictionary<PackageVersionKey, int> workflowVersionMap) =>
        manifest with
        {
            Workflows = manifest.Workflows
                .Select(workflow => workflow with
                {
                    Version = RemapVersion(workflowVersionMap, workflow.Key, workflow.Version),
                })
                .ToArray(),
            Agents = manifest.Agents
                .Select(agent => agent with
                {
                    Version = RemapVersion(agentVersionMap, agent.Key, agent.Version),
                })
                .ToArray(),
        };

    private static int RemapVersion(
        IReadOnlyDictionary<PackageVersionKey, int> versionMap,
        string key,
        int version) =>
        versionMap.TryGetValue(new PackageVersionKey(key, version), out var mappedVersion)
            ? mappedVersion
            : version;

    private static bool VersionMapsEqual(
        IReadOnlyDictionary<PackageVersionKey, int> left,
        IReadOnlyDictionary<PackageVersionKey, int> right) =>
        left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private async Task<WorkflowPackageImportItem> PreviewAgentRoleAssignmentAsync(
        WorkflowPackageAgentRoleAssignment assignment,
        CancellationToken cancellationToken)
    {
        var existingRoles = await agentRoleRepository.GetRolesForAgentAsync(assignment.AgentKey, cancellationToken);
        var existingRoleKeys = existingRoles
            .Select(role => role.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var packageRoleKeys = assignment.RoleKeys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (existingRoleKeys.Length == 0 && packageRoleKeys.Length > 0)
        {
            return Create(WorkflowPackageImportResourceKind.AgentRoleAssignment, assignment.AgentKey, null);
        }

        return existingRoleKeys.SequenceEqual(packageRoleKeys, StringComparer.Ordinal)
            ? Reuse(WorkflowPackageImportResourceKind.AgentRoleAssignment, assignment.AgentKey, null)
            : Conflict(
                WorkflowPackageImportResourceKind.AgentRoleAssignment,
                assignment.AgentKey,
                null,
                "This agent already has a different role assignment set.");
    }

    private async Task<WorkflowPackageImportItem> PreviewRoleAsync(
        WorkflowPackageRole packageRole,
        CancellationToken cancellationToken)
    {
        var existing = await agentRoleRepository.GetByKeyAsync(packageRole.Key, cancellationToken);
        if (existing is null)
        {
            return Create(WorkflowPackageImportResourceKind.Role, packageRole.Key, null);
        }

        var grants = await agentRoleRepository.GetGrantsAsync(existing.Id, cancellationToken);
        var skillIds = await agentRoleRepository.GetSkillGrantsAsync(existing.Id, cancellationToken);
        var skillNames = new List<string>(skillIds.Count);
        foreach (var skillId in skillIds)
        {
            var skill = await skillRepository.GetAsync(skillId, cancellationToken);
            if (skill is not null)
            {
                skillNames.Add(skill.Name);
            }
        }

        return Equivalent(packageRole, existing, grants, skillNames)
            ? Reuse(WorkflowPackageImportResourceKind.Role, packageRole.Key, null)
            : Conflict(
                WorkflowPackageImportResourceKind.Role,
                packageRole.Key,
                null,
                "A role with this key already exists with different metadata, grants, or skills.");
    }

    private async Task<WorkflowPackageImportItem> PreviewSkillAsync(
        WorkflowPackageSkill packageSkill,
        CancellationToken cancellationToken)
    {
        var existing = await skillRepository.GetByNameAsync(packageSkill.Name, cancellationToken);
        if (existing is null)
        {
            return Create(WorkflowPackageImportResourceKind.Skill, packageSkill.Name, null);
        }

        return Equivalent(packageSkill, existing)
            ? Reuse(WorkflowPackageImportResourceKind.Skill, packageSkill.Name, null)
            : Conflict(
                WorkflowPackageImportResourceKind.Skill,
                packageSkill.Name,
                null,
                "A skill with this name already exists with different body or archive state.");
    }

    private async Task<WorkflowPackageImportItem> PreviewMcpServerAsync(
        WorkflowPackageMcpServer packageServer,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (packageServer.HasBearerToken)
        {
            warnings.Add($"MCP server '{packageServer.Key}' exported with a bearer token. The token is not in the package and must be configured locally after import.");
        }

        var endpointPolicyError = await ValidateMcpEndpointAsync(packageServer.EndpointUrl, cancellationToken);
        if (endpointPolicyError is not null)
        {
            return Conflict(
                WorkflowPackageImportResourceKind.McpServer,
                packageServer.Key,
                null,
                endpointPolicyError);
        }

        var existing = await mcpServerRepository.GetByKeyAsync(packageServer.Key, cancellationToken);
        if (existing is null)
        {
            return Create(WorkflowPackageImportResourceKind.McpServer, packageServer.Key, null);
        }

        var tools = await mcpServerRepository.GetToolsAsync(existing.Id, cancellationToken);
        return Equivalent(packageServer, existing, tools)
            ? Reuse(WorkflowPackageImportResourceKind.McpServer, packageServer.Key, null)
            : Conflict(
                WorkflowPackageImportResourceKind.McpServer,
                packageServer.Key,
                null,
                "An MCP server with this key already exists with different metadata or tools.");
    }

    private async Task<string?> ValidateMcpEndpointAsync(string endpointUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
        {
            return "MCP server endpoint URL must be an absolute URI.";
        }

        var result = await mcpEndpointPolicy.ValidateAsync(uri, cancellationToken);
        return result.IsAllowed
            ? null
            : result.Reason ?? "Endpoint is not allowed by the configured MCP endpoint policy.";
    }

    private async Task ImportSkillsAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var skill in package.Skills)
        {
            if (await dbContext.Skills.AnyAsync(existing => existing.Name == skill.Name, cancellationToken))
            {
                continue;
            }

            dbContext.Skills.Add(new SkillEntity
            {
                Name = skill.Name.Trim(),
                Body = skill.Body,
                CreatedAtUtc = UsePackageDateOrNow(skill.CreatedAtUtc, now),
                CreatedBy = Trim(skill.CreatedBy),
                UpdatedAtUtc = UsePackageDateOrNow(skill.UpdatedAtUtc, now),
                UpdatedBy = Trim(skill.UpdatedBy),
                IsArchived = skill.IsArchived,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportMcpServersAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var server in package.McpServers)
        {
            if (await dbContext.McpServers.AnyAsync(existing => existing.Key == server.Key, cancellationToken))
            {
                continue;
            }

            var entity = new McpServerEntity
            {
                Key = server.Key.Trim(),
                DisplayName = server.DisplayName.Trim(),
                Transport = server.Transport,
                EndpointUrl = server.EndpointUrl.Trim(),
                BearerTokenCipher = null,
                HealthStatus = server.HealthStatus,
                LastVerifiedAtUtc = server.LastVerifiedAtUtc,
                LastVerificationError = Trim(server.LastVerificationError),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                IsArchived = server.IsArchived,
            };

            dbContext.McpServers.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            dbContext.McpServerTools.AddRange(server.Tools
                .Select(tool => new McpServerToolEntity
                {
                    ServerId = entity.Id,
                    ToolName = tool.ToolName.Trim(),
                    Description = Trim(tool.Description),
                    ParametersJson = tool.Parameters?.ToJsonString(SerializerOptions),
                    IsMutating = tool.IsMutating,
                    SyncedAtUtc = UsePackageDateOrNow(tool.SyncedAtUtc, now),
                }));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportRolesAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var role in package.Roles)
        {
            if (await dbContext.AgentRoles.AnyAsync(existing => existing.Key == role.Key, cancellationToken))
            {
                continue;
            }

            var skillIds = await dbContext.Skills
                .Where(skill => role.SkillNames.Contains(skill.Name))
                .Select(skill => new { skill.Name, skill.Id })
                .ToDictionaryAsync(skill => skill.Name, skill => skill.Id, StringComparer.Ordinal, cancellationToken);

            var missingSkills = role.SkillNames.Where(skillName => !skillIds.ContainsKey(skillName)).ToArray();
            if (missingSkills.Length > 0)
            {
                throw new WorkflowPackageResolutionException(
                    $"Workflow package import could not resolve skill '{missingSkills[0]}' for role '{role.Key}'.");
            }

            var entity = new AgentRoleEntity
            {
                Key = role.Key.Trim(),
                DisplayName = role.DisplayName.Trim(),
                Description = Trim(role.Description),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                IsArchived = role.IsArchived,
            };

            dbContext.AgentRoles.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            dbContext.AgentRoleToolGrants.AddRange(role.ToolGrants
                .Select(grant => new AgentRoleToolGrantEntity
                {
                    RoleId = entity.Id,
                    Category = grant.Category,
                    ToolIdentifier = grant.ToolIdentifier.Trim(),
                }));
            dbContext.AgentRoleSkillGrants.AddRange(role.SkillNames
                .Select(skillName => new AgentRoleSkillGrantEntity
                {
                    RoleId = entity.Id,
                    SkillId = skillIds[skillName],
                }));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportAgentsAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var group in package.Agents.GroupBy(agent => agent.Key, StringComparer.Ordinal))
        {
            var packageVersions = group.OrderBy(agent => agent.Version).ToArray();
            var maxVersion = await dbContext.Agents
                .Where(agent => agent.Key == group.Key)
                .Select(agent => (int?)agent.Version)
                .MaxAsync(cancellationToken);
            var finalMaxVersion = Math.Max(maxVersion ?? 0, packageVersions.Max(agent => agent.Version));

            foreach (var existingActive in await dbContext.Agents
                         .Where(agent => agent.Key == group.Key && agent.IsActive)
                         .ToListAsync(cancellationToken))
            {
                existingActive.IsActive = existingActive.Version == finalMaxVersion;
            }

            foreach (var agent in packageVersions)
            {
                if (await dbContext.Agents.AnyAsync(
                        existing => existing.Key == agent.Key && existing.Version == agent.Version,
                        cancellationToken))
                {
                    continue;
                }

                dbContext.Agents.Add(new AgentConfigEntity
                {
                    Key = agent.Key.Trim(),
                    Version = agent.Version,
                    ConfigJson = agent.Config?.ToJsonString(SerializerOptions) ?? "{}",
                    CreatedAtUtc = UsePackageDateOrNow(agent.CreatedAtUtc, now),
                    CreatedBy = Trim(agent.CreatedBy),
                    IsActive = agent.Version == finalMaxVersion,
                    IsRetired = false,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportRoleAssignmentsAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var roleIds = await dbContext.AgentRoles
            .Where(role => package.Roles.Select(packageRole => packageRole.Key).Contains(role.Key))
            .Select(role => new { role.Key, role.Id })
            .ToDictionaryAsync(role => role.Key, role => role.Id, StringComparer.Ordinal, cancellationToken);

        foreach (var assignment in package.AgentRoleAssignments)
        {
            if (await dbContext.AgentRoleAssignments.AnyAsync(
                    existing => existing.AgentKey == assignment.AgentKey,
                    cancellationToken))
            {
                continue;
            }

            foreach (var roleKey in assignment.RoleKeys.Distinct(StringComparer.Ordinal))
            {
                if (!roleIds.TryGetValue(roleKey, out var roleId))
                {
                    throw new WorkflowPackageResolutionException(
                        $"Workflow package import could not resolve role '{roleKey}' for agent '{assignment.AgentKey}'.");
                }

                dbContext.AgentRoleAssignments.Add(new AgentRoleAssignmentEntity
                {
                    AgentKey = assignment.AgentKey.Trim(),
                    RoleId = roleId,
                    CreatedAtUtc = now,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportWorkflowsAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var workflow in package.Workflows.OrderBy(workflow => workflow.Key).ThenBy(workflow => workflow.Version))
        {
            if (await dbContext.Workflows.AnyAsync(
                    existing => existing.Key == workflow.Key && existing.Version == workflow.Version,
                    cancellationToken))
            {
                continue;
            }

            dbContext.Workflows.Add(new WorkflowEntity
            {
                Key = workflow.Key.Trim(),
                Version = workflow.Version,
                Name = workflow.Name.Trim(),
                MaxRoundsPerRound = workflow.MaxRoundsPerRound,
                Category = workflow.Category,
                TagsJson = JsonSerializer.Serialize(workflow.Tags, SerializerOptions),
                WorkflowVarsReadsJson = WorkflowJson.SerializeStringList(workflow.WorkflowVarsReads),
                WorkflowVarsWritesJson = WorkflowJson.SerializeStringList(workflow.WorkflowVarsWrites),
                CreatedAtUtc = UsePackageDateOrNow(workflow.CreatedAtUtc, now),
                Nodes = workflow.Nodes
                    .Select(node => new WorkflowNodeEntity
                    {
                        NodeId = node.Id,
                        Kind = node.Kind,
                        AgentKey = Trim(node.AgentKey),
                        AgentVersion = node.AgentVersion,
                        OutputScript = node.OutputScript,
                        InputScript = node.InputScript,
                        OutputPortsJson = JsonSerializer.Serialize(node.OutputPorts, SerializerOptions),
                        LayoutX = node.LayoutX,
                        LayoutY = node.LayoutY,
                        SubflowKey = Trim(node.SubflowKey),
                        SubflowVersion = node.SubflowVersion,
                        ReviewMaxRounds = node.ReviewMaxRounds,
                        LoopDecision = Trim(node.LoopDecision),
                        OptOutLastRoundReminder = node.OptOutLastRoundReminder,
                        RejectionHistoryConfigJson = WorkflowJson.SerializeRejectionHistoryConfig(node.RejectionHistory),
                        MirrorOutputToWorkflowVar = Trim(node.MirrorOutputToWorkflowVar),
                        OutputPortReplacementsJson = WorkflowJson.SerializePortReplacements(node.OutputPortReplacements),
                        Template = node.Template,
                        OutputType = NormalizeOutputType(node.OutputType),
                        SwarmProtocol = Trim(node.SwarmProtocol),
                        SwarmN = node.SwarmN,
                        ContributorAgentKey = Trim(node.ContributorAgentKey),
                        ContributorAgentVersion = node.ContributorAgentVersion,
                        SynthesizerAgentKey = Trim(node.SynthesizerAgentKey),
                        SynthesizerAgentVersion = node.SynthesizerAgentVersion,
                        CoordinatorAgentKey = Trim(node.CoordinatorAgentKey),
                        CoordinatorAgentVersion = node.CoordinatorAgentVersion,
                        SwarmTokenBudget = node.SwarmTokenBudget,
                    })
                    .ToList(),
                Edges = workflow.Edges
                    .Select(edge => new WorkflowEdgeEntity
                    {
                        FromNodeId = edge.FromNodeId,
                        FromPort = edge.FromPort,
                        ToNodeId = edge.ToNodeId,
                        ToPort = string.IsNullOrWhiteSpace(edge.ToPort) ? WorkflowEdge.DefaultInputPort : edge.ToPort,
                        RotatesRound = edge.RotatesRound,
                        SortOrder = edge.SortOrder,
                    })
                    .ToList(),
                Inputs = workflow.Inputs
                    .Select(input => new WorkflowInputEntity
                    {
                        Key = input.Key.Trim(),
                        DisplayName = input.DisplayName.Trim(),
                        Kind = input.Kind,
                        Required = input.Required,
                        DefaultValueJson = input.DefaultValueJson,
                        Description = input.Description,
                        Ordinal = input.Ordinal,
                    })
                    .ToList(),
            });
        }
    }

    private static WorkflowPackageImportItem Create(
        WorkflowPackageImportResourceKind kind,
        string key,
        int? version,
        int? sourceVersion = null,
        int? existingMaxVersion = null) =>
        new(
            kind,
            key,
            version,
            WorkflowPackageImportAction.Create,
            "Will create this package resource.",
            SourceVersion: sourceVersion ?? version,
            ExistingMaxVersion: existingMaxVersion);

    private static WorkflowPackageImportItem Reuse(
        WorkflowPackageImportResourceKind kind,
        string key,
        int? version,
        int? sourceVersion = null,
        int? existingMaxVersion = null) =>
        new(
            kind,
            key,
            version,
            WorkflowPackageImportAction.Reuse,
            "Matching target resource already exists.",
            SourceVersion: sourceVersion ?? version,
            ExistingMaxVersion: existingMaxVersion);

    private static WorkflowPackageImportItem ReuseExternal(
        WorkflowPackageImportResourceKind kind,
        string key,
        int? version,
        string message,
        int? existingMaxVersion = null) =>
        new(
            kind,
            key,
            version,
            WorkflowPackageImportAction.Reuse,
            message,
            SourceVersion: version,
            ExistingMaxVersion: existingMaxVersion);

    private static WorkflowPackageImportItem CreateVersionBump(
        WorkflowPackageImportResourceKind kind,
        string key,
        int importedVersion,
        int targetVersion,
        string changedDescription,
        int? existingMaxVersion = null) =>
        new(
            kind,
            key,
            targetVersion,
            WorkflowPackageImportAction.Create,
            $"Will create v{targetVersion} because imported v{importedVersion} matches an existing target version with different {changedDescription}.",
            SourceVersion: importedVersion,
            ExistingMaxVersion: existingMaxVersion);

    private static WorkflowPackageImportItem Conflict(
        WorkflowPackageImportResourceKind kind,
        string key,
        int? version,
        string message,
        int? sourceVersion = null,
        int? existingMaxVersion = null) =>
        new(
            kind,
            key,
            version,
            WorkflowPackageImportAction.Conflict,
            message,
            SourceVersion: sourceVersion ?? version,
            ExistingMaxVersion: existingMaxVersion);

    private static bool Equivalent(WorkflowPackageWorkflow packageWorkflow, Workflow existing) =>
        string.Equals(packageWorkflow.Name, existing.Name, StringComparison.Ordinal) &&
        packageWorkflow.MaxRoundsPerRound == existing.MaxRoundsPerRound &&
        packageWorkflow.Category == existing.Category &&
        packageWorkflow.Tags.SequenceEqual(existing.TagsOrEmpty, StringComparer.Ordinal) &&
        SerializedEquals(packageWorkflow.Nodes, existing.Nodes.Select(node =>
        {
            var packageNode = packageWorkflow.Nodes.SingleOrDefault(candidate => candidate.Id == node.Id);
            return new WorkflowPackageWorkflowNode(
            node.Id,
            node.Kind,
            NormalizeOptional(node.AgentKey),
            node.AgentVersion ?? packageNode?.AgentVersion,
            node.OutputScript,
            node.OutputPorts,
            node.LayoutX,
            node.LayoutY,
            NormalizeOptional(node.SubflowKey),
            node.SubflowVersion ?? packageNode?.SubflowVersion,
            node.ReviewMaxRounds,
            NormalizeOptional(node.LoopDecision),
            node.InputScript,
            node.OptOutLastRoundReminder,
            node.RejectionHistory,
            NormalizeOptional(node.MirrorOutputToWorkflowVar),
            node.OutputPortReplacements,
            node.Template,
            NormalizeOutputType(node.OutputType),
            NormalizeOptional(node.SwarmProtocol),
            node.SwarmN,
            NormalizeOptional(node.ContributorAgentKey),
            node.ContributorAgentVersion ?? packageNode?.ContributorAgentVersion,
            NormalizeOptional(node.SynthesizerAgentKey),
            node.SynthesizerAgentVersion ?? packageNode?.SynthesizerAgentVersion,
            NormalizeOptional(node.CoordinatorAgentKey),
            node.CoordinatorAgentVersion ?? packageNode?.CoordinatorAgentVersion,
            node.SwarmTokenBudget);
        })) &&
        SerializedEquals(packageWorkflow.Edges, existing.Edges.Select(edge => new WorkflowPackageWorkflowEdge(
            edge.FromNodeId,
            edge.FromPort,
            edge.ToNodeId,
            edge.ToPort,
            edge.RotatesRound,
            edge.SortOrder))) &&
        SerializedEquals(packageWorkflow.Inputs, existing.Inputs.Select(input => new WorkflowPackageWorkflowInput(
            input.Key,
            input.DisplayName,
            input.Kind,
            input.Required,
            input.DefaultValueJson,
            input.Description,
            input.Ordinal))) &&
        WorkflowVarsListEqual(packageWorkflow.WorkflowVarsReads, existing.WorkflowVarsReads) &&
        WorkflowVarsListEqual(packageWorkflow.WorkflowVarsWrites, existing.WorkflowVarsWrites);

    private static bool WorkflowVarsListEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null && b is null)
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static bool Equivalent(WorkflowPackageAgent packageAgent, AgentConfig existing) =>
        packageAgent.Kind == existing.Kind &&
        JsonNode.DeepEquals(packageAgent.Config, JsonNode.Parse(existing.ConfigJson)) &&
        SerializedEquals(packageAgent.Outputs, existing.DeclaredOutputs.Select(output => new WorkflowPackageAgentOutput(
            output.Kind,
            output.Description,
            output.PayloadExample is JsonElement payload ? JsonNode.Parse(payload.GetRawText()) : null)));

    private static bool Equivalent(
        WorkflowPackageRole packageRole,
        AgentRole existing,
        IReadOnlyList<AgentRoleToolGrant> existingGrants,
        IReadOnlyList<string> existingSkillNames) =>
        string.Equals(packageRole.DisplayName, existing.DisplayName, StringComparison.Ordinal) &&
        string.Equals(packageRole.Description, existing.Description, StringComparison.Ordinal) &&
        packageRole.IsArchived == existing.IsArchived &&
        SerializedEquals(
            packageRole.ToolGrants.OrderBy(grant => grant.Category).ThenBy(grant => grant.ToolIdentifier, StringComparer.Ordinal),
            existingGrants.OrderBy(grant => grant.Category).ThenBy(grant => grant.ToolIdentifier, StringComparer.Ordinal)
                .Select(grant => new WorkflowPackageRoleGrant(grant.Category, grant.ToolIdentifier))) &&
        packageRole.SkillNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .SequenceEqual(existingSkillNames.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool Equivalent(WorkflowPackageSkill packageSkill, Skill existing) =>
        string.Equals(packageSkill.Body, existing.Body, StringComparison.Ordinal) &&
        packageSkill.IsArchived == existing.IsArchived;

    private static bool Equivalent(
        WorkflowPackageMcpServer packageServer,
        McpServer existing,
        IReadOnlyList<McpServerTool> existingTools) =>
        string.Equals(packageServer.DisplayName, existing.DisplayName, StringComparison.Ordinal) &&
        packageServer.Transport == existing.Transport &&
        string.Equals(packageServer.EndpointUrl, existing.EndpointUrl, StringComparison.Ordinal) &&
        packageServer.HasBearerToken == existing.HasBearerToken &&
        packageServer.IsArchived == existing.IsArchived &&
        SerializedEquals(
            packageServer.Tools.OrderBy(tool => tool.ToolName, StringComparer.Ordinal),
            existingTools.OrderBy(tool => tool.ToolName, StringComparer.Ordinal)
                .Select(tool => new WorkflowPackageMcpServerTool(
                    tool.ToolName,
                    tool.Description,
                    string.IsNullOrWhiteSpace(tool.ParametersJson) ? null : JsonNode.Parse(tool.ParametersJson),
                    tool.IsMutating,
                    tool.SyncedAtUtc)));

    private static bool SerializedEquals<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        JsonSerializer.SerializeToNode(left, JsonSerializerOptions.Web)?.ToJsonString() ==
        JsonSerializer.SerializeToNode(right, JsonSerializerOptions.Web)?.ToJsonString();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeOutputType(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "string" : value.Trim().ToLowerInvariant();

    private static DateTime UsePackageDateOrNow(DateTime value, DateTime now) =>
        value == default ? now : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// Replaces every null <see cref="IReadOnlyList{T}"/> in the package (top-level and nested)
    /// with an empty array. <see cref="WorkflowPackage"/> uses positional record syntax with
    /// non-nullable list parameters, but System.Text.Json constructs records by passing null for
    /// any property the JSON omits — and the LLM-driven `save_workflow_package` tool routinely
    /// emits packages without the collections it has nothing to contribute to (no agents, no
    /// roles, no MCP servers, etc.). Without this pass the validator and planner both NRE on
    /// `.Any()`/`.GroupBy()` over the null lists, which surfaces in chat as "Tool
    /// 'save_workflow_package' threw NullReferenceException".
    /// </summary>
    private static WorkflowPackage NormalizeNulls(WorkflowPackage package) => package with
    {
        Workflows = (package.Workflows ?? Array.Empty<WorkflowPackageWorkflow>())
            .Select(NormalizeWorkflow)
            .ToArray(),
        Agents = (package.Agents ?? Array.Empty<WorkflowPackageAgent>())
            .Select(NormalizeAgent)
            .ToArray(),
        AgentRoleAssignments = (package.AgentRoleAssignments ?? Array.Empty<WorkflowPackageAgentRoleAssignment>())
            .Select(NormalizeAssignment)
            .ToArray(),
        Roles = (package.Roles ?? Array.Empty<WorkflowPackageRole>())
            .Select(NormalizeRole)
            .ToArray(),
        Skills = package.Skills ?? Array.Empty<WorkflowPackageSkill>(),
        McpServers = (package.McpServers ?? Array.Empty<WorkflowPackageMcpServer>())
            .Select(NormalizeMcpServer)
            .ToArray(),
        Manifest = package.Manifest is null ? null : NormalizeManifest(package.Manifest),
    };

    private static WorkflowPackageWorkflow NormalizeWorkflow(WorkflowPackageWorkflow workflow) => workflow with
    {
        Tags = workflow.Tags ?? Array.Empty<string>(),
        Nodes = (workflow.Nodes ?? Array.Empty<WorkflowPackageWorkflowNode>())
            .Select(NormalizeNode)
            .ToArray(),
        Edges = workflow.Edges ?? Array.Empty<WorkflowPackageWorkflowEdge>(),
        Inputs = workflow.Inputs ?? Array.Empty<WorkflowPackageWorkflowInput>(),
    };

    private static WorkflowPackageWorkflowNode NormalizeNode(WorkflowPackageWorkflowNode node) => node with
    {
        OutputPorts = node.OutputPorts ?? Array.Empty<string>(),
    };

    private static WorkflowPackageAgent NormalizeAgent(WorkflowPackageAgent agent) => agent with
    {
        Outputs = agent.Outputs ?? Array.Empty<WorkflowPackageAgentOutput>(),
    };

    private static WorkflowPackageAgentRoleAssignment NormalizeAssignment(WorkflowPackageAgentRoleAssignment assignment) => assignment with
    {
        RoleKeys = assignment.RoleKeys ?? Array.Empty<string>(),
    };

    private static WorkflowPackageRole NormalizeRole(WorkflowPackageRole role) => role with
    {
        ToolGrants = role.ToolGrants ?? Array.Empty<WorkflowPackageRoleGrant>(),
        SkillNames = role.SkillNames ?? Array.Empty<string>(),
    };

    private static WorkflowPackageMcpServer NormalizeMcpServer(WorkflowPackageMcpServer server) => server with
    {
        Tools = server.Tools ?? Array.Empty<WorkflowPackageMcpServerTool>(),
    };

    private static WorkflowPackageManifest NormalizeManifest(WorkflowPackageManifest manifest) => manifest with
    {
        Workflows = manifest.Workflows ?? Array.Empty<WorkflowPackageReference>(),
        Agents = manifest.Agents ?? Array.Empty<WorkflowPackageReference>(),
        Roles = manifest.Roles ?? Array.Empty<string>(),
        Skills = manifest.Skills ?? Array.Empty<string>(),
        McpServers = manifest.McpServers ?? Array.Empty<string>(),
    };

    private readonly record struct PackageVersionKey(string Key, int Version);

    private sealed record VersionedImportPlan(
        IReadOnlyDictionary<PackageVersionKey, int> VersionMap,
        IReadOnlyList<WorkflowPackageImportItem> Items);

    private sealed record WorkflowPackageImportPlan(
        WorkflowPackage Package,
        WorkflowPackageImportPreview Preview);
}
