using CodeFlow.Api.Tests.Integration;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Tests.EndToEnd;

/// <summary>
/// AP-10 (sc-841): full agent-package round trip exercised end-to-end against the live API.
/// <para/>
/// Drives the same surfaces a user would touch: seed an agent + role + skill + MCP server +
/// role assignment via the public endpoints, GET the canonical package, rewrite every key
/// to a fresh "import-target" namespace, POST the rewritten package through preview + apply,
/// and verify the rebuilt entities show up in the registry at parity.
/// <para/>
/// Notes:
/// <list type="bullet">
///   <item>The test factory shares state across tests, so each entity keys off
///     <see cref="Guid.NewGuid"/> to keep concurrent test runs isolated.</item>
///   <item>The "drop entities" half of the AP-10 card is replaced by key-rewriting against the
///     same DB — equivalent fidelity (an apply against keys that don't exist yet, asserting
///     all rows land as Create) and avoids tearing down shared fixtures mid-collection.</item>
///   <item>E2E #2 (assistant-authoring → save_agent_package → apply-from-artifact) is split
///     out to the AP-2 follow-up PR that lands the agent-side bridge endpoints; until those
///     ship there is no apply-from-artifact endpoint to land the snapshot bytes on. The
///     existing <c>AgentPackageDraftToolsTests</c> + <c>AssistantArtifactRepositoryTests</c>
///     already cover the producer side of that flow.</item>
/// </list>
/// </summary>
[Trait("Category", "EndToEnd")]
[Collection("CodeFlowApi")]
public sealed class AgentPackageRoundTripTests
{
    private readonly CodeFlowApiFactory factory;

    public AgentPackageRoundTripTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task RoundTrip_SeededAgent_ExportsAndImportsAtParity()
    {
        using var client = factory.CreateClient();

        // ----- Seed: agent + skill + MCP server + role (with Host + Mcp + Skill grants) +
        //              role assignment.
        var sourceSuffix = Guid.NewGuid().ToString("N")[..10];
        var sourceAgentKey = $"e2e-agent-{sourceSuffix}";
        var sourceRoleKey = $"e2e-role-{sourceSuffix}";
        var sourceSkillName = $"e2e-skill-{sourceSuffix}";
        var sourceMcpKey = $"e2e-mcp-{sourceSuffix}";

        // Skill.
        var skillCreate = await client.PostAsJsonAsync("/api/skills", new
        {
            name = sourceSkillName,
            body = "Always classify the request first.",
        });
        skillCreate.StatusCode.Should().Be(HttpStatusCode.Created);
        var skillId = (await skillCreate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt64();

        // MCP server (with one tool).
        var mcpCreate = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            key = sourceMcpKey,
            displayName = "E2E MCP",
            transport = "StreamableHttp",
            endpointUrl = "https://e2e.local/mcp",
            tools = new object[]
            {
                new
                {
                    toolName = "search",
                    description = "search the index",
                    parametersJson = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""",
                    isMutating = false,
                },
            },
        });
        mcpCreate.StatusCode.Should().Be(HttpStatusCode.Created);

        // Role with Host + Mcp grants and the skill grant.
        var roleCreate = await client.PostAsJsonAsync("/api/agent-roles", new
        {
            key = sourceRoleKey,
            displayName = "E2E Role",
            description = "End-to-end test role.",
            tags = new[] { "e2e" },
        });
        roleCreate.StatusCode.Should().Be(HttpStatusCode.Created);
        var roleId = (await roleCreate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt64();

        // /tools accepts the grant list directly (no wrapper object).
        var grantsReplace = await client.PutAsJsonAsync($"/api/agent-roles/{roleId}/tools", new object[]
        {
            new { category = "Host", toolIdentifier = "read_file" },
            new { category = "Mcp", toolIdentifier = $"mcp:{sourceMcpKey}:search" },
        });
        grantsReplace.StatusCode.Should().Be(HttpStatusCode.OK);

        var skillsReplace = await client.PutAsJsonAsync($"/api/agent-roles/{roleId}/skills", new
        {
            skillIds = new[] { skillId },
        });
        skillsReplace.StatusCode.Should().Be(HttpStatusCode.OK);

        // Agent.
        var agentCreate = await client.PostAsJsonAsync("/api/agents", new
        {
            key = sourceAgentKey,
            tags = new[] { "e2e" },
            config = new
            {
                provider = "openai",
                model = "gpt-5",
                systemPrompt = "Round-trip me.",
            },
        });
        agentCreate.StatusCode.Should().Be(HttpStatusCode.Created);

        // Role assignment.
        var assign = await client.PutAsJsonAsync($"/api/agents/{sourceAgentKey}/roles", new
        {
            roleIds = new[] { roleId },
        });
        assign.StatusCode.Should().Be(HttpStatusCode.OK);

        // ----- Export: GET /package returns a self-contained bundle.
        var packageJson = await client.GetStringAsync($"/api/agents/{sourceAgentKey}/1/package");
        var packageNode = JsonNode.Parse(packageJson)!.AsObject();

        packageNode["schemaVersion"]!.GetValue<string>().Should().Be("codeflow.agent-package.v1");
        packageNode["agents"]!.AsArray().Should().HaveCount(1);
        packageNode["roles"]!.AsArray().Should().HaveCount(1);
        packageNode["skills"]!.AsArray().Should().HaveCount(1);
        packageNode["mcpServers"]!.AsArray().Should().HaveCount(1);

        // ----- Rewrite every key to a fresh "import-target" namespace so the apply lands as
        // Create rows on entities that don't exist on the shared fixture DB yet.
        var targetSuffix = Guid.NewGuid().ToString("N")[..10];
        var targetAgentKey = $"e2e-agent-{targetSuffix}";
        var targetRoleKey = $"e2e-role-{targetSuffix}";
        var targetSkillName = $"e2e-skill-{targetSuffix}";
        var targetMcpKey = $"e2e-mcp-{targetSuffix}";

        RewritePackageKeys(
            packageNode,
            sourceAgentKey, targetAgentKey,
            sourceRoleKey, targetRoleKey,
            sourceSkillName, targetSkillName,
            sourceMcpKey, targetMcpKey);

        // ----- Preview: every row should be Create on the fresh target namespace.
        var previewBody = new JsonObject { ["package"] = packageNode.DeepClone() };
        var previewResponse = await client.PostAsync(
            "/api/agents/package/preview",
            new StringContent(previewBody.ToJsonString(), Encoding.UTF8, "application/json"));
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var previewDoc = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var preview = previewDoc.RootElement;
        preview.GetProperty("conflictCount").GetInt32().Should().Be(0);
        preview.GetProperty("refusedCount").GetInt32().Should().Be(0);
        preview.GetProperty("canApply").GetBoolean().Should().BeTrue();

        var actions = preview.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("action").GetString())
            .ToArray();
        actions.Should().NotBeEmpty();
        actions.Should().OnlyContain(action => action == "Create",
            because: "all five entities are under fresh keys; no row should reuse the source set");

        // ----- Apply: round-trip lands every entity at v1 in the new namespace.
        var applyBody = new JsonObject { ["package"] = packageNode.DeepClone() };
        var applyResponse = await client.PostAsync(
            "/api/agents/package/apply",
            new StringContent(applyBody.ToJsonString(), Encoding.UTF8, "application/json"));
        applyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var applyDoc = JsonDocument.Parse(await applyResponse.Content.ReadAsStringAsync());
        applyDoc.RootElement.GetProperty("conflictCount").GetInt32().Should().Be(0);
        applyDoc.RootElement.GetProperty("createCount").GetInt32().Should().Be(actions.Length);

        // ----- Verify parity: each entity appears at the new key, the agent's tags survived,
        // and the agent has the expected role assignment.
        var importedAgent = await client.GetFromJsonAsync<JsonElement>(
            $"/api/agents/{targetAgentKey}/1");
        importedAgent.GetProperty("key").GetString().Should().Be(targetAgentKey);
        importedAgent.GetProperty("version").GetInt32().Should().Be(1);
        importedAgent.GetProperty("tags").EnumerateArray()
            .Select(t => t.GetString()).Should().Contain("e2e");

        var importedRoles = await client.GetFromJsonAsync<JsonElement>(
            $"/api/agents/{targetAgentKey}/roles");
        importedRoles.EnumerateArray()
            .Select(r => r.GetProperty("key").GetString())
            .Should().Equal(targetRoleKey);

        // Skills + MCP servers exist under their new identifiers.
        var skills = await client.GetFromJsonAsync<JsonElement>("/api/skills");
        skills.EnumerateArray()
            .Select(s => s.GetProperty("name").GetString())
            .Should().Contain(targetSkillName);

        var mcpServers = await client.GetFromJsonAsync<JsonElement>("/api/mcp-servers");
        mcpServers.EnumerateArray()
            .Select(m => m.GetProperty("key").GetString())
            .Should().Contain(targetMcpKey);
    }

    /// <summary>
    /// Walk the exported package JSON and rename every (agent, role, skill, MCP server) key
    /// to its target namespace. Mirrors the workflow-side <c>RewritePackageKeys</c> helper
    /// in <c>WorkflowsEndpointsTests</c> — same pattern, different field set (no workflow
    /// nodes / edges to chase).
    /// </summary>
    private static void RewritePackageKeys(
        JsonObject pkg,
        string fromAgentKey, string toAgentKey,
        string fromRoleKey, string toRoleKey,
        string fromSkillName, string toSkillName,
        string fromMcpKey, string toMcpKey)
    {
        // Entry point.
        if (pkg["entryPoint"] is JsonObject entry)
        {
            ReplaceStringIfMatch(entry, "key", fromAgentKey, toAgentKey);
        }

        // Agents.
        foreach (var agent in pkg["agents"]!.AsArray())
        {
            if (agent is not JsonObject obj) continue;
            ReplaceStringIfMatch(obj, "key", fromAgentKey, toAgentKey);
        }

        // Role assignments.
        foreach (var assignment in pkg["agentRoleAssignments"]!.AsArray())
        {
            if (assignment is not JsonObject obj) continue;
            ReplaceStringIfMatch(obj, "agentKey", fromAgentKey, toAgentKey);
            if (obj["roleKeys"] is JsonArray roleKeys)
            {
                for (var i = 0; i < roleKeys.Count; i++)
                {
                    if (roleKeys[i]?.GetValue<string>() == fromRoleKey)
                    {
                        roleKeys[i] = JsonValue.Create(toRoleKey);
                    }
                }
            }
        }

        // Roles.
        foreach (var role in pkg["roles"]!.AsArray())
        {
            if (role is not JsonObject obj) continue;
            ReplaceStringIfMatch(obj, "key", fromRoleKey, toRoleKey);
            if (obj["skillNames"] is JsonArray skillNames)
            {
                for (var i = 0; i < skillNames.Count; i++)
                {
                    if (skillNames[i]?.GetValue<string>() == fromSkillName)
                    {
                        skillNames[i] = JsonValue.Create(toSkillName);
                    }
                }
            }
            if (obj["toolGrants"] is JsonArray grants)
            {
                foreach (var grant in grants)
                {
                    if (grant is not JsonObject grantObj) continue;
                    var ident = grantObj["toolIdentifier"]?.GetValue<string>();
                    if (ident is null) continue;
                    if (ident.StartsWith($"mcp:{fromMcpKey}:", StringComparison.Ordinal))
                    {
                        grantObj["toolIdentifier"] = ident.Replace(
                            $"mcp:{fromMcpKey}:",
                            $"mcp:{toMcpKey}:",
                            StringComparison.Ordinal);
                    }
                }
            }
        }

        // Skills.
        foreach (var skill in pkg["skills"]!.AsArray())
        {
            if (skill is not JsonObject obj) continue;
            ReplaceStringIfMatch(obj, "name", fromSkillName, toSkillName);
        }

        // MCP servers.
        foreach (var server in pkg["mcpServers"]!.AsArray())
        {
            if (server is not JsonObject obj) continue;
            ReplaceStringIfMatch(obj, "key", fromMcpKey, toMcpKey);
        }

        // Manifest mirrors the typed collections.
        if (pkg["manifest"] is JsonObject manifest)
        {
            if (manifest["agent"] is JsonObject manifestAgent)
            {
                ReplaceStringIfMatch(manifestAgent, "key", fromAgentKey, toAgentKey);
            }
            ReplaceStringInArray(manifest["roles"] as JsonArray, fromRoleKey, toRoleKey);
            ReplaceStringInArray(manifest["skills"] as JsonArray, fromSkillName, toSkillName);
            ReplaceStringInArray(manifest["mcpServers"] as JsonArray, fromMcpKey, toMcpKey);
        }
    }

    private static void ReplaceStringIfMatch(JsonObject obj, string property, string from, string to)
    {
        if (obj[property]?.GetValue<string>() == from)
        {
            obj[property] = JsonValue.Create(to);
        }
    }

    private static void ReplaceStringInArray(JsonArray? array, string from, string to)
    {
        if (array is null) return;
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i]?.GetValue<string>() == from)
            {
                array[i] = JsonValue.Create(to);
            }
        }
    }
}
