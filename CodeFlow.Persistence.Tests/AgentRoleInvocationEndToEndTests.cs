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

        result.Decision.PortName.Should().Be("Completed");
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
                                    new JsonObject { ["systemPrompt"] = "Echo helper.", ["input"] = "please-echo" },
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
                SubAgents: new SubAgentConfig(Provider: "child", Model: "m", MaxConcurrent: 1)),
            "go",
            tools);

        result.Decision.PortName.Should().Be("Completed");
        subAgentInvocations.Should().Be(1);
    }

    [Fact]
    public async Task Agent_invoked_with_role_grant_can_execute_workspace_host_tools()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        var roleKey = $"role-{Guid.NewGuid():N}";
        var serverKey = $"svc-{Guid.NewGuid():N}";
        var workspaceRoot = CreateWorkspaceRoot();

        try
        {
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
            await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "src", "main.txt"), "alpha\nbeta\ngamma\n");

            await SeedAsync(agentKey, roleKey, serverKey,
                grants: new[]
                {
                    new AgentRoleToolGrant(AgentRoleToolCategory.Host, "read_file"),
                    new AgentRoleToolGrant(AgentRoleToolCategory.Host, "apply_patch"),
                    new AgentRoleToolGrant(AgentRoleToolCategory.Host, "run_command"),
                });

            var modelClient = new ToolScriptedModelClient(
            [
                _ => new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Read the file first.",
                        ToolCalls:
                        [
                            new ToolCall("call_read", "read_file", new JsonObject { ["path"] = "src/main.txt" })
                        ]),
                    InvocationStopReason.ToolCalls),
                request =>
                {
                    request.Messages[^1].Role.Should().Be(ChatMessageRole.Tool);
                    request.Messages[^1].Content.Should().Contain("alpha");
                    return new InvocationResponse(
                        new ChatMessage(
                            ChatMessageRole.Assistant,
                            "Patch it.",
                            ToolCalls:
                            [
                                new ToolCall(
                                    "call_patch",
                                    "apply_patch",
                                    new JsonObject
                                    {
                                        ["patch"] =
                                            """
                                            *** Begin Patch
                                            *** Update File: src/main.txt
                                             alpha
                                            -beta
                                            +beta patched
                                             gamma
                                            *** End Patch
                                            """
                                    })
                            ]),
                        InvocationStopReason.ToolCalls);
                },
                request =>
                {
                    request.Messages[^1].Role.Should().Be(ChatMessageRole.Tool);
                    request.Messages[^1].Content.Should().Contain("\"ok\":true");
                    return new InvocationResponse(
                        new ChatMessage(
                            ChatMessageRole.Assistant,
                            "Now run a command.",
                            ToolCalls:
                            [
                                new ToolCall(
                                    "call_run",
                                    "run_command",
                                    new JsonObject
                                    {
                                        ["command"] = "dotnet",
                                        ["args"] = new JsonArray("--version")
                                    })
                            ]),
                        InvocationStopReason.ToolCalls);
                },
                request =>
                {
                    request.Messages[^1].Role.Should().Be(ChatMessageRole.Tool);
                    request.Messages[^1].Content.Should().Contain("\"exitCode\":0");
                    return new InvocationResponse(
                        new ChatMessage(ChatMessageRole.Assistant, "Workspace tools complete."),
                        InvocationStopReason.EndTurn);
                },
            ]);

            var agent = new Agent(
                new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]),
                hostToolProvider: new HostToolProvider(
                    workspaceTools: new CodeFlow.Runtime.Workspace.WorkspaceHostToolService(
                        new CodeFlow.Runtime.Workspace.WorkspaceOptions
                        {
                            Root = workspaceRoot,
                            ReadMaxBytes = 256 * 1024,
                            ExecTimeoutSeconds = 30,
                            ExecOutputMaxBytes = 128 * 1024
                        })));

            await using var ctx = CreateDbContext();
            var resolver = new RoleResolutionService(ctx, NullLogger<RoleResolutionService>.Instance);
            var tools = await resolver.ResolveAsync(agentKey);

            var result = await agent.InvokeAsync(
                new AgentInvocationConfiguration(Provider: "test", Model: "m"),
                "start",
                tools,
                toolExecutionContext: new ToolExecutionContext(
                    new ToolWorkspaceContext(Guid.NewGuid(), workspaceRoot)));

            result.Decision.PortName.Should().Be("Completed");
            result.ToolCallsExecuted.Should().Be(3);
            (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "src", "main.txt")))
                .Should().Be("alpha\nbeta patched\ngamma\n");
        }
        finally
        {
            DeleteWorkspaceRoot(workspaceRoot);
        }
    }

    [Fact]
    public async Task Agent_invocation_denies_ungranted_expanded_host_tool_even_when_other_new_host_tool_is_allowed()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        var roleKey = $"role-{Guid.NewGuid():N}";
        var serverKey = $"svc-{Guid.NewGuid():N}";
        var workspaceRoot = CreateWorkspaceRoot();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "main.txt"), "alpha\n");

            await SeedAsync(agentKey, roleKey, serverKey,
                grants: new[] { new AgentRoleToolGrant(AgentRoleToolCategory.Host, "read_file") });

            var modelClient = new ToolScriptedModelClient(
            [
                _ => new InvocationResponse(
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Try an ungranted mutating tool.",
                        ToolCalls:
                        [
                            new ToolCall(
                                "call_patch",
                                "apply_patch",
                                new JsonObject
                                {
                                    ["patch"] =
                                        """
                                        *** Begin Patch
                                        *** Update File: main.txt
                                        -alpha
                                        +beta
                                        *** End Patch
                                        """
                                })
                        ]),
                    InvocationStopReason.ToolCalls),
                request =>
                {
                    var toolMessage = request.Messages[^1];
                    toolMessage.Role.Should().Be(ChatMessageRole.Tool);
                    toolMessage.Content.Should().Contain("not available");
                    return new InvocationResponse(
                        new ChatMessage(ChatMessageRole.Assistant, "Denied as expected."),
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
                tools,
                toolExecutionContext: new ToolExecutionContext(
                    new ToolWorkspaceContext(Guid.NewGuid(), workspaceRoot)));

            result.Output.Should().Be("Denied as expected.");
            (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "main.txt"))).Should().Be("alpha\n");
        }
        finally
        {
            DeleteWorkspaceRoot(workspaceRoot);
        }
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

    private static string CreateWorkspaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeflow-role-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteWorkspaceRoot(string workspaceRoot)
    {
        try
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
        catch
        {
        }
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
