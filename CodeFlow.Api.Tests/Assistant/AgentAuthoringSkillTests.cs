using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Api.Assistant.Skills;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Structural lints for the embedded `agent-authoring` skill (AP-5 / sc-836). Guards the
/// curated body against silent regression on two axes:
/// 1. Every vocabulary token / gotcha the assistant needs to author an agent package
///    correctly is present.
/// 2. The canonical exemplar at the end of the body deserializes cleanly into an
///    <see cref="AgentPackage"/> using the same options the API's import path uses.
/// </summary>
public sealed class AgentAuthoringSkillTests
{
    private const string SkillKey = "agent-authoring";

    /// <summary>
    /// Mirrors the API's request-body deserializer (web defaults + string enum converter).
    /// The exemplar's enum values (`"Agent"`, `"Host"`, `"Mcp"`, `"HttpSse"`, etc.) only
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

    private static string ExtractExemplarJson(string body)
    {
        const string OpenFence = "```cf-agent-package";
        const string CloseFence = "```";

        var openIndex = body.LastIndexOf(OpenFence, StringComparison.Ordinal);
        openIndex.Should().BeGreaterThan(0,
            because: "the skill body must end with a `cf-agent-package` exemplar");

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

        skill.Key.Should().Be("agent-authoring");
        skill.Name.Should().NotBeNullOrWhiteSpace();
        skill.Description.Should().NotBeNullOrWhiteSpace();
        skill.Trigger.Should().NotBeNullOrWhiteSpace();
        skill.Body.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    // Vocabulary anchors — the model needs every term to author an agent package correctly.
    [InlineData("agentRoleAssignments")]
    [InlineData("toolGrants")]
    [InlineData("skillNames")]
    [InlineData("mcpServers")]
    [InlineData("toolIdentifier")]
    [InlineData("partialPins")]
    [InlineData("budget")]
    [InlineData("submit")]
    [InlineData("setWorkflow")]
    [InlineData("setContext")]
    [InlineData("forkedFromKey")]
    [InlineData("hasBearerToken")]
    [InlineData("HttpSse")]
    [InlineData("Hitl")]
    public void Skill_TeachesAuthoringVocabulary(string token)
    {
        var skill = LoadSkill();
        skill.Body.Should().Contain(token);
    }

    [Theory]
    // Tool-precedence-first contract: the skill MUST nudge the model to call the recording
    // tools rather than emit JSON in fenced blocks. Per `feedback_skill_artifact_tool_precedence` —
    // models read "emission contract" wording as canonical action and skip the tool calls.
    [InlineData("set_agent_package_draft")]
    [InlineData("patch_agent_package_draft")]
    [InlineData("get_agent_package_draft")]
    [InlineData("clear_agent_package_draft")]
    [InlineData("save_agent_package")]
    public void Skill_ReferencesEveryDraftToolByName(string toolName)
    {
        var skill = LoadSkill();
        skill.Body.Should().Contain(toolName);
    }

    [Fact]
    public void Skill_PrescribesToolFirst_NotFencedEmission()
    {
        var skill = LoadSkill();

        // Explicit "do not paste / emit / re-emit" guidance somewhere in the body.
        skill.Body.Should().MatchRegex(
            @"(?i)do not (paste|emit|re-emit|write)",
            because: "the skill must steer the model away from fenced-block emission as the primary path");

        // The exemplar should be self-labeled as reference-only.
        skill.Body.Should().Contain("reference only", because: "label the exemplar so the model treats it as shape reference, not an emission contract");
    }

    [Theory]
    // Shape gotchas the importer enforces. Without these the model will guess a wrong field
    // name and bounce off admission.
    [InlineData("schemaVersion")]
    [InlineData("codeflow.agent-package.v1")]
    [InlineData("entryPoint")]
    [InlineData("agentVersion")]
    [InlineData("mcp:<server_key>:<tool_name>")]
    [InlineData("manifest.agent")]
    public void Skill_PinsShapeGotchas(string token)
    {
        var skill = LoadSkill();
        skill.Body.Should().Contain(token);
    }

    [Theory]
    // Save-result branches — every shape `save_agent_package` returns must be named so the
    // model knows how to react.
    [InlineData("preview_ok")]
    [InlineData("preview_conflicts")]
    [InlineData("invalid")]
    [InlineData("snapshotId")]
    [InlineData("missingReferences")]
    public void Skill_DescribesSaveResultBranches(string token)
    {
        var skill = LoadSkill();
        skill.Body.Should().Contain(token);
    }

    [Theory]
    // Conflict-resolution surface mirrors the imports page chip; the model must know the
    // three modes by name + when to default to waiting.
    [InlineData("UseExisting")]
    [InlineData("Bump")]
    [InlineData("Copy")]
    public void Skill_DescribesConflictResolutionModes(string mode)
    {
        var skill = LoadSkill();
        skill.Body.Should().Contain(mode);
    }

    [Fact]
    public void Skill_DescribesRedactionRecovery()
    {
        // Per `feedback_skill_artifact_tool_precedence` follow-up: the model that copies its
        // own redacted prior emission must be steered toward get_agent_package_draft + patch,
        // NOT toward re-emitting via set_agent_package_draft.
        var skill = LoadSkill();
        skill.Body.Should().Contain("_redacted");
        skill.Body.Should().Contain("get_agent_package_draft");
        skill.Body.Should().MatchRegex(
            @"(?is)recovery procedure.*get_agent_package_draft.*patch_agent_package_draft",
            because: "the recovery procedure must enumerate get → patch in order");
    }

    [Fact]
    public void Skill_DistinguishesItselfFromWorkflowAuthoringSkill()
    {
        // The two skills overlap on terminology; agent-authoring should explicitly say
        // "load workflow-authoring instead" for workflow-level work, otherwise the model
        // will try to author a workflow under this skill.
        var skill = LoadSkill();
        skill.Body.Should().Contain("workflow-authoring");
    }

    [Fact]
    public void Exemplar_DeserializesCleanlyAsAgentPackage()
    {
        var skill = LoadSkill();
        var exemplarJson = ExtractExemplarJson(skill.Body);

        var package = JsonSerializer.Deserialize<AgentPackage>(exemplarJson, DeserializerOptions);

        package.Should().NotBeNull();
        package!.SchemaVersion.Should().Be("codeflow.agent-package.v1");
        package.EntryPoint.Should().NotBeNull();
        package.Agents.Should().NotBeEmpty();
    }

    [Fact]
    public void Exemplar_EntryPointMatchesAnAgentRow()
    {
        var skill = LoadSkill();
        var package = JsonSerializer.Deserialize<AgentPackage>(
            ExtractExemplarJson(skill.Body), DeserializerOptions);

        package!.Agents.Should().Contain(agent =>
            agent.Key == package.EntryPoint.Key && agent.Version == package.EntryPoint.Version,
            because: "admission rejects packages whose entry point isn't in the agents collection");
    }

    [Fact]
    public void Exemplar_AgentVersionPinIsPositive()
    {
        var skill = LoadSkill();
        var package = JsonSerializer.Deserialize<AgentPackage>(
            ExtractExemplarJson(skill.Body), DeserializerOptions);

        package!.EntryPoint.Version.Should().BeGreaterThan(0,
            because: "agentVersion=0 is the unauthored placeholder; admission rejects it");
        foreach (var agent in package.Agents)
        {
            agent.Version.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Exemplar_AgentHasOutputsAndTags()
    {
        var skill = LoadSkill();
        var package = JsonSerializer.Deserialize<AgentPackage>(
            ExtractExemplarJson(skill.Body), DeserializerOptions);

        var entryAgent = package!.Agents.Single(a =>
            a.Key == package.EntryPoint.Key && a.Version == package.EntryPoint.Version);
        entryAgent.Outputs.Should().NotBeEmpty(
            because: "every port the agent submits to must be declared in outputs[]");
        entryAgent.Tags.Should().NotBeEmpty(
            because: "tags drive library browsing; an exemplar with no tags teaches the wrong thing");
    }

    [Fact]
    public void Exemplar_HasRoleWithBothHostAndMcpGrants()
    {
        var skill = LoadSkill();
        var package = JsonSerializer.Deserialize<AgentPackage>(
            ExtractExemplarJson(skill.Body), DeserializerOptions);

        package!.Roles.Should().Contain(
            role => role.ToolGrants.Any(g => g.Category == AgentRoleToolCategory.Host)
                 && role.ToolGrants.Any(g => g.Category == AgentRoleToolCategory.Mcp),
            because: "the exemplar must demonstrate the canonical Host + Mcp role shape so the " +
                     "model sees the toolIdentifier conventions for both categories");
    }

    [Fact]
    public void Exemplar_RolesCarryTags()
    {
        var skill = LoadSkill();
        var package = JsonSerializer.Deserialize<AgentPackage>(
            ExtractExemplarJson(skill.Body), DeserializerOptions);

        package!.Roles.Should().AllSatisfy(role => role.Tags.Should().NotBeEmpty(
            because: "the exemplar must teach that roles get tags too"));
    }

    [Fact]
    public void Exemplar_ManifestAgentMatchesEntryPoint()
    {
        var skill = LoadSkill();
        var package = JsonSerializer.Deserialize<AgentPackage>(
            ExtractExemplarJson(skill.Body), DeserializerOptions);

        package!.Manifest.Should().NotBeNull();
        package.Manifest!.Agent.Key.Should().Be(package.EntryPoint.Key);
        package.Manifest.Agent.Version.Should().Be(package.EntryPoint.Version);
    }

    [Fact]
    public void Exemplar_McpToolIdentifierFollowsCanonicalForm()
    {
        // Catch the common gotcha where the model writes `mcp/server/tool` or `server:tool`
        // instead of `mcp:<server>:<tool>` — the importer rejects everything else with a
        // `WorkflowPackageResolutionException`.
        var skill = LoadSkill();
        var package = JsonSerializer.Deserialize<AgentPackage>(
            ExtractExemplarJson(skill.Body), DeserializerOptions);

        var mcpGrants = package!.Roles
            .SelectMany(role => role.ToolGrants)
            .Where(g => g.Category == AgentRoleToolCategory.Mcp)
            .ToArray();
        mcpGrants.Should().NotBeEmpty(
            because: "the exemplar must demonstrate at least one MCP grant so the toolIdentifier shape is on display");
        mcpGrants.Should().AllSatisfy(grant =>
            grant.ToolIdentifier.Split(':').Length.Should().Be(3)
                .And.Be(3, "MCP grants follow `mcp:<server>:<tool>` exactly — three colon-separated segments"));
        mcpGrants.Should().AllSatisfy(grant =>
            grant.ToolIdentifier.Should().StartWith("mcp:"));
    }

    [Fact]
    public void Exemplar_McpServerEmbeddedForGrant()
    {
        // Self-containment rule: every Mcp toolGrant references a server that MUST be embedded.
        var skill = LoadSkill();
        var package = JsonSerializer.Deserialize<AgentPackage>(
            ExtractExemplarJson(skill.Body), DeserializerOptions);

        foreach (var role in package!.Roles)
        {
            foreach (var grant in role.ToolGrants.Where(g => g.Category == AgentRoleToolCategory.Mcp))
            {
                var serverKey = grant.ToolIdentifier.Split(':')[1];
                package.McpServers.Should().Contain(server => server.Key == serverKey,
                    because: $"role '{role.Key}' grants `{grant.ToolIdentifier}` so server '{serverKey}' must be embedded");
            }
        }
    }
}
