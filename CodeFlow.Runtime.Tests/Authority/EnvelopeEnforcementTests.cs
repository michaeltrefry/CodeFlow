using System.Text.Json.Nodes;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Authority;

/// <summary>
/// sc-269 PR3 — verifies that the resolved <see cref="WorkflowExecutionEnvelope"/> reaches the
/// tool layer through <see cref="ToolExecutionContext"/> and is honoured by every enforcement
/// surface: workspace command allowlist (ExecuteGrants/Workspace.CommandAllowlist), VCS repo
/// access (RepoScopes), MCP network egress (Network), and the agent-level tool access policy
/// (ToolGrants). Each surface keeps its pre-PR3 behaviour when the envelope is null so legacy
/// callers — and standalone fixtures that don't go through <c>AgentInvocationConsumer</c> —
/// don't regress.
/// </summary>
public sealed class EnvelopeEnforcementTests : IDisposable
{
    private readonly string workspaceRoot;

    public EnvelopeEnforcementTests()
    {
        workspaceRoot = Path.Combine(Path.GetTempPath(), "codeflow-envelope-enforce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
        catch
        {
        }
    }

    // ---------- Workspace: ExecuteGrants ----------

    [Fact]
    public async Task RunCommand_WhenEnvelopeExecuteGrantsAllow_PermitsCall()
    {
        var service = NewWorkspaceService(staticAllowlist: null);
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            ExecuteGrants = new[] { new ExecuteGrant("dotnet") }
        };

        var result = await service.RunCommandAsync(
            BuildRunCommandCall("dotnet", "--version"),
            NewWorkspaceContext(envelope));

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task RunCommand_WhenEnvelopeExecuteGrantsExcludesCommand_RefusesWithEnvelopeCode()
    {
        var service = NewWorkspaceService(staticAllowlist: null);
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            ExecuteGrants = new[] { new ExecuteGrant("dotnet") }
        };

        var result = await service.RunCommandAsync(
            BuildRunCommandCall("rm", "-rf", "/"),
            NewWorkspaceContext(envelope));

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!.AsObject();
        refusal["code"]!.GetValue<string>().Should().Be("envelope-execute-grants");
        refusal["axis"]!.GetValue<string>().Should().Be(BlockedBy.Axes.ExecuteGrants);
        refusal["command"]!.GetValue<string>().Should().Be("rm");
        refusal["allowed"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo(new[] { "dotnet" });
    }

    [Fact]
    public async Task RunCommand_WhenEnvelopeExecuteGrantsEmpty_DeniesAll()
    {
        // Empty ExecuteGrants from the resolver means "tier intersection emptied this axis";
        // strict deny is the right semantic so an envelope-driven empty list is not silently
        // demoted to "no enforcement" the way the legacy static-config empty list is.
        var service = NewWorkspaceService(staticAllowlist: null);
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            ExecuteGrants = Array.Empty<ExecuteGrant>()
        };

        var result = await service.RunCommandAsync(
            BuildRunCommandCall("dotnet", "--version"),
            NewWorkspaceContext(envelope));

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("envelope-execute-grants");
    }

    [Fact]
    public async Task RunCommand_EnvelopeExecuteGrantsTrumpsStaticConfig()
    {
        // Static config grants 'rm' but the envelope's ExecuteGrants does not — envelope wins.
        var service = NewWorkspaceService(staticAllowlist: new List<string> { "rm" });
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            ExecuteGrants = new[] { new ExecuteGrant("dotnet") }
        };

        var result = await service.RunCommandAsync(
            BuildRunCommandCall("rm"),
            NewWorkspaceContext(envelope));

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("envelope-execute-grants");
    }

    // ---------- Workspace: Workspace.CommandAllowlist (secondary envelope axis) ----------

    [Fact]
    public async Task RunCommand_WhenEnvelopeWorkspaceAllowlistGrants_PermitsCall()
    {
        var service = NewWorkspaceService(staticAllowlist: null);
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            Workspace = new EnvelopeWorkspace(
                CodeFlow.Runtime.Authority.WorkspaceSymlinkPolicy.RefuseForMutation,
                CommandAllowlist: new[] { "dotnet" })
        };

        var result = await service.RunCommandAsync(
            BuildRunCommandCall("dotnet", "--version"),
            NewWorkspaceContext(envelope));

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task RunCommand_WhenEnvelopeWorkspaceAllowlistExcludesCommand_RefusesWithWorkspaceCode()
    {
        var service = NewWorkspaceService(staticAllowlist: null);
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            Workspace = new EnvelopeWorkspace(
                CodeFlow.Runtime.Authority.WorkspaceSymlinkPolicy.RefuseForMutation,
                CommandAllowlist: new[] { "dotnet" })
        };

        var result = await service.RunCommandAsync(
            BuildRunCommandCall("rm"),
            NewWorkspaceContext(envelope));

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!.AsObject();
        refusal["code"]!.GetValue<string>().Should().Be("envelope-workspace-allowlist");
        refusal["axis"]!.GetValue<string>().Should().Be(BlockedBy.Axes.Workspace);
    }

    // ---------- Workspace: back-compat with static config ----------

    [Fact]
    public async Task RunCommand_WhenEnvelopeNull_StaticAllowlistStillEnforces()
    {
        // sc-270 path — when the envelope is silent, static config is authoritative.
        var service = NewWorkspaceService(staticAllowlist: new List<string> { "dotnet" });

        var allowed = await service.RunCommandAsync(
            BuildRunCommandCall("dotnet", "--version"),
            NewWorkspaceContext(envelope: null));
        allowed.IsError.Should().BeFalse();

        var denied = await service.RunCommandAsync(
            BuildRunCommandCall("rm"),
            NewWorkspaceContext(envelope: null));
        denied.IsError.Should().BeTrue();
        JsonNode.Parse(denied.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("command-allowlist");
    }

    // ---------- VCS: RepoScopes ----------

    [Fact]
    public async Task OpenPullRequest_WhenEnvelopeRepoScopesGrantWriteAccess_PermitsCall()
    {
        var stub = new StubVcsProvider
        {
            OpenPrResult = new PullRequestInfo("https://example.com/pulls/9", 9)
        };
        var service = new VcsHostToolService(new SingleProviderFactory(stub));
        var identityKey = RepoReference.Parse("https://github.com/foo/bar.git").IdentityKey;
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            RepoScopes = new[]
            {
                new RepoScopeGrant(identityKey, "github.com/foo/bar", RepoAccess.Write)
            }
        };

        var result = await service.OpenPullRequestAsync(
            BuildOpenPrCall(),
            BuildVcsContext("foo", "bar", envelope));

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task OpenPullRequest_WhenEnvelopeRepoScopesOnlyGrantReadAccess_RefusesMutation()
    {
        var stub = new StubVcsProvider();
        var service = new VcsHostToolService(new SingleProviderFactory(stub));
        var identityKey = RepoReference.Parse("https://github.com/foo/bar.git").IdentityKey;
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            RepoScopes = new[]
            {
                new RepoScopeGrant(identityKey, "github.com/foo/bar", RepoAccess.Read)
            }
        };

        var result = await service.OpenPullRequestAsync(
            BuildOpenPrCall(),
            BuildVcsContext("foo", "bar", envelope));

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!.AsObject();
        refusal["code"]!.GetValue<string>().Should().Be("envelope-repo-scope");
        refusal["axis"]!.GetValue<string>().Should().Be(BlockedBy.Axes.RepoScopes);
        refusal["repo"]!.GetValue<string>().Should().Be("foo/bar");
        refusal["requiredAccess"]!.GetValue<string>().Should().Be(nameof(RepoAccess.Write));
        stub.LastOpenPrCall.Should().BeNull();
    }

    [Fact]
    public async Task OpenPullRequest_WhenEnvelopeRepoScopesDoesNotMatchRepo_Refuses()
    {
        var stub = new StubVcsProvider();
        var service = new VcsHostToolService(new SingleProviderFactory(stub));
        var otherIdentity = RepoReference.Parse("https://github.com/other/repo.git").IdentityKey;
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            RepoScopes = new[]
            {
                new RepoScopeGrant(otherIdentity, "github.com/other/repo", RepoAccess.Write)
            }
        };

        var result = await service.OpenPullRequestAsync(
            BuildOpenPrCall(),
            BuildVcsContext("foo", "bar", envelope));

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("envelope-repo-scope");
    }

    [Fact]
    public async Task GetRepoMetadata_WhenEnvelopeGrantsReadAccess_PermitsCall()
    {
        var stub = new StubVcsProvider();
        var service = new VcsHostToolService(new SingleProviderFactory(stub));
        var identityKey = RepoReference.Parse("https://github.com/foo/bar.git").IdentityKey;
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            RepoScopes = new[]
            {
                new RepoScopeGrant(identityKey, "github.com/foo/bar", RepoAccess.Read)
            }
        };

        var result = await service.GetRepoMetadataAsync(
            new ToolCall("c", "vcs.get_repo", new JsonObject
            {
                ["owner"] = "foo",
                ["name"] = "bar"
            }),
            BuildVcsContext("foo", "bar", envelope));

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task GetRepoMetadata_WhenEnvelopeRepoScopesNull_BackCompatFallsThroughToTraceContext()
    {
        var stub = new StubVcsProvider();
        var service = new VcsHostToolService(new SingleProviderFactory(stub));

        var result = await service.GetRepoMetadataAsync(
            new ToolCall("c", "vcs.get_repo", new JsonObject
            {
                ["owner"] = "foo",
                ["name"] = "bar"
            }),
            BuildVcsContext("foo", "bar", envelope: null));

        result.IsError.Should().BeFalse();
    }

    // ---------- MCP: Network ----------

    [Fact]
    public async Task McpInvocation_WhenEnvelopeNetworkIsNone_RefusesWithoutCallingClient()
    {
        var client = new RecordingMcpClient();
        var provider = new McpToolProvider(
            client,
            new[] { new McpToolDefinition("docs", "search", "Search docs.", new JsonObject()) });
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            Network = EnvelopeNetwork.Denied
        };

        var result = await provider.InvokeAsync(
            new ToolCall("c", "mcp:docs:search", new JsonObject { ["query"] = "x" }),
            context: new ToolExecutionContext(Envelope: envelope));

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!.AsObject();
        refusal["code"]!.GetValue<string>().Should().Be("envelope-network");
        refusal["axis"]!.GetValue<string>().Should().Be(BlockedBy.Axes.Network);
        refusal["tool"]!.GetValue<string>().Should().Be("mcp:docs:search");
        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task McpInvocation_WhenEnvelopeNetworkIsAllowlist_DelegatesToClient()
    {
        var client = new RecordingMcpClient();
        var provider = new McpToolProvider(
            client,
            new[] { new McpToolDefinition("docs", "search", "Search docs.", new JsonObject()) });
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            Network = EnvelopeNetwork.Permissive
        };

        var result = await provider.InvokeAsync(
            new ToolCall("c", "mcp:docs:search", new JsonObject { ["query"] = "x" }),
            context: new ToolExecutionContext(Envelope: envelope));

        result.IsError.Should().BeFalse();
        client.Calls.Should().ContainSingle()
            .Which.Server.Should().Be("docs");
    }

    [Fact]
    public async Task McpInvocation_WhenEnvelopeNull_BackCompatDelegatesToClient()
    {
        var client = new RecordingMcpClient();
        var provider = new McpToolProvider(
            client,
            new[] { new McpToolDefinition("docs", "search", "Search docs.", new JsonObject()) });

        var result = await provider.InvokeAsync(
            new ToolCall("c", "mcp:docs:search", new JsonObject { ["query"] = "x" }),
            context: null);

        result.IsError.Should().BeFalse();
        client.Calls.Should().ContainSingle();
    }

    // ---------- Agent.MergeToolAccessPolicy via end-to-end Agent.InvokeAsync ----------

    [Fact]
    public async Task AgentInvoke_WhenEnvelopeToolGrantsPresent_OverridesRoleAllowedTools()
    {
        // Role grants 'echo'; envelope only grants 'now'. Envelope is authoritative — the model
        // should see only 'now' (plus the runtime meta tools submit/fail/setContext/setWorkflow).
        var captured = new List<string>();
        var modelClient = new ToolCatalogCapturingClient(captured);
        var agent = new Agent(new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]));
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            ToolGrants = new[] { new ToolGrant("now", ToolGrant.CategoryHost) }
        };

        await agent.InvokeAsync(
            new AgentInvocationConfiguration(Provider: "test", Model: "m"),
            input: "hello",
            tools: new ResolvedAgentTools(
                AllowedToolNames: new[] { "echo" },
                McpTools: Array.Empty<McpToolDefinition>(),
                EnableHostTools: true),
            cancellationToken: default,
            toolExecutionContext: new ToolExecutionContext(Envelope: envelope));

        captured.Should().NotContain("echo");
        captured.Should().Contain("now");
    }

    [Fact]
    public async Task AgentInvoke_WhenEnvelopeNull_FallsBackToRoleAllowedTools()
    {
        var captured = new List<string>();
        var modelClient = new ToolCatalogCapturingClient(captured);
        var agent = new Agent(new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]));

        await agent.InvokeAsync(
            new AgentInvocationConfiguration(Provider: "test", Model: "m"),
            input: "hello",
            tools: new ResolvedAgentTools(
                AllowedToolNames: new[] { "echo" },
                McpTools: Array.Empty<McpToolDefinition>(),
                EnableHostTools: true),
            cancellationToken: default,
            toolExecutionContext: new ToolExecutionContext(Envelope: null));

        captured.Should().Contain("echo");
        captured.Should().NotContain("now");
    }

    [Fact]
    public async Task AgentInvoke_WhenEnvelopeToolGrantsEmpty_NoExternalToolsVisible()
    {
        // An empty envelope ToolGrants list = "tier intersection denied everything".
        var captured = new List<string>();
        var modelClient = new ToolCatalogCapturingClient(captured);
        var agent = new Agent(new ModelClientRegistry([new ModelClientRegistration("test", modelClient)]));
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            ToolGrants = Array.Empty<ToolGrant>()
        };

        await agent.InvokeAsync(
            new AgentInvocationConfiguration(Provider: "test", Model: "m"),
            input: "hello",
            tools: new ResolvedAgentTools(
                AllowedToolNames: new[] { "echo", "now" },
                McpTools: Array.Empty<McpToolDefinition>(),
                EnableHostTools: true),
            cancellationToken: default,
            toolExecutionContext: new ToolExecutionContext(Envelope: envelope));

        captured.Should().NotContain("echo");
        captured.Should().NotContain("now");
        // Runtime meta tools always present regardless of envelope.
        captured.Should().Contain("submit");
    }

    // ---------- helpers ----------

    private WorkspaceHostToolService NewWorkspaceService(IList<string>? staticAllowlist) =>
        new(new WorkspaceOptions
        {
            Root = workspaceRoot,
            ReadMaxBytes = 64 * 1024,
            ExecTimeoutSeconds = 30,
            ExecOutputMaxBytes = 64 * 1024,
            CommandAllowlist = staticAllowlist,
            SymlinkPolicy = CodeFlow.Runtime.Workspace.WorkspaceSymlinkPolicy.RefuseForMutation
        });

    private ToolExecutionContext NewWorkspaceContext(WorkflowExecutionEnvelope? envelope) =>
        new(
            new ToolWorkspaceContext(Guid.NewGuid(), workspaceRoot),
            Repositories: null,
            Envelope: envelope);

    private static ToolCall BuildRunCommandCall(string command, params string[] args) =>
        new(
            "c1",
            "run_command",
            new JsonObject
            {
                ["command"] = command,
                ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray())
            });

    private static ToolCall BuildOpenPrCall() =>
        new(
            "c_pr",
            "vcs.open_pr",
            new JsonObject
            {
                ["owner"] = "foo",
                ["name"] = "bar",
                ["head"] = "feat/x",
                ["base"] = "main",
                ["title"] = "Add x"
            });

    private static ToolExecutionContext BuildVcsContext(
        string owner,
        string name,
        WorkflowExecutionEnvelope? envelope)
    {
        var url = $"https://github.com/{owner}/{name}.git";
        var identityKey = RepoReference.Parse(url).IdentityKey;
        return new ToolExecutionContext(
            Repositories: new[]
            {
                new ToolRepositoryContext(owner, name, url, identityKey, $"{owner}/{name}")
            },
            Envelope: envelope);
    }

    private sealed class StubVcsProvider : IVcsProvider
    {
        public GitHostMode Mode => GitHostMode.GitHub;
        public PullRequestInfo OpenPrResult { get; set; } = new("https://example.com/pulls/0", 0);
        public VcsRepoMetadata RepoMetadata { get; set; } =
            new("main", "https://example.com/foo/bar.git", VcsRepoVisibility.Public);
        public (string Owner, string Name, string Head, string Base, string Title, string Body)? LastOpenPrCall { get; private set; }

        public Task<VcsRepoMetadata> GetRepoMetadataAsync(string owner, string name, CancellationToken cancellationToken = default)
            => Task.FromResult(RepoMetadata);

        public Task<PullRequestInfo> OpenPullRequestAsync(
            string owner, string name, string head, string baseRef, string title, string body,
            CancellationToken cancellationToken = default)
        {
            LastOpenPrCall = (owner, name, head, baseRef, title, body);
            return Task.FromResult(OpenPrResult);
        }
    }

    private sealed class SingleProviderFactory : IVcsProviderFactory
    {
        private readonly IVcsProvider provider;
        public SingleProviderFactory(IVcsProvider provider) => this.provider = provider;
        public Task<IVcsProvider> CreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(provider);
    }

    private sealed class RecordingMcpClient : IMcpClient
    {
        public List<(string Server, string ToolName, JsonNode? Arguments)> Calls { get; } = new();

        public Task<McpToolResult> InvokeAsync(
            string server,
            string toolName,
            JsonNode? arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((server, toolName, arguments?.DeepClone()));
            return Task.FromResult(new McpToolResult("{\"ok\":true}"));
        }
    }

    /// <summary>
    /// Captures the tool catalog the model client was invoked with on its first call, then
    /// terminates the invocation loop with a <c>fail</c> tool call so the test doesn't have
    /// to script a multi-round transcript.
    /// </summary>
    private sealed class ToolCatalogCapturingClient : IModelClient
    {
        private readonly List<string> capturedNames;

        public ToolCatalogCapturingClient(List<string> capturedNames)
        {
            this.capturedNames = capturedNames;
        }

        public Task<InvocationResponse> InvokeAsync(
            InvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Tools is { Count: > 0 } tools)
            {
                capturedNames.AddRange(tools.Select(t => t.Name));
            }
            return Task.FromResult(new InvocationResponse(
                new ChatMessage(
                    ChatMessageRole.Assistant,
                    "fail",
                    ToolCalls: new[]
                    {
                        new ToolCall("call_fail", "fail", new JsonObject
                        {
                            ["reason"] = "test-shortcut"
                        })
                    }),
                InvocationStopReason.ToolCalls));
        }
    }
}
