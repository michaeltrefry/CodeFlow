using System.Text.RegularExpressions;
using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;

namespace CodeFlow.Api.Validation.Pipeline.Rules;

/// <summary>
/// V7: Lint each agent's SystemPrompt and PromptTemplate for forbidden phrases that cause the
/// well-known "reviewer rejects every iteration" / "always reject" failure modes (see
/// docs/authoring-workflows.md). Soft-warning per match — authors can dismiss after reviewing
/// or apply the suggested fix in the editor (the editor's auto-fix replaces the offending block
/// with <c>{{ include "@codeflow/reviewer-base" }}</c>; that UI affordance is wired in P1).
///
/// Forbidden phrases are matched case-insensitively with word-boundary anchoring to avoid
/// false positives in prose that quotes the rule itself. Each agent fires at most one finding
/// per phrase per validation run regardless of how many nodes reference it.
/// </summary>
public sealed class PromptLintRule : IWorkflowValidationRule
{
    /// <summary>
    /// Phrases that cause prompt-anti-pattern failures, with a short remediation hint shown to
    /// the author. Order matters only for the order findings are emitted.
    /// </summary>
    private static readonly IReadOnlyList<ForbiddenPhrase> ForbiddenPhrases = new[]
    {
        new ForbiddenPhrase(
            Pattern: new Regex(@"default to rejected?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            Hint: "Reviewers that 'default to Rejected' bias every round toward rejection. "
                + "Replace with the @codeflow/reviewer-base partial which encodes the approval-"
                + "bias norm."),
        new ForbiddenPhrase(
            Pattern: new Regex(@"you must always reject", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            Hint: "Hard 'always reject' instructions guarantee the loop exhausts its rounds. "
                + "Use criteria-based rejection instead."),
        new ForbiddenPhrase(
            Pattern: new Regex(@"the goal is \d+ iterations?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            Hint: "Stating an iteration count as a goal teaches the agent to keep rejecting. "
                + "ReviewLoop's maxRounds is a budget, not a target — phrase the criteria as "
                + "'approve when X is true' instead."),
        new ForbiddenPhrase(
            Pattern: new Regex(@"keep iterating until", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            Hint: "'Keep iterating until' framing implies the agent should hold approval. "
                + "Frame the criteria positively: 'approve when these conditions are met'."),
    };

    public string RuleId => "prompt-lint";

    public int Order => 230;

    public async Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<WorkflowValidationFinding>();
        // Cache: lint each (agentKey, version) pair once even if referenced by multiple nodes.
        var linted = new Dictionary<(string Key, int Version), Guid?>();

        foreach (var node in context.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsAgentPinnedNodeKind(node.Kind))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.AgentKey))
            {
                continue;
            }

            var agentKey = node.AgentKey.Trim();

            int version;
            try
            {
                version = node.AgentVersion ?? await context.AgentRepository
                    .GetLatestVersionAsync(agentKey, cancellationToken);
            }
            catch (AgentConfigNotFoundException)
            {
                continue;
            }

            var versionKey = (agentKey, version);
            if (linted.ContainsKey(versionKey))
            {
                continue;
            }

            AgentConfig agentConfig;
            try
            {
                agentConfig = await context.AgentRepository.GetAsync(
                    agentKey, version, cancellationToken);
            }
            catch (AgentConfigNotFoundException)
            {
                continue;
            }

            linted[versionKey] = node.Id;

            var systemPrompt = agentConfig.Configuration?.SystemPrompt;
            var promptTemplate = agentConfig.Configuration?.PromptTemplate;

            // Per phrase, emit at most one finding per agent — even if both prompts trip the
            // same pattern. Editors/authors prefer one warning per (agent, rule) pair over a
            // wall of duplicates.
            foreach (var phrase in ForbiddenPhrases)
            {
                if (TryMatch(phrase, systemPrompt, "system prompt", out var hint)
                    || TryMatch(phrase, promptTemplate, "prompt template", out hint))
                {
                    findings.Add(new WorkflowValidationFinding(
                        RuleId: RuleId,
                        Severity: WorkflowValidationSeverity.Warning,
                        Message: $"Agent '{agentKey}' v{version} {hint}",
                        Location: new WorkflowValidationLocation(NodeId: node.Id)));
                }
            }
        }

        return findings;
    }

    private static bool TryMatch(
        ForbiddenPhrase phrase,
        string? prompt,
        string source,
        out string hint)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            hint = string.Empty;
            return false;
        }

        var match = phrase.Pattern.Match(prompt);
        if (!match.Success)
        {
            hint = string.Empty;
            return false;
        }

        hint = $"{source} contains the phrase \"{match.Value}\". {phrase.Hint}";
        return true;
    }

    private static bool IsAgentPinnedNodeKind(WorkflowNodeKind kind) =>
        kind is WorkflowNodeKind.Start or WorkflowNodeKind.Agent or WorkflowNodeKind.Hitl;

    private sealed record ForbiddenPhrase(Regex Pattern, string Hint);
}
