using CodeFlow.Runtime;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

/// <summary>
/// End-to-end acceptance gate for Phase D: role grants → resolved tools → runtime invocation.
/// </summary>
public sealed class AgentRoleInvocationEndToEndTests : IAsyncLifetime
{
    private static readonly byte[] MasterKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private string? connectionString;

    public async Task InitializeAsync()
    {
        await mariaDbContainer.StartAsync();
        connectionString = mariaDbContainer.GetConnectionString();
        await using var ctx = CreateDbContext();
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task Agent_invoked_with_role_grants_executes_host_and_mcp_tools_and_rejects_others()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        var roleKey = $"role-{Guid.NewGuid():N}";
        var serverKey = $"artifacts-{Guid.NewGuid():N}";

        await SeedAsync(agentKey, roleKey, serverKey);

        var mcpClient = new RecordingMcpClient();
        var modelClient = new ToolScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Reading artifact.",
                    ToolCalls:
                    [
                        new ToolCall("call_read", $"mcp:{serverKey}:read", new JsonObject { ["uri"] = "file://a" })
                    ]),
                InvocationStopReason.ToolCalls),
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Echoing.",
                    ToolCalls: [new ToolCall("call_echo", "echo", new JsonObject { ["text"] = "hi" })]),
                InvocationStopReason.ToolCalls),
            _ => new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "All granted tools invoked."),
                InvocationStopReason.EndTurn),
        ]);

        var agent = new Agent(
            new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]),
            mcpClient: mcpClient);

        await using var ctx = CreateDbContext();
        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var tools = await resolver.ResolveAsync(agentKey);

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(Provider: "test", Model: "m"),
            "start",
            tools);

        result.Decision.Should().BeOfType<CompletedDecision>();
        result.ToolCallsExecuted.Should().Be(2);

        mcpClient.Invocations.Should().ContainSingle()
            .Which.Should().Match<RecordingMcpClient.McpInvocation>(i =>
                i.Server == serverKey && i.ToolName == "read");
    }

    [Fact]
    public async Task Agent_invocation_denies_tools_that_are_not_granted_by_any_assigned_role()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        var roleKey = $"role-{Guid.NewGuid():N}";
        var serverKey = $"svc-{Guid.NewGuid():N}";

        await SeedAsync(agentKey, roleKey, serverKey,
            grants: new[] { new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo") });

        var modelClient = new ToolScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Trying ungranted tool.",
                    ToolCalls:
                    [
                        new ToolCall("call_now", "now", new JsonObject())
                    ]),
                InvocationStopReason.ToolCalls),
            request =>
            {
                var toolMessage = request.Messages[^1];
                toolMessage.Role.Should().Be(ChatMessageRole.Tool);
                toolMessage.Content.Should().Contain("not available", "denied tools surface via UnknownToolException.Message");
                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "Denial observed; giving up."),
                    InvocationStopReason.EndTurn);
            },
        ]);

        var agent = new Agent(new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]));

        await using var ctx = CreateDbContext();
        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var tools = await resolver.ResolveAsync(agentKey);

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(Provider: "test", Model: "m"),
            "start",
            tools);

        result.Output.Should().Be("Denial observed; giving up.");
    }

    [Fact]
    public async Task Editing_a_role_mid_flight_takes_effect_on_the_next_invocation()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        var roleKey = $"role-{Guid.NewGuid():N}";
        var serverKey = $"svc-{Guid.NewGuid():N}";

        await SeedAsync(agentKey, roleKey, serverKey,
            grants: new[] { new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo") });

        await using var ctx1 = CreateDbContext();
        var resolver1 = new RoleResolutionService(ctx1, NullLogger<RoleResolutionService>.Instance);
        var beforeEdit = await resolver1.ResolveAsync(agentKey);
        beforeEdit.AllowedToolNames.Should().BeEquivalentTo(new[] { "echo" });

        await using (var ctx2 = CreateDbContext())
        {
            var roleRepo = new AgentRoleRepository(ctx2);
            var role = await roleRepo.GetByKeyAsync(roleKey);
            await roleRepo.ReplaceGrantsAsync(role!.Id, new[]
            {
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
            });
        }

        await using var ctx3 = CreateDbContext();
        var resolver3 = new RoleResolutionService(ctx3, NullLogger<RoleResolutionService>.Instance);
        var afterEdit = await resolver3.ResolveAsync(agentKey);

        afterEdit.AllowedToolNames.Should().BeEquivalentTo(new[] { "now" });
    }

    [Fact]
    public async Task Sub_agents_inherit_parent_resolved_tools()
    {
        var agentKey = $"parent-{Guid.NewGuid():N}";
        var roleKey = $"role-{Guid.NewGuid():N}";
        var serverKey = $"svc-{Guid.NewGuid():N}";

        await SeedAsync(agentKey, roleKey, serverKey,
            grants: new[] { new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo") });

        var subAgentInvocations = 0;

        var parentClient = new ToolScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Delegating.",
                    ToolCalls:
                    [
                        new ToolCall(
                            "call_spawn",
                            "spawn_subagent",
                            new JsonObject
                            {
                                ["invocations"] = new JsonArray
                                {
                                    new JsonObject { ["agent"] = "child", ["input"] = "please-echo" },
                                },
                            }),
                    ]),
                InvocationStopReason.ToolCalls),
            _ => new InvocationResponse(
                new ChatMessage(ChatMessageRole.Assistant, "Sub-agent finished."),
                InvocationStopReason.EndTurn),
        ]);

        var childClient = new ToolScriptedModelClient(
        [
            _ => new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "Using echo.",
                    ToolCalls: [new ToolCall("call_echo", "echo", new JsonObject { ["text"] = "hi" })]),
                InvocationStopReason.ToolCalls),
            _ =>
            {
                Interlocked.Increment(ref subAgentInvocations);
                return new InvocationResponse(
                    new ChatMessage(ChatMessageRole.Assistant, "child-done"),
                    InvocationStopReason.EndTurn);
            },
        ]);

        var agent = new Agent(new ModelClientRegistry(
        [
            new ModelClientRegistration("parent", parentClient),
            new ModelClientRegistration("child", childClient),
        ]));

        await using var ctx = CreateDbContext();
        var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
        var tools = await resolver.ResolveAsync(agentKey);

        var result = await agent.InvokeAsync(
            new AgentInvocationConfiguration(
                Provider: "parent",
                Model: "m",
                SubAgents: new Dictionary<string, AgentInvocationConfiguration>
                {
                    ["child"] = new AgentInvocationConfiguration(Provider: "child", Model: "m"),
                }),
            "go",
            tools);

        result.Decision.Should().BeOfType<CompletedDecision>();
        subAgentInvocations.Should().Be(1);
    }

    private async Task SeedAsync(
        string agentKey,
        string roleKey,
        string serverKey,
        AgentRoleToolGrant[]? grants = null)
    {
        await using var ctx = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var mcpRepo = new McpServerRepository(ctx, protector);
        var roleRepo = new AgentRoleRepository(ctx);

        var serverId = await mcpRepo.CreateAsync(new McpServerCreate(
            Key: serverKey,
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerTokenPlaintext: null,
            CreatedBy: null));

        await mcpRepo.ReplaceToolsAsync(serverId, new[]
        {
            new McpServerToolWrite("read", "read artifact", null, IsMutating: false),
        });

        var roleId = await roleRepo.CreateAsync(new AgentRoleCreate(roleKey, "R", null, null));
        await roleRepo.ReplaceGrantsAsync(roleId, grants ?? new[]
        {
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{serverKey}:read"),
        });
        await roleRepo.ReplaceAssignmentsAsync(agentKey, new[] { roleId });
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }

    private sealed class ToolScriptedModelClient(IReadOnlyList<Func<InvocationRequest, InvocationResponse>> steps) : IModelClient
    {
        private int nextStepIndex;

        public Task<InvocationResponse> InvokeAsync(InvocationRequest request, CancellationToken cancellationToken = default)
        {
            if (nextStepIndex >= steps.Count)
            {
                throw new InvalidOperationException("No scripted response remains.");
            }
            return Task.FromResult(steps[nextStepIndex++](request));
        }
    }

    private sealed class RecordingMcpClient : IMcpClient
    {
        public List<McpInvocation> Invocations { get; } = new();

        public Task<McpToolResult> InvokeAsync(string server, string toolName, JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add(new McpInvocation(server, toolName, arguments?.DeepClone()));
            return Task.FromResult(new McpToolResult("ok"));
        }

        public sealed record McpInvocation(string Server, string ToolName, JsonNode? Arguments);
    }
}
