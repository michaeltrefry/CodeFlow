using CodeFlow.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace CodeFlow.Persistence;

public sealed class RoleResolutionService : IRoleResolutionService
{
    private static readonly HashSet<string> HostToolNames = HostToolProvider.GetCatalog()
        .Select(tool => tool.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly CodeFlowDbContext dbContext;
    private readonly ILogger<RoleResolutionService> logger;

    public RoleResolutionService(CodeFlowDbContext dbContext, ILogger<RoleResolutionService> logger)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(logger);
        this.dbContext = dbContext;
        this.logger = logger;
    }

    public async Task<ResolvedAgentTools> ResolveAsync(string agentKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);
        var normalized = agentKey.Trim();

        var grants = await (
            from assignment in dbContext.AgentRoleAssignments.AsNoTracking()
            join grant in dbContext.AgentRoleToolGrants.AsNoTracking()
                on assignment.RoleId equals grant.RoleId
            where assignment.AgentKey == normalized
                && !assignment.Role.IsArchived
            select new GrantView(assignment.Role.Key, grant.Category, grant.ToolIdentifier))
            .ToListAsync(cancellationToken);

        if (grants.Count == 0)
        {
            return ResolvedAgentTools.Empty;
        }

        var enableHostTools = false;
        var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mcpIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostGrants = new List<GrantView>();
        var mcpGrants = new List<GrantView>();

        foreach (var grant in grants)
        {
            switch (grant.Category)
            {
                case AgentRoleToolCategory.Host:
                    hostGrants.Add(grant);
                    break;
                case AgentRoleToolCategory.Mcp:
                    mcpGrants.Add(grant);
                    break;
            }
        }

        foreach (var grant in hostGrants)
        {
            if (!HostToolNames.Contains(grant.ToolIdentifier))
            {
                logger.LogWarning(
                    "Role '{RoleKey}' grants host tool '{Tool}' which is not in the host tool catalog; skipping.",
                    grant.RoleKey,
                    grant.ToolIdentifier);
                continue;
            }

            allowedNames.Add(grant.ToolIdentifier);
            enableHostTools = true;
        }

        IReadOnlyList<McpToolDefinition> mcpTools = Array.Empty<McpToolDefinition>();

        if (mcpGrants.Count > 0)
        {
            foreach (var grant in mcpGrants)
            {
                mcpIdentifiers.Add(grant.ToolIdentifier);
            }

            var (servers, toolsByServer) = await LoadMcpCatalogAsync(cancellationToken);
            var resolvedTools = new List<McpToolDefinition>();

            foreach (var grant in mcpGrants)
            {
                var parts = grant.ToolIdentifier.Split(':', 3);
                if (parts.Length != 3 || !string.Equals(parts[0], "mcp", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Role '{RoleKey}' grants malformed MCP identifier '{Identifier}'; expected 'mcp:<server>:<tool>'. Skipping.",
                        grant.RoleKey,
                        grant.ToolIdentifier);
                    continue;
                }

                var serverKey = parts[1];
                var toolName = parts[2];

                if (!servers.TryGetValue(serverKey, out var server) || server.IsArchived)
                {
                    logger.LogWarning(
                        "Role '{RoleKey}' grants MCP tool '{Identifier}' but server '{ServerKey}' is missing or archived; skipping.",
                        grant.RoleKey,
                        grant.ToolIdentifier,
                        serverKey);
                    continue;
                }

                if (!toolsByServer.TryGetValue(server.Id, out var toolMap)
                    || !toolMap.TryGetValue(toolName, out var toolRow))
                {
                    logger.LogWarning(
                        "Role '{RoleKey}' grants MCP tool '{Identifier}' but server '{ServerKey}' has no tool '{ToolName}'. Ghost tool skipped.",
                        grant.RoleKey,
                        grant.ToolIdentifier,
                        serverKey,
                        toolName);
                    continue;
                }

                var parameters = string.IsNullOrEmpty(toolRow.ParametersJson)
                    ? null
                    : TryParseNode(toolRow.ParametersJson);

                resolvedTools.Add(new McpToolDefinition(
                    Server: serverKey,
                    ToolName: toolName,
                    Description: toolRow.Description ?? string.Empty,
                    Parameters: parameters,
                    IsMutating: toolRow.IsMutating));

                allowedNames.Add(grant.ToolIdentifier);
            }

            mcpTools = resolvedTools;
        }

        return new ResolvedAgentTools(allowedNames, mcpTools, enableHostTools);
    }

    private async Task<(IReadOnlyDictionary<string, McpServerLookup> Servers,
                       IReadOnlyDictionary<long, IReadOnlyDictionary<string, McpToolLookup>> ToolsByServer)>
        LoadMcpCatalogAsync(CancellationToken cancellationToken)
    {
        var servers = await dbContext.McpServers
            .AsNoTracking()
            .Select(server => new McpServerLookup(server.Id, server.Key, server.IsArchived))
            .ToListAsync(cancellationToken);

        var serverDictionary = servers.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        var tools = await dbContext.McpServerTools
            .AsNoTracking()
            .Select(tool => new McpToolLookup(tool.ServerId, tool.ToolName, tool.Description, tool.ParametersJson, tool.IsMutating))
            .ToListAsync(cancellationToken);

        var toolsByServer = tools
            .GroupBy(tool => tool.ServerId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, McpToolLookup>)group.ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase));

        return (serverDictionary, toolsByServer);
    }

    private static JsonNode? TryParseNode(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private sealed record GrantView(string RoleKey, AgentRoleToolCategory Category, string ToolIdentifier);
    private sealed record McpServerLookup(long Id, string Key, bool IsArchived);
    private sealed record McpToolLookup(long ServerId, string ToolName, string? Description, string? ParametersJson, bool IsMutating);
}
