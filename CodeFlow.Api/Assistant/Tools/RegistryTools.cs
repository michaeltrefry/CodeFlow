using System.Text.Json;
using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Registry-introspection tools (HAA-4). Each tool wraps a thin DB query against the same
/// repositories the rest of the API uses; results are bounded so the assistant's context window
/// can't be blown by a sprawling catalog.
/// </summary>
file static class RegistryDefaults
{
    public const int DefaultListLimit = 25;
    public const int MaxListLimit = 100;

    /// <summary>
    /// Per-string truncation cap for free-form fields like prompt templates and system prompts.
    /// 4 KB per field × small-handful-of-fields-per-record × small-list = comfortably inside the
    /// dispatcher's 32 KB result budget.
    /// </summary>
    public const int LongStringCap = 4096;
}

/// <summary>
/// Lists workflows in the library, optionally filtered by category, name prefix, or tag.
/// Returns latest-version summary per key.
/// </summary>
public sealed class ListWorkflowsTool(IWorkflowRepository repository) : IAssistantTool
{
    public string Name => "list_workflows";
    public string Description =>
        "List workflows from the library. Returns the latest version of each workflow with summary fields " +
        "(key, version, name, category, tags, node/edge/input counts, createdAtUtc). Supports optional " +
        "filtering by category (Workflow|Subflow|Loop), case-insensitive name prefix, and tag membership. " +
        "Default limit 25, max 100.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""category"": { ""type"": ""string"", ""enum"": [""Workflow"", ""Subflow"", ""Loop""], ""description"": ""Filter by WorkflowCategory."" },
            ""namePrefix"": { ""type"": ""string"", ""description"": ""Case-insensitive prefix match against workflow name."" },
            ""tag"": { ""type"": ""string"", ""description"": ""Match workflows that carry this tag (case-insensitive)."" },
            ""limit"": { ""type"": ""integer"", ""description"": ""Max results to return. Default 25, max 100."" }
        },
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var category = AssistantToolJson.ReadOptionalString(arguments, "category");
        var namePrefix = AssistantToolJson.ReadOptionalString(arguments, "namePrefix");
        var tag = AssistantToolJson.ReadOptionalString(arguments, "tag");
        var limit = AssistantToolJson.ClampLimit(
            AssistantToolJson.ReadOptionalInt(arguments, "limit"),
            RegistryDefaults.DefaultListLimit,
            RegistryDefaults.MaxListLimit);

        var workflows = await repository.ListLatestAsync(cancellationToken);

        WorkflowCategory? categoryFilter = null;
        if (category is not null
            && Enum.TryParse<WorkflowCategory>(category, ignoreCase: true, out var parsedCategory))
        {
            categoryFilter = parsedCategory;
        }

        var filtered = workflows
            .Where(w => categoryFilter is null || w.Category == categoryFilter)
            .Where(w => namePrefix is null
                || w.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(w => tag is null
                || w.TagsOrEmpty.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(w => w.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(w => new
            {
                key = w.Key,
                latestVersion = w.Version,
                name = w.Name,
                category = w.Category.ToString(),
                tags = w.TagsOrEmpty,
                nodeCount = w.Nodes.Count,
                edgeCount = w.Edges.Count,
                inputCount = w.Inputs.Count,
                createdAtUtc = w.CreatedAtUtc,
            })
            .ToArray();

        var json = JsonSerializer.Serialize(new
        {
            count = filtered.Length,
            limit,
            truncated = workflows.Count > filtered.Length,
            workflows = filtered
        }, AssistantToolJson.SerializerOptions);

        return new AssistantToolResult(json);
    }
}

/// <summary>
/// Fetches a single workflow with its full graph (nodes, edges, inputs). If <c>version</c> is
/// omitted, returns the latest version.
/// </summary>
public sealed class GetWorkflowTool(IWorkflowRepository repository) : IAssistantTool
{
    public string Name => "get_workflow";
    public string Description =>
        "Get a single workflow with its full node/edge/input graph. If `version` is omitted, returns the " +
        "latest version. Each node includes kind, agent reference, ports, scripts (truncated to 4 KB), " +
        "subflow reference, swarm config, and template. Use this when the user asks 'show me workflow X' " +
        "or before reasoning about a workflow's structure.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""key"": { ""type"": ""string"", ""description"": ""Workflow key (required)."" },
            ""version"": { ""type"": ""integer"", ""description"": ""Specific version to fetch. Omit to get latest."" }
        },
        ""required"": [""key""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var key = AssistantToolJson.ReadOptionalString(arguments, "key")
            ?? throw new ArgumentException("`key` is required.");
        var version = AssistantToolJson.ReadOptionalInt(arguments, "version");

        Workflow? workflow;
        try
        {
            workflow = version is null
                ? await repository.GetLatestAsync(key, cancellationToken)
                : await repository.GetAsync(key, version.Value, cancellationToken);
        }
        catch (WorkflowNotFoundException)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Workflow '{key}' v{version} not found." }),
                IsError: true);
        }

        if (workflow is null)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Workflow '{key}' not found." }),
                IsError: true);
        }

        var payload = new
        {
            key = workflow.Key,
            version = workflow.Version,
            name = workflow.Name,
            category = workflow.Category.ToString(),
            tags = workflow.TagsOrEmpty,
            maxRoundsPerRound = workflow.MaxRoundsPerRound,
            createdAtUtc = workflow.CreatedAtUtc,
            isRetired = workflow.IsRetired,
            terminalPorts = workflow.TerminalPorts,
            workflowVarsReads = workflow.WorkflowVarsReadsOrEmpty,
            workflowVarsWrites = workflow.WorkflowVarsWritesOrEmpty,
            inputs = workflow.Inputs
                .OrderBy(i => i.Ordinal)
                .Select(i => new
                {
                    key = i.Key,
                    displayName = i.DisplayName,
                    kind = i.Kind.ToString(),
                    required = i.Required,
                    description = i.Description,
                })
                .ToArray(),
            nodes = workflow.Nodes
                .Select(n => new
                {
                    id = n.Id,
                    kind = n.Kind.ToString(),
                    agentKey = n.AgentKey,
                    agentVersion = n.AgentVersion,
                    outputPorts = n.OutputPorts,
                    subflowKey = n.SubflowKey,
                    subflowVersion = n.SubflowVersion,
                    reviewMaxRounds = n.ReviewMaxRounds,
                    loopDecision = n.LoopDecision,
                    inputScript = AssistantToolJson.TruncateText(n.InputScript, RegistryDefaults.LongStringCap),
                    outputScript = AssistantToolJson.TruncateText(n.OutputScript, RegistryDefaults.LongStringCap),
                    template = AssistantToolJson.TruncateText(n.Template, RegistryDefaults.LongStringCap),
                    outputType = n.OutputType,
                    swarmProtocol = n.SwarmProtocol,
                    swarmN = n.SwarmN,
                    contributorAgentKey = n.ContributorAgentKey,
                    synthesizerAgentKey = n.SynthesizerAgentKey,
                    coordinatorAgentKey = n.CoordinatorAgentKey,
                })
                .ToArray(),
            edges = workflow.Edges
                .Select(e => new
                {
                    fromNodeId = e.FromNodeId,
                    fromPort = e.FromPort,
                    toNodeId = e.ToNodeId,
                    toPort = e.ToPort,
                    rotatesRound = e.RotatesRound,
                    intentionalBackedge = e.IntentionalBackedge,
                })
                .ToArray(),
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }
}

/// <summary>
/// Lists every saved version of a workflow key (newest first).
/// </summary>
public sealed class ListWorkflowVersionsTool(IWorkflowRepository repository) : IAssistantTool
{
    public string Name => "list_workflow_versions";
    public string Description =>
        "List every saved version of a workflow (newest first). Returns each version's number, name " +
        "(may differ across versions), category, and createdAtUtc. Use when the user asks about a " +
        "workflow's history or wants to compare versions.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""key"": { ""type"": ""string"", ""description"": ""Workflow key (required)."" }
        },
        ""required"": [""key""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var key = AssistantToolJson.ReadOptionalString(arguments, "key")
            ?? throw new ArgumentException("`key` is required.");

        var versions = await repository.ListVersionsAsync(key, cancellationToken);
        if (versions.Count == 0)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Workflow '{key}' not found." }),
                IsError: true);
        }

        var payload = new
        {
            key,
            count = versions.Count,
            versions = versions
                .Select(v => new
                {
                    version = v.Version,
                    name = v.Name,
                    category = v.Category.ToString(),
                    createdAtUtc = v.CreatedAtUtc,
                    nodeCount = v.Nodes.Count,
                    edgeCount = v.Edges.Count,
                })
                .ToArray()
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }
}

/// <summary>
/// Lists agents (latest version per key), optionally filtered. Skips workflow-scoped forks because
/// they're internal to a single workflow's authoring lifecycle and not useful library-level surface.
/// </summary>
public sealed class ListAgentsTool(CodeFlowDbContext dbContext) : IAssistantTool
{
    public string Name => "list_agents";
    public string Description =>
        "List library agents (latest version per key). Returns key, latest version, name (from agent " +
        "config), provider, model, kind, createdAtUtc, isRetired. Workflow-scoped forks are excluded — " +
        "they belong to in-progress in-place edits, not the public library. Supports optional case-" +
        "insensitive name prefix, provider filter (anthropic/openai/lmstudio), and includeRetired " +
        "(default false). Default limit 25, max 100.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""namePrefix"": { ""type"": ""string"", ""description"": ""Case-insensitive prefix match on agent key."" },
            ""provider"": { ""type"": ""string"", ""description"": ""Filter by provider (e.g. 'anthropic', 'openai')."" },
            ""includeRetired"": { ""type"": ""boolean"", ""description"": ""Include retired agents. Default false."" },
            ""limit"": { ""type"": ""integer"", ""description"": ""Max results. Default 25, max 100."" }
        },
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var namePrefix = AssistantToolJson.ReadOptionalString(arguments, "namePrefix");
        var providerFilter = AssistantToolJson.ReadOptionalString(arguments, "provider");
        var includeRetired = AssistantToolJson.ReadOptionalBool(arguments, "includeRetired", defaultValue: false);
        var limit = AssistantToolJson.ClampLimit(
            AssistantToolJson.ReadOptionalInt(arguments, "limit"),
            RegistryDefaults.DefaultListLimit,
            RegistryDefaults.MaxListLimit);

        IQueryable<AgentConfigEntity> query = dbContext.Agents
            .AsNoTracking()
            .Where(a => a.OwningWorkflowKey == null);
        if (!includeRetired)
        {
            query = query.Where(a => !a.IsRetired);
        }
        var entities = await query
            .OrderBy(a => a.Key)
            .ThenByDescending(a => a.Version)
            .ToListAsync(cancellationToken);

        var summaries = entities
            .GroupBy(a => a.Key)
            .Select(g => g.First())
            .Where(a => namePrefix is null
                || a.Key.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(a =>
            {
                AgentConfigSummaryView view;
                try
                {
                    var config = AgentConfigJsonReader.Read(a.ConfigJson);
                    view = new AgentConfigSummaryView(config.Provider, config.Model, config.Kind);
                }
                catch
                {
                    view = new AgentConfigSummaryView(null, null, null);
                }
                return (Entity: a, View: view);
            })
            .Where(t => providerFilter is null
                || string.Equals(t.View.Provider, providerFilter, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(t => new
            {
                key = t.Entity.Key,
                latestVersion = t.Entity.Version,
                provider = t.View.Provider,
                model = t.View.Model,
                kind = t.View.Kind,
                createdAtUtc = DateTime.SpecifyKind(t.Entity.CreatedAtUtc, DateTimeKind.Utc),
                createdBy = t.Entity.CreatedBy,
                isRetired = t.Entity.IsRetired,
            })
            .ToArray();

        var json = JsonSerializer.Serialize(new
        {
            count = summaries.Length,
            limit,
            agents = summaries
        }, AssistantToolJson.SerializerOptions);

        return new AssistantToolResult(json);
    }

    private sealed record AgentConfigSummaryView(string? Provider, string? Model, string? Kind);
}

/// <summary>
/// Fetches a single agent's full configuration including (truncated) prompt fields. When
/// <c>version</c> is omitted, returns the latest version.
/// </summary>
public sealed class GetAgentTool(IAgentConfigRepository repository) : IAssistantTool
{
    public string Name => "get_agent";
    public string Description =>
        "Get a single agent's full configuration: provider, model, system prompt, prompt template, " +
        "declared outputs, partial pins, and tool access policy. Long string fields (system prompt, " +
        "prompt template) are truncated to 4 KB with a marker. Use this when the user asks 'what's " +
        "the prompt for agent X' or before reasoning about an agent's behavior.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""key"": { ""type"": ""string"", ""description"": ""Agent key (required)."" },
            ""version"": { ""type"": ""integer"", ""description"": ""Specific version. Omit to get latest."" }
        },
        ""required"": [""key""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var key = AssistantToolJson.ReadOptionalString(arguments, "key")
            ?? throw new ArgumentException("`key` is required.");
        var version = AssistantToolJson.ReadOptionalInt(arguments, "version");

        AgentConfig? agent = null;
        try
        {
            var resolvedVersion = version ?? await repository.GetLatestVersionAsync(key, cancellationToken);
            agent = await repository.GetAsync(key, resolvedVersion, cancellationToken);
        }
        catch (AgentConfigNotFoundException)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Agent '{key}' v{version} not found." }),
                IsError: true);
        }

        var payload = new
        {
            key = agent.Key,
            version = agent.Version,
            kind = agent.Kind.ToString(),
            provider = agent.Configuration.Provider,
            model = agent.Configuration.Model,
            systemPrompt = AssistantToolJson.TruncateText(agent.Configuration.SystemPrompt, RegistryDefaults.LongStringCap),
            promptTemplate = AssistantToolJson.TruncateText(agent.Configuration.PromptTemplate, RegistryDefaults.LongStringCap),
            maxTokens = agent.Configuration.MaxTokens,
            temperature = agent.Configuration.Temperature,
            partialPins = agent.Configuration.PartialPins?
                .Select(p => new { key = p.Key, version = p.Version })
                .ToArray()
                ?? Array.Empty<object>(),
            outputs = agent.DeclaredOutputs
                .Select(o => new
                {
                    kind = o.Kind,
                    description = o.Description,
                    contentOptional = o.ContentOptional,
                })
                .ToArray(),
            owningWorkflowKey = agent.OwningWorkflowKey,
            forkedFromKey = agent.ForkedFromKey,
            forkedFromVersion = agent.ForkedFromVersion,
            createdAtUtc = agent.CreatedAtUtc,
            createdBy = agent.CreatedBy,
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }
}

/// <summary>
/// Lists every saved version of an agent key (newest first).
/// </summary>
public sealed class ListAgentVersionsTool(CodeFlowDbContext dbContext) : IAssistantTool
{
    public string Name => "list_agent_versions";
    public string Description =>
        "List every saved version of an agent (newest first). Returns each version's number, " +
        "createdAtUtc, createdBy, and isRetired flag.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""key"": { ""type"": ""string"", ""description"": ""Agent key (required)."" }
        },
        ""required"": [""key""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var key = AssistantToolJson.ReadOptionalString(arguments, "key")
            ?? throw new ArgumentException("`key` is required.");

        var entities = await dbContext.Agents
            .AsNoTracking()
            .Where(a => a.Key == key)
            .OrderByDescending(a => a.Version)
            .ToListAsync(cancellationToken);

        if (entities.Count == 0)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Agent '{key}' not found." }),
                IsError: true);
        }

        var payload = new
        {
            key,
            count = entities.Count,
            versions = entities
                .Select(e => new
                {
                    version = e.Version,
                    createdAtUtc = DateTime.SpecifyKind(e.CreatedAtUtc, DateTimeKind.Utc),
                    createdBy = e.CreatedBy,
                    isRetired = e.IsRetired,
                })
                .ToArray()
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }
}

/// <summary>
/// Cross-reference: which workflows reference a given agent. Walks every node kind that can carry
/// an agent reference (Agent, ReviewLoop's loopDecision agent, Swarm's contributor/synthesizer/
/// coordinator).
/// </summary>
public sealed class FindWorkflowsUsingAgentTool(CodeFlowDbContext dbContext) : IAssistantTool
{
    public string Name => "find_workflows_using_agent";
    public string Description =>
        "Find workflows that reference a given agent. Walks every agent slot (Agent.AgentKey, Swarm's " +
        "contributor/synthesizer/coordinator agents). If `agentVersion` is supplied, only matches " +
        "nodes pinned to that exact version; otherwise matches any version. Returns workflow key + " +
        "latest version + the matching node ids and slots.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""agentKey"": { ""type"": ""string"", ""description"": ""Agent key (required)."" },
            ""agentVersion"": { ""type"": ""integer"", ""description"": ""Optional pinned version filter. Omit to match any version."" }
        },
        ""required"": [""agentKey""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var agentKey = AssistantToolJson.ReadOptionalString(arguments, "agentKey")
            ?? throw new ArgumentException("`agentKey` is required.");
        var agentVersion = AssistantToolJson.ReadOptionalInt(arguments, "agentVersion");

        // Pull all workflow nodes that match in any of the agent slots, then group by workflow.
        // EF can't translate a clean cross-slot filter, so we filter in memory after the narrowed
        // server-side WHERE on the four candidate columns.
        var candidateWorkflows = await dbContext.Workflows
            .AsNoTracking()
            .Where(w => w.Nodes.Any(n =>
                n.AgentKey == agentKey
                || n.ContributorAgentKey == agentKey
                || n.SynthesizerAgentKey == agentKey
                || n.CoordinatorAgentKey == agentKey))
            .Include(w => w.Nodes)
            .ToListAsync(cancellationToken);

        // Latest-version-only: pick the highest-version row per key.
        var latestByKey = candidateWorkflows
            .GroupBy(w => w.Key)
            .Select(g => g.OrderByDescending(w => w.Version).First())
            .OrderBy(w => w.Key, StringComparer.Ordinal)
            .ToArray();

        var matches = latestByKey
            .Select(w =>
            {
                var slots = new List<object>();
                foreach (var n in w.Nodes)
                {
                    AddSlot(slots, n.NodeId, "AgentKey", n.AgentKey, n.AgentVersion, agentKey, agentVersion);
                    AddSlot(slots, n.NodeId, "ContributorAgentKey", n.ContributorAgentKey, n.ContributorAgentVersion, agentKey, agentVersion);
                    AddSlot(slots, n.NodeId, "SynthesizerAgentKey", n.SynthesizerAgentKey, n.SynthesizerAgentVersion, agentKey, agentVersion);
                    AddSlot(slots, n.NodeId, "CoordinatorAgentKey", n.CoordinatorAgentKey, n.CoordinatorAgentVersion, agentKey, agentVersion);
                }
                return (Workflow: w, Slots: slots);
            })
            .Where(t => t.Slots.Count > 0)
            .Select(t => new
            {
                key = t.Workflow.Key,
                version = t.Workflow.Version,
                name = t.Workflow.Name,
                category = t.Workflow.Category.ToString(),
                matches = t.Slots,
            })
            .ToArray();

        var payload = new
        {
            agentKey,
            agentVersion,
            count = matches.Length,
            workflows = matches,
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }

    private static void AddSlot(
        List<object> slots,
        Guid nodeId,
        string slot,
        string? slotAgentKey,
        int? slotAgentVersion,
        string agentKey,
        int? agentVersion)
    {
        if (!string.Equals(slotAgentKey, agentKey, StringComparison.Ordinal)) return;
        if (agentVersion is not null && slotAgentVersion != agentVersion) return;
        slots.Add(new { nodeId, slot, version = slotAgentVersion });
    }
}

/// <summary>
/// Searches agents whose system prompt or prompt template contains a fragment. Returns matching
/// agent key+version with the surrounding snippet. Case-insensitive substring match — no regex,
/// no fuzzy matching, no full-text indexing.
/// </summary>
public sealed class SearchPromptsTool(CodeFlowDbContext dbContext) : IAssistantTool
{
    private const int SnippetCharsBefore = 80;
    private const int SnippetCharsAfter = 120;

    public string Name => "search_prompts";
    public string Description =>
        "Find agents whose system prompt or prompt template contains a substring (case-insensitive). " +
        "Returns each matching agent's latest version + which field matched + a short snippet around " +
        "the match. Use when the user asks 'which agents talk about X' or 'who's pinning the X partial'. " +
        "Default limit 20, max 50. Substring search only — no regex.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""query"": { ""type"": ""string"", ""description"": ""Substring to search for (case-insensitive). Required."" },
            ""limit"": { ""type"": ""integer"", ""description"": ""Max results. Default 20, max 50."" }
        },
        ""required"": [""query""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = AssistantToolJson.ReadOptionalString(arguments, "query")
            ?? throw new ArgumentException("`query` is required.");
        var limit = AssistantToolJson.ClampLimit(
            AssistantToolJson.ReadOptionalInt(arguments, "limit"),
            defaultLimit: 20,
            maxLimit: 50);

        // Pull latest version per key from rows that aren't workflow-scoped forks. Scan ConfigJson
        // in memory because the agent prompt fields live inside the JSON blob — no indexed column
        // for them. Catalog is small (low hundreds of agents), so a full scan is fine; if this
        // grows we can add a generated column or a search index.
        var entities = await dbContext.Agents
            .AsNoTracking()
            .Where(a => a.OwningWorkflowKey == null && !a.IsRetired)
            .OrderBy(a => a.Key)
            .ThenByDescending(a => a.Version)
            .ToListAsync(cancellationToken);

        var hits = new List<object>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (!seenKeys.Add(entity.Key)) continue; // latest per key

            string? systemPrompt = null;
            string? promptTemplate = null;
            try
            {
                var view = AgentConfigJsonReader.Read(entity.ConfigJson);
                systemPrompt = view.SystemPrompt;
                promptTemplate = view.PromptTemplate;
            }
            catch
            {
                continue;
            }

            var systemHit = MatchSnippet(systemPrompt, query);
            var promptHit = MatchSnippet(promptTemplate, query);
            if (systemHit is null && promptHit is null) continue;

            hits.Add(new
            {
                key = entity.Key,
                version = entity.Version,
                systemPromptMatch = systemHit,
                promptTemplateMatch = promptHit,
            });

            if (hits.Count >= limit) break;
        }

        var payload = new
        {
            query,
            limit,
            count = hits.Count,
            hits,
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }

    private static object? MatchSnippet(string? source, string query)
    {
        if (string.IsNullOrEmpty(source)) return null;
        var idx = source.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = Math.Max(0, idx - SnippetCharsBefore);
        var end = Math.Min(source.Length, idx + query.Length + SnippetCharsAfter);
        var snippet = source[start..end];
        return new
        {
            offset = idx,
            length = query.Length,
            snippet = (start > 0 ? "..." : "") + snippet + (end < source.Length ? "..." : ""),
        };
    }
}

/// <summary>
/// Lists agent roles (the role-based access surface that grants tool/skill access to agents).
/// </summary>
public sealed class ListAgentRolesTool(IAgentRoleRepository repository) : IAssistantTool
{
    public string Name => "list_agent_roles";
    public string Description =>
        "List agent roles (the role-based grants surface that gives agents access to host tools, " +
        "MCP tools, sub-agents, and skills). Returns id, key, displayName, description, isArchived, " +
        "isSystemManaged. By default excludes archived roles.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""includeArchived"": { ""type"": ""boolean"", ""description"": ""Include archived roles. Default false."" }
        },
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var includeArchived = AssistantToolJson.ReadOptionalBool(arguments, "includeArchived", defaultValue: false);
        var roles = await repository.ListAsync(includeArchived, includeRetired: false, cancellationToken);

        var payload = new
        {
            count = roles.Count,
            roles = roles
                .Select(r => new
                {
                    id = r.Id,
                    key = r.Key,
                    displayName = r.DisplayName,
                    description = r.Description,
                    isArchived = r.IsArchived,
                    isRetired = r.IsRetired,
                    isSystemManaged = r.IsSystemManaged,
                })
                .ToArray()
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }
}

/// <summary>
/// Returns a single agent role's full surface: identity, tool grants (host + MCP + sub-agent),
/// and skill grants. Lets the homepage assistant self-diagnose what its assigned role actually
/// permits — distinguishing "the role grants nothing" from "the runtime didn't merge the grants"
/// when expected tools don't appear at runtime.
/// </summary>
public sealed class GetAgentRoleTool(IAgentRoleRepository roleRepository, ISkillRepository skillRepository) : IAssistantTool
{
    public string Name => "get_agent_role";

    public string Description =>
        "Get a single agent role's full surface: id, key, displayName, description, archived/" +
        "system flags, every tool grant (host tools, MCP tool identifiers, sub-agent keys), and " +
        "every granted skill name. Use this to verify what the assistant's assigned role permits, " +
        "or to inspect a role you discovered via list_agent_roles. Lookup is by `id` OR `key` — " +
        "exactly one must be supplied.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""id"": { ""type"": ""integer"", ""description"": ""Role id (use either id or key, not both)."" },
            ""key"": { ""type"": ""string"", ""description"": ""Role key (use either id or key, not both)."" }
        },
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var idArg = AssistantToolJson.ReadOptionalInt(arguments, "id");
        var keyArg = AssistantToolJson.ReadOptionalString(arguments, "key");

        if (idArg is null && keyArg is null)
        {
            return Error("Provide either `id` or `key`.");
        }
        if (idArg is not null && keyArg is not null)
        {
            return Error("Provide either `id` or `key`, not both.");
        }

        AgentRole? role = idArg is { } id
            ? await roleRepository.GetAsync(id, cancellationToken)
            : await roleRepository.GetByKeyAsync(keyArg!, cancellationToken);

        if (role is null)
        {
            var label = idArg is { } missingId ? $"id={missingId}" : $"key='{keyArg}'";
            return Error($"Agent role {label} not found.");
        }

        var grants = await roleRepository.GetGrantsAsync(role.Id, cancellationToken);
        var skillIds = await roleRepository.GetSkillGrantsAsync(role.Id, cancellationToken);
        var skillNames = new List<string>(skillIds.Count);
        foreach (var skillId in skillIds.Distinct())
        {
            var skill = await skillRepository.GetAsync(skillId, cancellationToken);
            if (skill is not null)
            {
                skillNames.Add(skill.Name);
            }
        }
        skillNames.Sort(StringComparer.OrdinalIgnoreCase);

        var payload = new
        {
            id = role.Id,
            key = role.Key,
            displayName = role.DisplayName,
            description = role.Description,
            isArchived = role.IsArchived,
            isRetired = role.IsRetired,
            isSystemManaged = role.IsSystemManaged,
            grantCount = grants.Count,
            skillCount = skillNames.Count,
            toolGrants = grants
                .OrderBy(g => g.Category)
                .ThenBy(g => g.ToolIdentifier, StringComparer.Ordinal)
                .Select(g => new
                {
                    category = g.Category.ToString(),
                    toolIdentifier = g.ToolIdentifier,
                })
                .ToArray(),
            skillNames = skillNames.ToArray(),
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }

    private static AssistantToolResult Error(string message)
    {
        return new AssistantToolResult(
            JsonSerializer.Serialize(new { error = message }, AssistantToolJson.SerializerOptions),
            IsError: true);
    }
}

/// <summary>
/// Lightweight reader over an agent's ConfigJson — the assistant tools only need provider, model,
/// kind, and the two prompt fields, so we avoid the heavier <c>AgentConfigJson.Deserialize</c>
/// path which round-trips through <c>AgentInvocationConfiguration</c> (and crashes on configs that
/// reference partials the runtime hasn't seen).
/// </summary>
internal sealed record AgentConfigJsonReader(string? Provider, string? Model, string? Kind, string? SystemPrompt, string? PromptTemplate)
{
    public static AgentConfigJsonReader Read(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return new AgentConfigJsonReader(null, null, null, null, null);
        }

        using var doc = JsonDocument.Parse(configJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AgentConfigJsonReader(null, null, null, null, null);
        }

        return new AgentConfigJsonReader(
            ReadString(root, "provider"),
            ReadString(root, "model"),
            ReadString(root, "type"),
            ReadString(root, "systemPrompt"),
            ReadString(root, "promptTemplate"));
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }
}
