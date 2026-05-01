using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Catalog-discovery tools for workflow authoring. Mirror the data the role-editor UI fetches from
/// /api/host-tools and /api/mcp-servers/* so the assistant can recommend specific tools to grant a
/// new agent role instead of guessing from training data. Read-only, no chip — these don't mutate
/// state, they just enumerate what already exists.
/// </summary>
file static class DiscoveryDefaults
{
    public const int DefaultListLimit = 25;
    public const int MaxListLimit = 100;
}

/// <summary>
/// Lists every host tool the platform ships (read_file, run_command, vcs.*, etc.) with full input
/// schemas so the model can recommend a fit for an agent role.
/// </summary>
public sealed class ListHostToolsTool : IAssistantTool
{
    public string Name => "list_host_tools";

    public string Description =>
        "List every host tool the platform exposes (the same catalog backing /api/host-tools and the " +
        "agent-role tool picker UI). Returns each tool's name, description, full JSON-schema input " +
        "shape, and isMutating flag. Use this when the user is authoring an agent role and wants to " +
        "know which built-in tools they can grant — host tools cover workspace I/O (read_file, " +
        "apply_patch, run_command), VCS operations (vcs.open_pr, vcs.get_repo), and a handful of " +
        "trivial helpers (echo, now). No arguments.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {},
        ""additionalProperties"": false
    }");

    public Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var catalog = HostToolProvider.GetCatalog();
        var payload = new
        {
            count = catalog.Count,
            tools = catalog
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.Parameters?.DeepClone(),
                    isMutating = t.IsMutating,
                })
                .ToArray(),
        };

        return Task.FromResult(new AssistantToolResult(
            JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions)));
    }
}

/// <summary>
/// Lists configured MCP servers — the second leg of the agent-role tool picker. Returns metadata
/// only (no per-server tool lists); call <c>list_mcp_server_tools</c> with a server id to drill in.
/// </summary>
public sealed class ListMcpServersTool(IMcpServerRepository repository) : IAssistantTool
{
    public string Name => "list_mcp_servers";

    public string Description =>
        "List MCP servers configured on this CodeFlow instance. Returns each server's id, key, " +
        "displayName, transport, endpointUrl, healthStatus (Unknown|Healthy|Unhealthy), " +
        "lastVerifiedAtUtc, lastVerificationError, isArchived, and toolCount (number of tools the " +
        "server has published; 0 if not yet refreshed). By default excludes archived servers. Use " +
        "this when the user is wiring tools to an agent role and needs to know which MCP servers " +
        "exist; follow up with `list_mcp_server_tools` to see a server's tools.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""includeArchived"": { ""type"": ""boolean"", ""description"": ""Include archived servers. Default false."" }
        },
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var includeArchived = AssistantToolJson.ReadOptionalBool(arguments, "includeArchived", defaultValue: false);
        var servers = await repository.ListAsync(includeArchived, cancellationToken);

        // Tool counts are per-server; query each row. Catalogs are small (handful of MCP servers
        // typical) so a sequential walk is fine — if this grows we can push the count into the
        // repository as a single grouped query.
        var rows = new List<object>(servers.Count);
        foreach (var server in servers)
        {
            var tools = await repository.GetToolsAsync(server.Id, cancellationToken);
            rows.Add(new
            {
                id = server.Id,
                key = server.Key,
                displayName = server.DisplayName,
                transport = server.Transport.ToString(),
                endpointUrl = server.EndpointUrl,
                healthStatus = server.HealthStatus.ToString(),
                lastVerifiedAtUtc = server.LastVerifiedAtUtc,
                lastVerificationError = server.LastVerificationError,
                isArchived = server.IsArchived,
                toolCount = tools.Count,
            });
        }

        var payload = new
        {
            count = rows.Count,
            servers = rows,
        };

        return new AssistantToolResult(
            JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }
}

/// <summary>
/// Lists the tools a single MCP server has published, with full parameter schemas. Looked up by
/// server id (long) or key (string) — exactly one. Mirrors GET /api/mcp-servers/{id}/tools.
/// </summary>
public sealed class ListMcpServerToolsTool(IMcpServerRepository repository) : IAssistantTool
{
    public string Name => "list_mcp_server_tools";

    public string Description =>
        "List the tools a single MCP server has published. Returns each tool's name, description, " +
        "full JSON-schema parameters, isMutating flag, and syncedAtUtc. Look the server up by " +
        "numeric `serverId` OR by string `serverKey` — exactly one must be supplied. Default limit " +
        "25, max 100. Use after `list_mcp_servers` to drill into a specific server's tool catalog " +
        "when authoring an agent role's MCP grants. The grant identifier the role expects is " +
        "`mcp:<serverKey>:<toolName>`.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""serverId"": { ""type"": ""integer"", ""description"": ""MCP server id (use either serverId or serverKey)."" },
            ""serverKey"": { ""type"": ""string"", ""description"": ""MCP server key (use either serverId or serverKey)."" },
            ""limit"": { ""type"": ""integer"", ""description"": ""Max tools to return. Default 25, max 100."" }
        },
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var serverId = AssistantToolJson.ReadOptionalInt(arguments, "serverId");
        var serverKey = AssistantToolJson.ReadOptionalString(arguments, "serverKey");

        if (serverId is null && serverKey is null)
        {
            return Error("Provide either `serverId` or `serverKey`.");
        }
        if (serverId is not null && serverKey is not null)
        {
            return Error("Provide either `serverId` or `serverKey`, not both.");
        }

        var server = serverId is { } id
            ? await repository.GetAsync(id, cancellationToken)
            : await repository.GetByKeyAsync(serverKey!, cancellationToken);

        if (server is null)
        {
            var label = serverId is { } missingId ? $"id={missingId}" : $"key='{serverKey}'";
            return Error($"MCP server {label} not found.");
        }

        var limit = AssistantToolJson.ClampLimit(
            AssistantToolJson.ReadOptionalInt(arguments, "limit"),
            DiscoveryDefaults.DefaultListLimit,
            DiscoveryDefaults.MaxListLimit);

        var tools = await repository.GetToolsAsync(server.Id, cancellationToken);
        var page = tools
            .OrderBy(t => t.ToolName, StringComparer.Ordinal)
            .Take(limit)
            .Select(t => new
            {
                name = t.ToolName,
                description = t.Description,
                parameters = string.IsNullOrEmpty(t.ParametersJson) ? null : TryParseParameters(t.ParametersJson),
                isMutating = t.IsMutating,
                syncedAtUtc = t.SyncedAtUtc,
                grantIdentifier = $"mcp:{server.Key}:{t.ToolName}",
            })
            .ToArray();

        var payload = new
        {
            serverId = server.Id,
            serverKey = server.Key,
            displayName = server.DisplayName,
            healthStatus = server.HealthStatus.ToString(),
            count = page.Length,
            limit,
            truncated = tools.Count > page.Length,
            tools = page,
        };

        return new AssistantToolResult(
            JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }

    private static JsonNode? TryParseParameters(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static AssistantToolResult Error(string message)
    {
        return new AssistantToolResult(
            JsonSerializer.Serialize(new { error = message }, AssistantToolJson.SerializerOptions),
            IsError: true);
    }
}
