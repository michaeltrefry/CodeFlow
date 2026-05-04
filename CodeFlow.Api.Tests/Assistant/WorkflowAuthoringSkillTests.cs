using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Api.Assistant.Skills;
using CodeFlow.Api.WorkflowPackages;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Structural lints for the embedded `workflow-authoring` skill (AS-3). These tests guard
/// the curated body against silent regression in two dimensions:
/// 1. Every gotcha / vocabulary token the legacy curated prompt taught is still present in
///    the migrated skill — the model must not lose authoring competence in the move.
/// 2. The canonical-shape JSON exemplar at the end of the body deserializes cleanly into a
///    <see cref="WorkflowPackage"/> via the same options the API uses on import, and covers
///    every key node kind the assistant might emit.
/// </summary>
public sealed class WorkflowAuthoringSkillTests
{
    private const string SkillKey = "workflow-authoring";

    /// <summary>
    /// Mirrors the API's request-body deserializer (<c>JsonOptions</c> in
    /// <c>ApiServiceCollectionExtensions</c>): web defaults + string enum converter. The
    /// exemplar's enum values (<c>"Start"</c>, <c>"Workflow"</c>, <c>"Text"</c>, etc.) only
    /// round-trip with the converter installed.
    /// </summary>
    private static readonly JsonSerializerOptions DeserializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static AssistantSkill LoadSkill()
    {
        var provider = new EmbeddedAssistantSkillProvider();
        var skill = provider.Get(SkillKey);
        skill.Should().NotBeNull(
            because: $"the embedded resource pipeline must surface '{SkillKey}.md' as a registered skill");
        return skill!;
    }

    /// <summary>
    /// Pull out the contents of the LAST <c>```cf-workflow-package</c> fenced block. The skill
    /// body convention places the canonical exemplar at the end; earlier occurrences (e.g. in
    /// the "Emission contract" section showing the LLM how to emit a draft) are illustrative
    /// shells with `...` placeholders and would not parse as JSON.
    /// </summary>
    private static string ExtractExemplarJson(string body)
    {
        const string OpenFence = "```cf-workflow-package";
        const string CloseFence = "```";

        var openIndex = body.LastIndexOf(OpenFence, StringComparison.Ordinal);
        openIndex.Should().BeGreaterThan(0,
            because: "the skill body must end with a `cf-workflow-package` exemplar");

        var jsonStart = body.IndexOf('\n', openIndex);
        jsonStart.Should().BeGreaterThan(0);
        jsonStart++;

        var closeIndex = body.IndexOf(CloseFence, jsonStart, StringComparison.Ordinal);
        closeIndex.Should().BeGreaterThan(jsonStart, because: "exemplar fence must close");

        return body[jsonStart..closeIndex];
    }

    [Fact]
    public void Skill_ExposesExpectedFrontmatter()
    {
        var skill = LoadSkill();

        skill.Key.Should().Be("workflow-authoring");
        skill.Name.Should().NotBeNullOrWhiteSpace();
        skill.Description.Should().NotBeNullOrWhiteSpace();
        skill.Trigger.Should().NotBeNullOrWhiteSpace();
        skill.Body.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    // Authoring vocabulary anchors — these were lints on the legacy prompt and must survive
    // the migration into this skill. The "Workflows are data, not source code" guardrail lives
    // in the base prompt (see AssistantSystemPromptTests), not here — it's an always-on rule,
    // not authoring-specific.
    [InlineData("partialPins")]
    [InlineData("@codeflow/reviewer-base")]
    [InlineData("@codeflow/producer-base")]
    [InlineData("rejectionHistory")]
    [InlineData("mirrorOutputToWorkflowVar")]
    [InlineData("outputPortReplacements")]
    [InlineData("setWorkflow")]
    [InlineData("setContext")]
    [InlineData("setOutput")]
    [InlineData("setInput")]
    [InlineData("setNodePath")]
    [InlineData("Subflow")]
    [InlineData("ReviewLoop")]
    [InlineData("Swarm")]
    [InlineData("Transform")]
    [InlineData("Hitl")]
    [InlineData("workflow` bag")]
    [InlineData("context` bag")]
    // Sub-agents primitive (sc-571): the skill must disambiguate it from Swarm so the model
    // doesn't suggest a node when the user asks for "a developer agent that delegates to
    // helpers" — that's runtime fan-out from one agent's reasoning, not a workflow-design-time
    // protocol.
    [InlineData("subAgents")]
    [InlineData("spawn_subagent")]
    [InlineData("anonymous worker")]
    public void Skill_TeachesAuthoringVocabulary(string token)
    {
        LoadSkill().Body.Should().Contain(token);
    }

    [Theory]
    // Save / package validators that show up in error responses and must be cited explicitly
    // when the model explains a save rejection.
    [InlineData("port-coupling")]
    [InlineData("missing-role")]
    [InlineData("prompt-lint")]
    [InlineData("protected-variable-target")]
    [InlineData("package-node-missing-agent-version")]
    [InlineData("package-node-missing-subflow-version")]
    public void Skill_CitesValidatorRuleIds(string ruleId)
    {
        LoadSkill().Body.Should().Contain(ruleId);
    }

    [Theory]
    // Common shape gotchas (#198) — silent shape drift here costs the user a save round-trip,
    // so the migrated skill must keep teaching every one.
    [InlineData("config.outputs")]
    [InlineData("toolGrants")]
    [InlineData("\"category\": \"Host\"|\"Mcp\"")]
    [InlineData("agents[].tags[]")]
    [InlineData("roles[].tags[]")]
    [InlineData("schemaVersion")]
    [InlineData("codeflow.workflow-package.v1")]
    [InlineData("entryPoint")]
    [InlineData("agentRoleAssignments")]
    [InlineData("Embedding rule")]
    [InlineData("cf-workflow-package")]
    // Field-name and id-shape gotchas — observed real-world spirals from sessions where the
    // model defaulted to JSON Schema dialects (`$schema`) or to slug-style ids.
    [InlineData("$schema")]
    [InlineData("MUST be a fresh GUID")]
    public void Skill_PinsShapeGotchasAndEmissionContract(string token)
    {
        LoadSkill().Body.Should().Contain(token);
    }

    [Theory]
    // save_workflow_package result branches — every shape the LLM may see and must handle
    // distinctly.
    [InlineData("preview_ok")]
    [InlineData("preview_conflicts")]
    [InlineData("status: \"invalid\"")]
    [InlineData("almost always empty")]
    [InlineData("\"error\":")]
    [InlineData("never deltas")]
    public void Skill_DescribesSaveResultBranches(string token)
    {
        LoadSkill().Body.Should().Contain(token);
    }

    [Fact]
    public void Skill_DescribesRepositoriesBagRouting()
    {
        // sc-607: declaring `repositories` as a workflow input routes the resolved value into
        // the workflow.* bag (not context.*), and the saga lifts it to a typed RepositoriesJson
        // field that backs the vcs_* tool allowlist. The skill must affirmatively teach the
        // workflow.* bag location and the canonical workspace path. Negative-pedagogy mentions
        // of context.repositories or setContext('repositories', ...) are allowed; what we pin
        // here is that the *positive* surface is named correctly.
        var body = LoadSkill().Body;
        body.Should().Contain("workflow.repositories",
            "post sc-607 the per-trace VCS allowlist lives on workflow.*, not context.*");
        body.Should().Contain("traceWorkDir",
            "framework-managed workspace path is workflow.traceWorkDir");
        body.Should().Contain("setWorkflow",
            "the runtime mutation surface for the allowlist is setWorkflow");
    }

    [Fact]
    public void Skill_DoesNotPromoteLegacyWorkDir()
    {
        // sc-604 retired `workflow.workDir`; the skill teaches `traceWorkDir` only.
        LoadSkill().Body.Should().NotContain("workflow.workDir",
            "workflow.workDir is gone post sc-604");
    }

    [Fact]
    public void Skill_DescribesVZ2AsOptInWarning()
    {
        // VZ2 (workflow-vars-declaration) is a save-time linting layer with two important
        // qualifiers: it's opt-in (skipped entirely when the workflow declares neither
        // workflowVarsReads nor workflowVarsWrites) and every finding is a Warning, not an
        // Error (warnings never block save). A future skill edit that drops either qualifier
        // would lead the assistant to over-prescribe declarations as a hard requirement —
        // which they aren't. Pin both phrases on the same line as the rule id.
        var body = LoadSkill().Body;
        var vz2Line = body.Split('\n')
            .FirstOrDefault(line => line.Contains("workflow-vars-declaration"));
        vz2Line.Should().NotBeNull("the skill must mention VZ2 by its rule id");
        vz2Line!.Should().Contain("Warning",
            "VZ2 findings are Warnings, never Errors — the skill must not imply they block save");
        vz2Line.Should().Contain("opt-in",
            "VZ2 is opt-in (skipped when the workflow has no declared reads/writes); the skill must not imply it always fires");
    }

    [Fact]
    public void Skill_DoesNotImplyImporterIgnoresDb()
    {
        // The earlier "the importer does not resolve from the DB" wording contradicted the
        // resolver, which DOES look up unembedded refs against the local DB. Forbid the
        // misleading sentence so we don't regress.
        LoadSkill().Body.Should().NotContain("importer does not resolve from the DB");
    }

    [Fact]
    public void Exemplar_DeserializesCleanlyAsWorkflowPackage()
    {
        var json = ExtractExemplarJson(LoadSkill().Body);

        var package = JsonSerializer.Deserialize<WorkflowPackage>(json, DeserializerOptions);

        package.Should().NotBeNull();
        package!.SchemaVersion.Should().Be("codeflow.workflow-package.v1");
        package.EntryPoint.Should().NotBeNull();
        package.Workflows.Should().NotBeEmpty();
        package.Agents.Should().NotBeEmpty();

        // Entry point must appear in workflows[]; anything else fails admission.
        package.Workflows.Should().Contain(w =>
            w.Key == package.EntryPoint.Key && w.Version == package.EntryPoint.Version);
    }

    [Fact]
    public void Exemplar_CoversEveryRequiredNodeKind()
    {
        var json = ExtractExemplarJson(LoadSkill().Body);
        var package = JsonSerializer.Deserialize<WorkflowPackage>(json, DeserializerOptions)!;

        var allKinds = package.Workflows
            .SelectMany(w => w.Nodes)
            .Select(n => n.Kind)
            .Distinct()
            .ToHashSet();

        // Acceptance criterion: at minimum Start + Agent + ReviewLoop + Subflow + Hitl. The
        // exemplar must be expressive enough to teach the model the full shape grammar even
        // when the user's library is empty.
        allKinds.Should().Contain(CodeFlow.Persistence.WorkflowNodeKind.Start);
        allKinds.Should().Contain(CodeFlow.Persistence.WorkflowNodeKind.Agent);
        allKinds.Should().Contain(CodeFlow.Persistence.WorkflowNodeKind.ReviewLoop);
        allKinds.Should().Contain(CodeFlow.Persistence.WorkflowNodeKind.Hitl);
    }

    [Fact]
    public void Exemplar_AgentsHaveConfigOutputsArrayAndPinnedVersions()
    {
        var json = ExtractExemplarJson(LoadSkill().Body);
        var package = JsonSerializer.Deserialize<WorkflowPackage>(json, DeserializerOptions)!;

        // Every agent the exemplar embeds must show config.outputs[] (the canonical port
        // declaration the importer reads) — otherwise the model is being shown a shape that
        // the validator rejects on port-coupling.
        foreach (var agent in package.Agents)
        {
            agent.Version.Should().BeGreaterThan(0,
                because: $"agent '{agent.Key}' must carry a concrete pinned version");
            agent.Tags.Should().NotBeEmpty(
                because: $"agent '{agent.Key}' should show package-portable browse tags");
            agent.Config.Should().NotBeNull(
                because: $"agent '{agent.Key}' must show a config blob in the exemplar");

            var configOutputs = agent.Config!["outputs"];
            configOutputs.Should().NotBeNull(
                because: $"agent '{agent.Key}' must have config.outputs[] — port-coupling reads from this");
            configOutputs!.AsArray().Count.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Exemplar_RolesCarryTags()
    {
        var json = ExtractExemplarJson(LoadSkill().Body);
        var package = JsonSerializer.Deserialize<WorkflowPackage>(json, DeserializerOptions)!;

        package.Roles.Should().NotBeEmpty(because: "the exemplar must include at least one role to teach toolGrants[] shape");
        package.Roles.Should().AllSatisfy(role =>
            role.Tags.Should().NotBeEmpty(because: "role tags are portable package metadata"));
    }

    [Fact]
    public void Exemplar_HasRoleWithBothHostAndMcpGrants()
    {
        var json = ExtractExemplarJson(LoadSkill().Body);
        var package = JsonSerializer.Deserialize<WorkflowPackage>(json, DeserializerOptions)!;

        package.Roles.Should().NotBeEmpty(because: "the exemplar must include at least one role to teach toolGrants[] shape");

        var grants = package.Roles.SelectMany(r => r.ToolGrants).ToArray();
        grants.Should().Contain(g => g.Category == CodeFlow.Persistence.AgentRoleToolCategory.Host,
            because: "the role exemplar must show a Host grant");
        grants.Should().Contain(g => g.Category == CodeFlow.Persistence.AgentRoleToolCategory.Mcp,
            because: "the role exemplar must show an Mcp grant");
    }

    [Fact]
    public void Exemplar_AgentVersionPinsAreNonZeroEverywhere()
    {
        var json = ExtractExemplarJson(LoadSkill().Body);
        var package = JsonSerializer.Deserialize<WorkflowPackage>(json, DeserializerOptions)!;

        // Package admission rejects any agent-bearing node with null/0 agentVersion. Verify
        // every node in the exemplar pins a concrete integer so the model learns the right
        // shape.
        foreach (var workflow in package.Workflows)
        {
            foreach (var node in workflow.Nodes)
            {
                if (node.Kind is CodeFlow.Persistence.WorkflowNodeKind.Start
                    or CodeFlow.Persistence.WorkflowNodeKind.Agent
                    or CodeFlow.Persistence.WorkflowNodeKind.Hitl)
                {
                    node.AgentKey.Should().NotBeNullOrWhiteSpace(
                        because: $"node {node.Id} ({node.Kind}) must reference an agent key");
                    node.AgentVersion.Should().NotBeNull(
                        because: $"node {node.Id} ({node.Kind}) must pin a concrete agent version");
                    node.AgentVersion!.Value.Should().BeGreaterThan(0);
                }

                if (node.Kind is CodeFlow.Persistence.WorkflowNodeKind.Subflow
                    or CodeFlow.Persistence.WorkflowNodeKind.ReviewLoop)
                {
                    node.SubflowKey.Should().NotBeNullOrWhiteSpace(
                        because: $"node {node.Id} ({node.Kind}) must reference a subflow key");
                    node.SubflowVersion.Should().NotBeNull(
                        because: $"node {node.Id} ({node.Kind}) must pin a concrete subflow version");
                    node.SubflowVersion!.Value.Should().BeGreaterThan(0);
                }
            }
        }
    }

    [Theory]
    // Code-aware authoring lessons (epic 658 follow-up). These are durable design rules the
    // skill must teach — losing them causes the assistant to emit dev-flow workflows that
    // complete all their LLM work and then fail at the publish step. Add to this list as new
    // failure modes are discovered.
    [InlineData("Push on first commit")]
    [InlineData("git push -u origin <featureBranch>")]
    [InlineData("git ls-remote")]
    [InlineData("never silently default to")]
    [InlineData("credential helper")]
    [InlineData("vcs.clone")]
    [InlineData("vcs.open_pr")]
    [InlineData("docs/code-aware-workflows.md")]
    public void Skill_TeachesCodeAwareWorkflowPatterns(string token)
    {
        LoadSkill().Body.Should().Contain(token,
            because: "code-aware workflow design patterns must stay in the skill — losing them strands real workflows at publish-time");
    }

    [Fact]
    public void Exemplar_DemonstratesDeclarativeAuthoringFeatures()
    {
        var json = ExtractExemplarJson(LoadSkill().Body);

        // The exemplar should demonstrate the declarative features the prose recommends —
        // P3 rejection-history, P4 mirror, P5 port-keyed replacement — so the model has a
        // concrete shape reference, not just a description.
        json.Should().Contain("rejectionHistory");
        json.Should().Contain("mirrorOutputToWorkflowVar");
        json.Should().Contain("outputPortReplacements");
        json.Should().Contain("partialPins");
    }
}
