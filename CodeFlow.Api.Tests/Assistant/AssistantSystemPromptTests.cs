using CodeFlow.Api.Assistant;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// HAA-3 — System prompt + core knowledge layer. Beyond the manual-eval acceptance criterion
/// (the assistant correctly answers ~20 curated questions covering each concept), these tests
/// act as a structural lint: if a section is accidentally removed in a future PR, CI fails so
/// the omission is intentional rather than silent.
/// </summary>
public sealed class AssistantSystemPromptTests
{
    [Fact]
    public async Task DefaultProvider_ReturnsTheCuratedPrompt()
    {
        var provider = new DefaultAssistantSystemPromptProvider();
        var prompt = await provider.GetSystemPromptAsync();
        prompt.Should().Be(AssistantSystemPrompt.Default);
    }

    [Fact]
    public void DefaultPrompt_IsNonTrivialAndIdentifiesTheAssistant()
    {
        var prompt = AssistantSystemPrompt.Default;
        prompt.Length.Should().BeGreaterThan(2_000,
            because: "the curated prompt covers many concepts; a tiny prompt means a section was deleted");
        prompt.Should().Contain("CodeFlow assistant");
        prompt.Should().Contain("workflow-orchestration");
    }

    [Fact]
    public void DefaultPrompt_ReinforcesWorkflowsAreData()
    {
        // Critical reminder from project memory: the assistant should never tell users to edit
        // CodeFlow source code in order to author a workflow.
        AssistantSystemPrompt.Default.Should().Contain("Workflows are data");
    }

    [Theory]
    [InlineData("agents")]
    [InlineData("Scriban")]
    [InlineData("workflow")]
    [InlineData("Subflow")]
    [InlineData("ReviewLoop")]
    [InlineData("Swarm")]
    [InlineData("Transform")]
    [InlineData("HITL")]
    public void DefaultPrompt_CoversAuthoringPrimitives(string concept)
    {
        AssistantSystemPrompt.Default.Should().Contain(concept,
            because: $"acceptance criterion requires authoring concept '{concept}'");
    }

    [Theory]
    [InlineData("port")]
    [InlineData("Failed")]
    [InlineData("edge")]
    public void DefaultPrompt_CoversPortModel(string concept)
    {
        AssistantSystemPrompt.Default.Should().Contain(concept,
            because: $"acceptance criterion requires port-model concept '{concept}'");
    }

    [Theory]
    [InlineData("input script")]
    [InlineData("output script")]
    [InlineData("setInput")]
    [InlineData("setOutput")]
    [InlineData("setNodePath")]
    [InlineData("setWorkflow")]
    [InlineData("setContext")]
    [InlineData("1 MiB")]
    public void DefaultPrompt_CoversScriptingPrimitives(string concept)
    {
        AssistantSystemPrompt.Default.Should().Contain(concept,
            because: $"acceptance criterion requires scripting concept '{concept}'");
    }

    [Theory]
    [InlineData("workflow` bag")]
    [InlineData("context` bag")]
    [InlineData("submit")]
    [InlineData("@codeflow/reviewer-base")]
    [InlineData("@codeflow/producer-base")]
    [InlineData("partialPins")]
    [InlineData("rejectionHistory")]
    [InlineData("mirrorOutputToWorkflowVar")]
    [InlineData("outputPortReplacements")]
    public void DefaultPrompt_CoversAuthoringDeclarativeFeatures(string concept)
    {
        AssistantSystemPrompt.Default.Should().Contain(concept,
            because: $"the curated prompt must teach the {concept} primitive — it shipped and is preferred over hand-rolled scripts");
    }

    [Theory]
    [InlineData("port-coupling")]
    [InlineData("missing-role")]
    [InlineData("prompt-lint")]
    [InlineData("protected-variable-target")]
    public void DefaultPrompt_CitesValidatorRuleIds(string ruleId)
    {
        AssistantSystemPrompt.Default.Should().Contain(ruleId,
            because: $"the assistant must cite '{ruleId}' when explaining a save rejection");
    }

    [Fact]
    public void DefaultPrompt_DoesNotListPackageSelfContainmentAsSaveValidator()
    {
        // Codex audit follow-up: there is no `package-self-containment` rule in the
        // WorkflowValidator pipeline. The exporter's resolver throws when an in-DB workflow
        // can't form a closed dependency graph, and the importer's preview surfaces missing
        // unembedded refs as Conflict items — neither maps to a save-time validator named
        // package-self-containment. Keep that name out of the validators list so the LLM
        // doesn't cite a rule that doesn't exist.
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().NotContain("`package-self-containment` (V8)",
            because: "no save-time validator with that id exists; the resolver's export-time "
                + "self-containment check is described in the package section instead");
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("saga")]
    [InlineData("replay")]
    [InlineData("drift")]
    [InlineData("token")]
    [InlineData("in-place agent edit")]
    [InlineData("code-aware")]
    [InlineData("working directory")]
    public void DefaultPrompt_CoversRuntimeConcepts(string concept)
    {
        AssistantSystemPrompt.Default.Should().ContainEquivalentOf(concept,
            because: $"acceptance criterion requires runtime concept '{concept}'");
    }

    [Fact]
    public void DefaultPrompt_DescribesCurrentCapabilitiesAndGaps()
    {
        // The prompt's "what you can / can't do" stanza is the assistant's self-awareness about
        // shipped vs unshipped slices. As features land it must be kept current — these checks
        // pin the current state so a future PR that lands HAA-10 etc. updates this test
        // intentionally, not by accident.
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().Contain("Inspect a trace's timeline",
            because: "HAA-4 + HAA-5 wired tools; the prompt must claim trace-introspection capability");
        prompt.Should().Contain("Draft a complete workflow package",
            because: "HAA-9 made drafting a real capability");
        prompt.Should().Contain("save_workflow_package",
            because: "HAA-10 wired the save tool; the prompt must teach the model to invoke it");
        prompt.Should().Contain("run_workflow",
            because: "HAA-11 wired the run tool; the prompt must teach the model to invoke it");
        prompt.Should().Contain("diagnose_trace",
            because: "HAA-12 wired the diagnosis tool; the prompt must teach the model to invoke it");
        prompt.Should().Contain("propose_replay_with_edit",
            because: "HAA-13 wired the replay-with-edit bridge; the prompt must teach the model to invoke it");
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("codeflow.workflow-package.v1")]
    [InlineData("entryPoint")]
    [InlineData("agentRoleAssignments")]
    [InlineData("Embedding rule")]
    [InlineData("cf-workflow-package")]
    public void DefaultPrompt_DescribesPackageEmissionContract(string token)
    {
        // HAA-9 emission contract: the assistant must know the package schema's top-level
        // shape, the embedding rule (token economy — only embed entities being created/bumped,
        // existing refs resolve from the DB), and the fence language hint the chat UI looks for.
        AssistantSystemPrompt.Default.Should().Contain(token,
            because: $"HAA-9 emission contract requires the prompt to mention '{token}'");
    }

    [Fact]
    public void DefaultPrompt_DescribesRefinementBehavior()
    {
        // Acceptance criterion: refining mid-conversation produces a coherent updated package
        // without losing earlier decisions, and re-emits the FULL package, never deltas.
        AssistantSystemPrompt.Default.Should().Contain("never deltas",
            because: "the prompt must explicitly forbid emitting partial / diff packages on refinement");
    }

    [Theory]
    [InlineData("config.outputs")]
    [InlineData("package-node-missing-agent-version")]
    [InlineData("package-node-missing-subflow-version")]
    public void DefaultPrompt_PinsPackageAdmissionFacts(string token)
    {
        // Codex audit (2026-05-01) caught three drift hazards in the prompt: agentVersion /
        // subflowVersion pins ARE mandatory in packages (admission rejects null), declared
        // outputs MUST live in `config.outputs` (the package's top-level `agents[].outputs[]`
        // is exporter-only metadata that the importer ignores), and the embedding-rule
        // language is the truth — the importer DOES resolve unembedded refs from the local DB.
        // Pin those facts so a future edit can't quietly reintroduce the misconception.
        AssistantSystemPrompt.Default.Should().Contain(token,
            because: $"prompt must teach '{token}' so it doesn't draft packages that fail admission");
    }

    [Fact]
    public void DefaultPrompt_DoesNotImplyImporterIgnoresDb()
    {
        // The earlier "the importer does not resolve from the DB" wording contradicted the
        // resolver, which DOES look up unembedded refs against the local DB. Forbid that
        // misleading sentence so we don't regress; the embedding rule's "Reuse" framing is
        // the canonical phrasing.
        AssistantSystemPrompt.Default.Should().NotContain(
            "importer does not resolve from the DB",
            because: "the importer DOES resolve unembedded refs against the DB and emits Reuse items");
        AssistantSystemPrompt.Default.Should().NotContain(
            "importer does\nnot resolve from the DB",
            because: "wrap-resilient version of the same lint");
    }

    [Fact]
    public void DefaultPrompt_DescribesAllInvalidResultShapes()
    {
        // save_workflow_package's `status: "invalid"` payload always carries message + hint,
        // and may carry errors[] (validator detail) but missingReferences[] is almost always
        // empty on the import path because importer throws use the no-MissingReferences
        // exception constructor. Plus a bare { error } when the tool itself fails. Pin each
        // so the prompt can't regress to the earlier framings.
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().Contain("errors[]",
            because: "validator-invalid payloads carry errors[]");
        prompt.Should().Contain("\"error\":",
            because: "tool-level failures return a bare { error } object with no status field");
        prompt.Should().Contain("almost always empty",
            because: "the prompt must explicitly call out that missingReferences[] is empty "
                + "on the import path so the LLM doesn't anchor remediation on it");
    }

    [Fact]
    public void DefaultPrompt_RoutesUnembeddedRefMissesThroughPreviewConflicts()
    {
        // Codex audit (2026-05-01 follow-up): on import, an unembedded ref the target
        // library doesn't have becomes a Conflict item (`status: "preview_conflicts"`),
        // not `status: "invalid"` with a missingReferences[] array. The prompt must direct
        // the LLM to resolve unembedded-ref misses by inspecting `items[]` with
        // `action: "Conflict"`, not by looking for missingReferences[].
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().Contain("preview_conflicts",
            because: "the conflict-preview branch is where unembedded-ref misses surface");
        prompt.Should().Contain("unembedded ref",
            because: "the prompt must explicitly cover the unembedded-ref-not-in-DB case");
    }

    [Fact]
    public void DefaultPrompt_FramesToolSurfaceAsExtensible()
    {
        // The operator can wire additional tools via an assigned agent role and an admin
        // overlay (operator instructions). The prompt must not contradict that — pin the
        // explicit "use whichever tools the runtime advertises" guidance and the
        // <operator-instructions> handoff so future edits don't reintroduce closed-set
        // wording like "these are the only tools at your disposal".
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().ContainEquivalentOf("Use whichever tools the");
        prompt.Should().ContainEquivalentOf("runtime advertises");
        prompt.Should().Contain("Tools at your disposal",
            because: "the section header anchors the model's reading of the role-grant + operator-overlay handoff");
        prompt.Should().Contain("<operator-instructions>",
            because: "the curated prompt must point the model at the operator-overlay block");
        prompt.Should().NotContain("only tools at your disposal");
        prompt.Should().NotContain("these are the only tools",
            because: "the assistant's tool surface is extended by role grants + operator instructions");
    }

    [Fact]
    public void DefaultPrompt_NeverPromisesDollarCosts()
    {
        // Project guardrail: CodeFlow tracks tokens only — never compute or display dollar costs.
        // Catch any future edit that would reintroduce cost language as a thing the assistant
        // computes (the prompt may instruct against it but should not present it as a feature).
        var prompt = AssistantSystemPrompt.Default.ToLowerInvariant();
        prompt.Should().NotContain("$");
        prompt.Should().NotContain("price per token");
        prompt.Should().NotContain("compute the cost");
        prompt.Should().NotContain("estimate the cost");
    }
}
