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

    public Task<WorkflowPackageImportPreview> PreviewAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default) =>
        PreviewAsync(package, resolutions: null, cancellationToken);

    public async Task<WorkflowPackageImportPreview> PreviewAsync(
        WorkflowPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var plan = await BuildImportPlanAsync(NormalizeNulls(package), resolutions, cancellationToken);
        return plan.Preview;
    }

    public Task<WorkflowPackageImportApplyResult> ApplyAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(package, resolutions: null, cancellationToken);

    public async Task<WorkflowPackageImportApplyResult> ApplyAsync(
        WorkflowPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var plan = await BuildImportPlanAsync(NormalizeNulls(package), resolutions, cancellationToken);
        var preview = plan.Preview;
        if (!preview.CanApply)
        {
            throw new WorkflowPackageResolutionException(
                "Workflow package import has conflicts. Preview the package and resolve conflicts before applying it.");
        }

        var importPackage = plan.Package;
        var lineage = plan.AgentLineage;
        var validationErrors = new List<WorkflowPackageValidationError>();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            validationErrors.Clear();
            await RunImportInTransactionAsync(importPackage, lineage, commit: true, validationErrors, cancellationToken);
        });

        if (validationErrors.Count > 0)
        {
            // RunImportInTransactionAsync rolled back when commit=true encounters validation
            // errors; surface as the same exception type the endpoint already maps to a 400.
            throw new WorkflowPackageResolutionException(FormatValidationFailure(validationErrors), validationErrors);
        }

        return new WorkflowPackageImportApplyResult(preview.EntryPoint, preview.Items, preview.Warnings);
    }

    public async Task<WorkflowPackageValidationResult> ValidateAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        // ValidateAsync intentionally ignores resolutions: the homepage assistant's
        // save_workflow_package tool runs validation BEFORE the user chooses any resolution
        // (the chip can only be offered once preview comes back valid). If a future caller
        // needs validation under a chosen resolution, add an overload mirroring PreviewAsync.
        var plan = await BuildImportPlanAsync(NormalizeNulls(package), resolutions: null, cancellationToken);
        if (!plan.Preview.CanApply)
        {
            // Conflict-only packages: caller (PreviewAsync) already reports the conflicts;
            // validation is moot until the conflicts are resolved.
            return WorkflowPackageValidationResult.Valid;
        }

        var importPackage = plan.Package;
        var lineage = plan.AgentLineage;
        var errors = new List<WorkflowPackageValidationError>();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            errors.Clear();
            await RunImportInTransactionAsync(importPackage, lineage, commit: false, errors, cancellationToken);
        });

        return errors.Count == 0
            ? WorkflowPackageValidationResult.Valid
            : new WorkflowPackageValidationResult(false, errors);
    }

    private async Task RunImportInTransactionAsync(
        WorkflowPackage importPackage,
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> agentLineage,
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
        await ImportAgentsAsync(importPackage, agentLineage, now, cancellationToken);
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
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
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

        // sc-395: port-shape compatibility refusal for UseExisting must run on the ORIGINAL
        // package — once ApplyResolutionsAsync rewrites the node refs from (key, sourceVersion)
        // to (key, libraryMax), we lose the ability to enumerate the package's "what ports does
        // it route through this agent?" set. Refusal items still go in the Items[] list and
        // CanApply blocks on RefusedCount, so the apply path will reject them.
        var portShapeRefusals = await CheckUseExistingPortShapeAsync(package, resolutions, cancellationToken);

        // sc-394: apply user-chosen resolutions BEFORE planning. The pre-process drops
        // UseExisting entities, rewrites Bump versions and Copy keys+versions, and rewrites
        // every transitive workflow-node ref (incl. Swarm slots) so the planner sees a
        // self-consistent post-resolution package. AgentLineage carries the (newKey,newVer) →
        // (origKey,origVer) provenance through to ImportAgentsAsync's ForkedFromKey/Version writes.
        var resolved = await ApplyResolutionsAsync(package, resolutions, cancellationToken);
        package = resolved.Package;

        // Future: thread `_ = ((Accepted<AdmittedPackageImport>)admission).Value` through the
        // plan so the apply loop's contract is "consume admitted package only". For PR2 the
        // executor still operates on `package` because the admitted value is structurally
        // equivalent and the change is invisible at this level.
        var items = new List<WorkflowPackageImportItem>();
        items.AddRange(portShapeRefusals);
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

        // Project the resolution lineage through the planner's auto-bump output so
        // ImportAgentsAsync can match by the FINAL (key,version) the agent row will land at.
        // Auto-bump-on-content-differs is rare for resolved entities (Bump targets a fresh
        // version, Copy lands at v1 of a probably-unique key), but the projection is cheap
        // and keeps lineage robust in the corner where it does fire.
        var finalAgentLineage = ProjectLineage(resolved.AgentLineage, agentPlan.VersionMap);

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

        return new WorkflowPackageImportPlan(remappedPackage, preview, finalAgentLineage);
    }

    /// <summary>
    /// Walks the lineage map (keyed by post-resolution agent identity) and rewrites the keys
    /// through the planner's auto-bump <paramref name="agentVersionMap"/>. After this, lineage
    /// is keyed by the (key,version) the row will actually land at in the DB — which is what
    /// ImportAgentsAsync iterates.
    /// </summary>
    private static IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> ProjectLineage(
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> postResolutionLineage,
        IReadOnlyDictionary<PackageVersionKey, int> agentVersionMap)
    {
        if (postResolutionLineage.Count == 0)
        {
            return postResolutionLineage;
        }

        var projected = new Dictionary<PackageVersionKey, PackageVersionKey>(postResolutionLineage.Count);
        foreach (var (postRes, origin) in postResolutionLineage)
        {
            var finalVersion = agentVersionMap.TryGetValue(postRes, out var v) ? v : postRes.Version;
            projected[new PackageVersionKey(postRes.Key, finalVersion)] = origin;
        }
        return projected;
    }

    /// <summary>
    /// sc-395: for each <c>UseExisting</c> agent resolution, verify that the library's
    /// existing-max version of that agent declares every output port the package's workflow
    /// nodes route through it. If a port is missing the library's declared outputs, emit a
    /// <see cref="WorkflowPackageImportAction.Refused"/> item — apply will refuse to commit.
    /// <para/>
    /// Runs against the ORIGINAL (pre-resolution) package because <see cref="ApplyResolutionsAsync"/>
    /// rewrites the node refs from <c>(key, sourceVersion)</c> to <c>(key, libraryMax)</c>,
    /// after which we can no longer enumerate "which nodes were routed through the source
    /// version we're being asked to drop?".
    /// </summary>
    private async Task<IReadOnlyList<WorkflowPackageImportItem>> CheckUseExistingPortShapeAsync(
        WorkflowPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken)
    {
        if (resolutions is null || resolutions.Count == 0)
        {
            return Array.Empty<WorkflowPackageImportItem>();
        }

        var refusals = new List<WorkflowPackageImportItem>();
        foreach (var resolution in resolutions.Values)
        {
            if (resolution.Mode != WorkflowPackageImportResolutionMode.UseExisting) continue;
            if (resolution.Target.Kind != WorkflowPackageImportResourceKind.Agent) continue;

            var target = resolution.Target;
            var sourceVersion = target.SourceVersion!.Value;
            var libraryMax = await MaxAgentVersionAsync(target.Key, cancellationToken);
            if (libraryMax is null) continue; // ValidateResolutionInput rejects this case earlier; safety guard.

            var libraryAgent = await agentConfigRepository.TryGetAsync(target.Key, libraryMax.Value, cancellationToken);
            if (libraryAgent is null) continue;

            var declaredOutputs = libraryAgent.DeclaredOutputs
                .Select(output => output.Kind)
                .ToHashSet(StringComparer.Ordinal);

            // Every output port any node routes through (target.Key, sourceVersion) must exist
            // in the library agent's DeclaredOutputs. Walk all four agent slots — we want the
            // refusal to fire even if a Swarm-slot ref is what triggers the missing port.
            var routedPorts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var workflow in package.Workflows)
            {
                foreach (var node in workflow.Nodes)
                {
                    if (NodeReferencesAgentSlot(node, target.Key, sourceVersion))
                    {
                        foreach (var port in node.OutputPorts ?? Array.Empty<string>())
                        {
                            routedPorts.Add(port);
                        }
                    }
                }
            }

            var missingPorts = routedPorts.Where(port => !declaredOutputs.Contains(port)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (missingPorts.Length > 0)
            {
                refusals.Add(Refused(
                    target.Kind,
                    target.Key,
                    sourceVersion,
                    libraryMax.Value,
                    $"UseExisting cannot rewrite refs to agent '{target.Key}' v{libraryMax.Value}: " +
                    $"library agent does not declare port(s) [{string.Join(", ", missingPorts)}] " +
                    $"that the package's workflow nodes route through v{sourceVersion}. " +
                    "Pick Bump or Copy instead, or update the library agent's declared outputs."));
            }
        }
        return refusals;
    }

    private static bool NodeReferencesAgentSlot(WorkflowPackageWorkflowNode node, string key, int version) =>
        (string.Equals(node.AgentKey, key, StringComparison.Ordinal) && node.AgentVersion == version) ||
        (string.Equals(node.ContributorAgentKey, key, StringComparison.Ordinal) && node.ContributorAgentVersion == version) ||
        (string.Equals(node.SynthesizerAgentKey, key, StringComparison.Ordinal) && node.SynthesizerAgentVersion == version) ||
        (string.Equals(node.CoordinatorAgentKey, key, StringComparison.Ordinal) && node.CoordinatorAgentVersion == version);

    /// <summary>
    /// sc-394: pre-process the package against user-chosen resolutions before the planner runs.
    /// Drops <c>UseExisting</c> entities and rewrites <c>Bump</c>/<c>Copy</c> entities in place
    /// (key + version), then rewrites every transitive workflow-node ref (the four agent slots
    /// plus the subflow slot), the entry-point reference, agent-role assignments, and the
    /// manifest so the package handed to the planner is self-consistent post-resolution.
    /// <para/>
    /// Returns the rewritten package plus an agent-lineage map keyed by the resolved
    /// (newKey, newVersion) → original (origKey, origVersion) for <c>Bump</c> and <c>Copy</c>.
    /// <c>UseExisting</c> writes no row, so it leaves no lineage entry.
    /// </summary>
    private async Task<ResolvedPackage> ApplyResolutionsAsync(
        WorkflowPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken)
    {
        if (resolutions is null || resolutions.Count == 0)
        {
            return new ResolvedPackage(
                package,
                new Dictionary<PackageVersionKey, PackageVersionKey>(0));
        }

        ValidateResolutionInput(package, resolutions);

        // Per-resource drop sets + rewrite maps. Build them by walking resolutions; apply to
        // the package collections afterward in a single pass so the order resolutions arrived
        // in doesn't affect the result.
        var droppedAgents = new HashSet<PackageVersionKey>();
        var droppedWorkflows = new HashSet<PackageVersionKey>();
        var droppedRoles = new HashSet<string>(StringComparer.Ordinal);
        var droppedSkills = new HashSet<string>(StringComparer.Ordinal);
        var droppedMcpServers = new HashSet<string>(StringComparer.Ordinal);
        var droppedAssignments = new HashSet<string>(StringComparer.Ordinal);

        // Ref rewrites: ORIGINAL identity (origKey, origVersion) → resolved identity
        // (newKey, newVersion). Used to rewrite both the package's own entity collections AND
        // every transitive ref that pointed at the original identity.
        var agentRewrites = new Dictionary<PackageVersionKey, PackageVersionKey>();
        var workflowRewrites = new Dictionary<PackageVersionKey, PackageVersionKey>();

        // Lineage keyed by RESOLVED identity (newKey, newVersion) → original (origKey,
        // origVersion). ImportAgentsAsync looks up by the agent row's own identity.
        var agentLineage = new Dictionary<PackageVersionKey, PackageVersionKey>();

        foreach (var resolution in resolutions.Values)
        {
            var target = resolution.Target;

            switch (resolution.Mode)
            {
                case WorkflowPackageImportResolutionMode.UseExisting:
                    switch (target.Kind)
                    {
                        case WorkflowPackageImportResourceKind.Agent:
                        {
                            var origin = new PackageVersionKey(target.Key, target.SourceVersion!.Value);
                            droppedAgents.Add(origin);
                            var existingMax = await MaxAgentVersionAsync(target.Key, cancellationToken)
                                ?? throw NewResolutionException(target,
                                    "UseExisting requires the library to already contain an agent with this key.");
                            agentRewrites[origin] = new PackageVersionKey(target.Key, existingMax);
                            // The library's existing role assignment for this agent stays —
                            // drop the package's assignment so PreviewAgentRoleAssignmentAsync
                            // doesn't compare it against unrelated existing rows.
                            droppedAssignments.Add(target.Key);
                            break;
                        }
                        case WorkflowPackageImportResourceKind.Workflow:
                        {
                            var origin = new PackageVersionKey(target.Key, target.SourceVersion!.Value);
                            droppedWorkflows.Add(origin);
                            var existingMax = await MaxWorkflowVersionAsync(target.Key, cancellationToken)
                                ?? throw NewResolutionException(target,
                                    "UseExisting requires the library to already contain a workflow with this key.");
                            workflowRewrites[origin] = new PackageVersionKey(target.Key, existingMax);
                            break;
                        }
                        case WorkflowPackageImportResourceKind.Role:
                            droppedRoles.Add(target.Key);
                            break;
                        case WorkflowPackageImportResourceKind.Skill:
                            // Skills are name-keyed; WorkflowPackageImportResolutionKey.Key
                            // carries the Name for this kind (mirrors WorkflowPackageImportItem).
                            droppedSkills.Add(target.Key);
                            break;
                        case WorkflowPackageImportResourceKind.McpServer:
                            droppedMcpServers.Add(target.Key);
                            break;
                        case WorkflowPackageImportResourceKind.AgentRoleAssignment:
                            droppedAssignments.Add(target.Key);
                            break;
                    }
                    break;

                case WorkflowPackageImportResolutionMode.Bump:
                {
                    var origin = new PackageVersionKey(target.Key, target.SourceVersion!.Value);
                    if (target.Kind == WorkflowPackageImportResourceKind.Agent)
                    {
                        var existingMax = await MaxAgentVersionAsync(target.Key, cancellationToken) ?? 0;
                        var bumped = new PackageVersionKey(target.Key, existingMax + 1);
                        agentRewrites[origin] = bumped;
                        agentLineage[bumped] = origin;
                    }
                    else
                    {
                        // Workflow
                        var existingMax = await MaxWorkflowVersionAsync(target.Key, cancellationToken) ?? 0;
                        var bumped = new PackageVersionKey(target.Key, existingMax + 1);
                        workflowRewrites[origin] = bumped;
                    }
                    break;
                }

                case WorkflowPackageImportResolutionMode.Copy:
                {
                    var origin = new PackageVersionKey(target.Key, target.SourceVersion!.Value);
                    var newKey = resolution.NewKey!;
                    var copy = new PackageVersionKey(newKey, 1);
                    if (target.Kind == WorkflowPackageImportResourceKind.Agent)
                    {
                        agentRewrites[origin] = copy;
                        agentLineage[copy] = origin;
                    }
                    else
                    {
                        // Workflow
                        workflowRewrites[origin] = copy;
                    }
                    break;
                }
            }
        }

        // Rewrite agents collection.
        var transformedAgents = new List<WorkflowPackageAgent>(package.Agents.Count);
        foreach (var agent in package.Agents)
        {
            var pvk = new PackageVersionKey(agent.Key, agent.Version);
            if (droppedAgents.Contains(pvk)) continue;
            if (agentRewrites.TryGetValue(pvk, out var resolved))
            {
                transformedAgents.Add(agent with { Key = resolved.Key, Version = resolved.Version });
            }
            else
            {
                transformedAgents.Add(agent);
            }
        }

        // Build a map from origAgentKey → newAgentKey for any Copy that renamed agents. Used
        // when rewriting role assignments so the new copy inherits the package's role grants.
        var copyAgentKeyRenames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (origin, resolved) in agentRewrites)
        {
            if (!string.Equals(origin.Key, resolved.Key, StringComparison.Ordinal))
            {
                copyAgentKeyRenames[origin.Key] = resolved.Key;
            }
        }

        // Rewrite workflows: filter dropped, rename Copy/Bump targets, rewrite every node ref.
        var transformedWorkflows = new List<WorkflowPackageWorkflow>(package.Workflows.Count);
        foreach (var workflow in package.Workflows)
        {
            var pvk = new PackageVersionKey(workflow.Key, workflow.Version);
            if (droppedWorkflows.Contains(pvk)) continue;
            var renamed = workflowRewrites.TryGetValue(pvk, out var resolved)
                ? workflow with { Key = resolved.Key, Version = resolved.Version }
                : workflow;
            transformedWorkflows.Add(renamed with
            {
                Nodes = renamed.Nodes
                    .Select(node => RewriteNodeRefs(node, agentRewrites, workflowRewrites))
                    .ToArray(),
            });
        }

        // Rewrite EntryPoint. UseExisting on the entry point is invalid (admission would
        // reject the dropped state); ValidateResolutionInput catches this, but a defensive
        // re-check here keeps the code symmetric.
        var entryPvk = new PackageVersionKey(package.EntryPoint.Key, package.EntryPoint.Version);
        if (droppedWorkflows.Contains(entryPvk))
        {
            throw new WorkflowPackageResolutionException(
                $"Resolution would drop the entry-point workflow '{package.EntryPoint.Key}' v{package.EntryPoint.Version}. "
                + "UseExisting is not valid for the entry point because the package's entry-point reference would no longer resolve.");
        }
        var newEntryPoint = workflowRewrites.TryGetValue(entryPvk, out var entryResolved)
            ? new WorkflowPackageReference(entryResolved.Key, entryResolved.Version)
            : package.EntryPoint;

        // Rewrite role assignments. UseExisting on an agent dropped its assignment up-front.
        // Copy renames the AgentKey on any matching assignment so the new copy inherits the
        // package's role grants.
        var transformedAssignments = new List<WorkflowPackageAgentRoleAssignment>(package.AgentRoleAssignments.Count);
        foreach (var assignment in package.AgentRoleAssignments)
        {
            if (droppedAssignments.Contains(assignment.AgentKey)) continue;
            var rewritten = copyAgentKeyRenames.TryGetValue(assignment.AgentKey, out var newAgentKey)
                ? assignment with { AgentKey = newAgentKey }
                : assignment;
            transformedAssignments.Add(rewritten);
        }

        var transformedRoles = droppedRoles.Count == 0
            ? package.Roles
            : package.Roles.Where(role => !droppedRoles.Contains(role.Key)).ToArray();
        var transformedSkills = droppedSkills.Count == 0
            ? package.Skills
            : package.Skills.Where(skill => !droppedSkills.Contains(skill.Name)).ToArray();
        var transformedMcpServers = droppedMcpServers.Count == 0
            ? package.McpServers
            : package.McpServers.Where(server => !droppedMcpServers.Contains(server.Key)).ToArray();

        var newManifest = package.Manifest is null
            ? null
            : RewriteManifest(
                package.Manifest,
                agentRewrites,
                workflowRewrites,
                droppedAgents,
                droppedWorkflows,
                droppedRoles,
                droppedSkills,
                droppedMcpServers);

        var resolvedPackage = package with
        {
            EntryPoint = newEntryPoint,
            Workflows = transformedWorkflows.ToArray(),
            Agents = transformedAgents.ToArray(),
            AgentRoleAssignments = transformedAssignments.ToArray(),
            Roles = transformedRoles,
            Skills = transformedSkills,
            McpServers = transformedMcpServers,
            Manifest = newManifest,
        };

        return new ResolvedPackage(resolvedPackage, agentLineage);
    }

    private static void ValidateResolutionInput(
        WorkflowPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution> resolutions)
    {
        // Indexes for "does this target exist in the package?" checks.
        var packageAgentIds = package.Agents
            .Select(agent => new PackageVersionKey(agent.Key, agent.Version))
            .ToHashSet();
        var packageWorkflowIds = package.Workflows
            .Select(workflow => new PackageVersionKey(workflow.Key, workflow.Version))
            .ToHashSet();
        var packageRoleKeys = package.Roles.Select(role => role.Key).ToHashSet(StringComparer.Ordinal);
        var packageSkillNames = package.Skills.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);
        var packageMcpServerKeys = package.McpServers.Select(server => server.Key).ToHashSet(StringComparer.Ordinal);
        var packageAssignmentAgentKeys = package.AgentRoleAssignments
            .Select(assignment => assignment.AgentKey)
            .ToHashSet(StringComparer.Ordinal);

        var newKeySeen = new Dictionary<WorkflowPackageImportResourceKind, HashSet<string>>();

        foreach (var (key, resolution) in resolutions)
        {
            if (!ReferenceEquals(key, resolution.Target) && !key.Equals(resolution.Target))
            {
                throw NewResolutionException(key,
                    "Dictionary key does not match resolution.Target. The dictionary should be keyed by the resolution's Target.");
            }

            var target = resolution.Target;
            if (string.IsNullOrWhiteSpace(target.Key))
            {
                throw NewResolutionException(target, "Target.Key is required.");
            }

            // Versioned-kind invariants.
            var isVersionedKind =
                target.Kind == WorkflowPackageImportResourceKind.Agent ||
                target.Kind == WorkflowPackageImportResourceKind.Workflow;
            if (isVersionedKind && target.SourceVersion is null)
            {
                throw NewResolutionException(target,
                    $"SourceVersion is required for {target.Kind} resolutions.");
            }
            if (!isVersionedKind && target.SourceVersion is not null)
            {
                throw NewResolutionException(target,
                    $"SourceVersion must be null for {target.Kind} resolutions.");
            }

            // Mode-kind compatibility.
            switch (resolution.Mode)
            {
                case WorkflowPackageImportResolutionMode.UseExisting:
                    if (!string.IsNullOrEmpty(resolution.NewKey))
                    {
                        throw NewResolutionException(target,
                            "NewKey must be null for UseExisting resolutions.");
                    }
                    break;
                case WorkflowPackageImportResolutionMode.Bump:
                    if (!isVersionedKind)
                    {
                        throw NewResolutionException(target,
                            $"Bump is only valid for Agent or Workflow; not for {target.Kind}.");
                    }
                    if (!string.IsNullOrEmpty(resolution.NewKey))
                    {
                        throw NewResolutionException(target,
                            "NewKey must be null for Bump resolutions.");
                    }
                    break;
                case WorkflowPackageImportResolutionMode.Copy:
                    if (!isVersionedKind)
                    {
                        throw NewResolutionException(target,
                            $"Copy is only valid for Agent or Workflow; not for {target.Kind}.");
                    }
                    if (string.IsNullOrWhiteSpace(resolution.NewKey))
                    {
                        throw NewResolutionException(target,
                            "NewKey is required for Copy resolutions.");
                    }
                    if (string.Equals(resolution.NewKey, target.Key, StringComparison.Ordinal))
                    {
                        throw NewResolutionException(target,
                            "Copy.NewKey must differ from the original Key.");
                    }
                    var seen = newKeySeen.GetOrCreate(target.Kind);
                    if (!seen.Add(resolution.NewKey!))
                    {
                        throw NewResolutionException(target,
                            $"Copy.NewKey '{resolution.NewKey}' is used by more than one resolution; each Copy must produce a distinct new key per kind.");
                    }
                    break;
            }

            // Target-existence checks against the un-resolved package.
            switch (target.Kind)
            {
                case WorkflowPackageImportResourceKind.Agent:
                    if (!packageAgentIds.Contains(new PackageVersionKey(target.Key, target.SourceVersion!.Value)))
                    {
                        throw NewResolutionException(target,
                            "Resolution target does not exist in the package's Agents collection.");
                    }
                    break;
                case WorkflowPackageImportResourceKind.Workflow:
                    if (!packageWorkflowIds.Contains(new PackageVersionKey(target.Key, target.SourceVersion!.Value)))
                    {
                        throw NewResolutionException(target,
                            "Resolution target does not exist in the package's Workflows collection.");
                    }
                    if (resolution.Mode == WorkflowPackageImportResolutionMode.UseExisting
                        && string.Equals(target.Key, package.EntryPoint.Key, StringComparison.Ordinal)
                        && target.SourceVersion!.Value == package.EntryPoint.Version)
                    {
                        throw NewResolutionException(target,
                            "UseExisting is not valid for the entry-point workflow.");
                    }
                    break;
                case WorkflowPackageImportResourceKind.Role:
                    if (!packageRoleKeys.Contains(target.Key))
                    {
                        throw NewResolutionException(target,
                            "Resolution target does not exist in the package's Roles collection.");
                    }
                    break;
                case WorkflowPackageImportResourceKind.Skill:
                    if (!packageSkillNames.Contains(target.Key))
                    {
                        throw NewResolutionException(target,
                            "Resolution target does not exist in the package's Skills collection.");
                    }
                    break;
                case WorkflowPackageImportResourceKind.McpServer:
                    if (!packageMcpServerKeys.Contains(target.Key))
                    {
                        throw NewResolutionException(target,
                            "Resolution target does not exist in the package's McpServers collection.");
                    }
                    break;
                case WorkflowPackageImportResourceKind.AgentRoleAssignment:
                    if (!packageAssignmentAgentKeys.Contains(target.Key))
                    {
                        throw NewResolutionException(target,
                            "Resolution target does not exist in the package's AgentRoleAssignments collection.");
                    }
                    break;
            }
        }
    }

    private static WorkflowPackageResolutionException NewResolutionException(
        WorkflowPackageImportResolutionKey target,
        string detail)
    {
        var versionPart = target.SourceVersion is int v ? $" v{v}" : string.Empty;
        return new WorkflowPackageResolutionException(
            $"Invalid resolution for {target.Kind} '{target.Key}'{versionPart}: {detail}");
    }

    private static WorkflowPackageWorkflowNode RewriteNodeRefs(
        WorkflowPackageWorkflowNode node,
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> agentRewrites,
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> workflowRewrites)
    {
        // Apply resolution rewrites to all four agent slots (the generic AgentKey plus the
        // three Swarm slots — Contributor, Synthesizer, Coordinator) plus the SubflowKey slot.
        // Pre-existing limitation worth noting: the planner's auto-bump pass only walks the
        // generic AgentKey + SubflowKey via RemapWorkflow, so a Swarm-slot agent that auto-
        // bumps because content differs would still be left with a stale ref. That's a wider
        // fix; resolution-driven rewrites at least handle the Bump/Copy paths correctly here.
        var (agentKey, agentVersion) = ResolveAgentSlot(node.AgentKey, node.AgentVersion, agentRewrites);
        var (contributorKey, contributorVersion) = ResolveAgentSlot(node.ContributorAgentKey, node.ContributorAgentVersion, agentRewrites);
        var (synthesizerKey, synthesizerVersion) = ResolveAgentSlot(node.SynthesizerAgentKey, node.SynthesizerAgentVersion, agentRewrites);
        var (coordinatorKey, coordinatorVersion) = ResolveAgentSlot(node.CoordinatorAgentKey, node.CoordinatorAgentVersion, agentRewrites);
        var (subflowKey, subflowVersion) = ResolveAgentSlot(node.SubflowKey, node.SubflowVersion, workflowRewrites);

        return node with
        {
            AgentKey = agentKey,
            AgentVersion = agentVersion,
            ContributorAgentKey = contributorKey,
            ContributorAgentVersion = contributorVersion,
            SynthesizerAgentKey = synthesizerKey,
            SynthesizerAgentVersion = synthesizerVersion,
            CoordinatorAgentKey = coordinatorKey,
            CoordinatorAgentVersion = coordinatorVersion,
            SubflowKey = subflowKey,
            SubflowVersion = subflowVersion,
        };
    }

    private static (string? Key, int? Version) ResolveAgentSlot(
        string? key,
        int? version,
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> rewrites)
    {
        if (string.IsNullOrWhiteSpace(key) || version is null)
        {
            return (key, version);
        }
        return rewrites.TryGetValue(new PackageVersionKey(key, version.Value), out var resolved)
            ? (resolved.Key, resolved.Version)
            : (key, version);
    }

    private static WorkflowPackageManifest RewriteManifest(
        WorkflowPackageManifest manifest,
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> agentRewrites,
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> workflowRewrites,
        IReadOnlySet<PackageVersionKey> droppedAgents,
        IReadOnlySet<PackageVersionKey> droppedWorkflows,
        IReadOnlySet<string> droppedRoles,
        IReadOnlySet<string> droppedSkills,
        IReadOnlySet<string> droppedMcpServers) =>
        manifest with
        {
            Workflows = manifest.Workflows
                .Where(reference => !droppedWorkflows.Contains(new PackageVersionKey(reference.Key, reference.Version)))
                .Select(reference =>
                {
                    var pvk = new PackageVersionKey(reference.Key, reference.Version);
                    return workflowRewrites.TryGetValue(pvk, out var resolved)
                        ? new WorkflowPackageReference(resolved.Key, resolved.Version)
                        : reference;
                })
                .ToArray(),
            Agents = manifest.Agents
                .Where(reference => !droppedAgents.Contains(new PackageVersionKey(reference.Key, reference.Version)))
                .Select(reference =>
                {
                    var pvk = new PackageVersionKey(reference.Key, reference.Version);
                    return agentRewrites.TryGetValue(pvk, out var resolved)
                        ? new WorkflowPackageReference(resolved.Key, resolved.Version)
                        : reference;
                })
                .ToArray(),
            Roles = manifest.Roles.Where(roleKey => !droppedRoles.Contains(roleKey)).ToArray(),
            Skills = manifest.Skills.Where(skillName => !droppedSkills.Contains(skillName)).ToArray(),
            McpServers = manifest.McpServers.Where(serverKey => !droppedMcpServers.Contains(serverKey)).ToArray(),
        };

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
                TagsJson = WorkflowJson.SerializeTags(TagNormalizer.Normalize(role.Tags)),
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
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> agentLineage,
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

                // sc-394: Bump and Copy resolutions stamp ForkedFromKey/Version on the new
                // row so (origKey, origVersion) → (resolvedKey, resolvedVersion) provenance is
                // queryable forever — same lineage shape that `__fork_*` agents already use
                // via AgentConfigRepository.CreateForkAsync / CreatePublishedVersionAsync.
                // UseExisting writes no row (the agent is dropped from the package) so it
                // never reaches this branch; agentLineage carries no entry for it.
                var lineage = agentLineage.TryGetValue(
                    new PackageVersionKey(agent.Key, agent.Version),
                    out var origin)
                    ? origin
                    : (PackageVersionKey?)null;

                dbContext.Agents.Add(new AgentConfigEntity
                {
                    Key = agent.Key.Trim(),
                    Version = agent.Version,
                    ConfigJson = agent.Config?.ToJsonString(SerializerOptions) ?? "{}",
                    CreatedAtUtc = UsePackageDateOrNow(agent.CreatedAtUtc, now),
                    CreatedBy = Trim(agent.CreatedBy),
                    IsActive = agent.Version == finalMaxVersion,
                    IsRetired = false,
                    TagsJson = WorkflowJson.SerializeTags(TagNormalizer.Normalize(agent.Tags)),
                    ForkedFromKey = lineage?.Key,
                    ForkedFromVersion = lineage?.Version,
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
                TagsJson = WorkflowJson.SerializeTags(TagNormalizer.Normalize(workflow.Tags)),
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

    private static WorkflowPackageImportItem Refused(
        WorkflowPackageImportResourceKind kind,
        string key,
        int? sourceVersion,
        int? existingMaxVersion,
        string message) =>
        new(
            kind,
            key,
            existingMaxVersion,
            WorkflowPackageImportAction.Refused,
            message,
            SourceVersion: sourceVersion,
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
        TagNormalizer.Normalize(packageAgent.Tags).SequenceEqual(existing.TagsOrEmpty, StringComparer.Ordinal) &&
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
        TagNormalizer.Normalize(packageRole.Tags).SequenceEqual(existing.TagsOrEmpty, StringComparer.Ordinal) &&
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
        Tags = TagNormalizer.Normalize(workflow.Tags),
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
        Tags = TagNormalizer.Normalize(agent.Tags),
    };

    private static WorkflowPackageAgentRoleAssignment NormalizeAssignment(WorkflowPackageAgentRoleAssignment assignment) => assignment with
    {
        RoleKeys = assignment.RoleKeys ?? Array.Empty<string>(),
    };

    private static WorkflowPackageRole NormalizeRole(WorkflowPackageRole role) => role with
    {
        ToolGrants = role.ToolGrants ?? Array.Empty<WorkflowPackageRoleGrant>(),
        SkillNames = role.SkillNames ?? Array.Empty<string>(),
        Tags = TagNormalizer.Normalize(role.Tags),
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
        WorkflowPackageImportPreview Preview,
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> AgentLineage);

    private sealed record ResolvedPackage(
        WorkflowPackage Package,
        IReadOnlyDictionary<PackageVersionKey, PackageVersionKey> AgentLineage);
}

internal static class WorkflowPackageImporterCollectionExtensions
{
    public static HashSet<string> GetOrCreate<TKey>(
        this Dictionary<TKey, HashSet<string>> dictionary,
        TKey key)
        where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            dictionary[key] = set;
        }
        return set;
    }
}
