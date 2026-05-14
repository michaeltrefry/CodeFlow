using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation;
using CodeFlow.Contracts;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Epic 993 / NO-3: save-time validation for a node's optional <see cref="AgentInvocationOverrides"/>
/// overlay. Each test seeds a Start → Agent → Sink topology, swaps the overlay on the Agent node
/// (or the node kind), and asserts the pass/fail outcome through <c>WorkflowValidator.ValidateAsync</c>.
/// </summary>
public sealed class AgentOverridesValidatorTests
{
    // A real host tool name pulled from the runtime catalog so the test stays correct if the
    // catalog changes — the validator checks identifiers against this same catalog.
    private static readonly string KnownHostTool = HostToolProvider.GetCatalog().First().Name;

    [Fact]
    public async Task NullOverrides_Validates()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with { AgentOverrides = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task FullValidOverlay_OnAgentNode_Validates()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(
                ModelProvider: "anthropic",
                Model: "claude-opus-4-7",
                MaxOutputTokens: 8192,
                MaxToolCalls: 32,
                MaxLoopDurationSeconds: 600,
                MaxConsecutiveNonMutatingCalls: 12,
                AdditionalToolIdentifiers: new[] { KnownHostTool, "mcp:shortcut:stories-create" }),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task Overlay_OnGoalNode_Validates()
    {
        // Goal is an agent-bearing kind, so it is override-eligible.
        var fx = await CreateFixtureAsync();
        var startId = Guid.NewGuid();
        var goalId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();

        var nodes = new[]
        {
            new WorkflowNodeDto(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                new[] { "Completed" }, 0, 0),
            new WorkflowNodeDto(goalId, WorkflowNodeKind.Goal, "worker", 1, null,
                Array.Empty<string>(), 0, 0,
                GoalObjective: "Complete the acceptance criteria",
                GoalTokenBudget: 500_000,
                GoalMaxIterations: 50,
                AgentOverrides: new AgentInvocationOverrides(MaxToolCalls: 40)),
            new WorkflowNodeDto(sinkId, WorkflowNodeKind.Agent, "sink", 1, null,
                new[] { "Completed" }, 0, 0),
        };
        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", goalId, "in", false, 0),
            new WorkflowEdgeDto(goalId, "Success", sinkId, "in", false, 1),
        };

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task Overlay_OnNonAgentBearingKind_Fails()
    {
        // A Logic node cannot dispatch an agent, so an overlay there has nowhere to apply.
        var fx = await CreateFixtureAsync();
        var startId = Guid.NewGuid();
        var logicId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();

        var nodes = new[]
        {
            new WorkflowNodeDto(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                new[] { "Completed" }, 0, 0),
            new WorkflowNodeDto(logicId, WorkflowNodeKind.Logic, null, null,
                "setNodePath('A');", new[] { "A" }, 0, 0,
                AgentOverrides: new AgentInvocationOverrides(MaxToolCalls: 10)),
            new WorkflowNodeDto(sinkId, WorkflowNodeKind.Agent, "sink", 1, null,
                new[] { "Completed" }, 0, 0),
        };
        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", logicId, "in", false, 0),
            new WorkflowEdgeDto(logicId, "A", sinkId, "in", false, 1),
        };

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("agent-bearing");
    }

    [Fact]
    public async Task ProviderWithoutModel_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(ModelProvider: "anthropic"),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("modelProvider and model");
    }

    [Fact]
    public async Task ModelWithoutProvider_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(Model: "claude-opus-4-7"),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("modelProvider and model");
    }

    [Fact]
    public async Task UnknownProvider_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(ModelProvider: "gemini", Model: "gemini-pro"),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("modelProvider 'gemini'");
    }

    [Fact]
    public async Task KnownProviderAndModel_Validates()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(ModelProvider: "openai", Model: "gpt-5.4"),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(WorkflowValidator.MaxOverrideMaxToolCalls + 1)]
    public async Task OutOfRangeMaxToolCalls_Fails(int badValue)
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(MaxToolCalls: badValue),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("maxToolCalls");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WorkflowValidator.MaxOverrideMaxOutputTokens + 1)]
    public async Task OutOfRangeMaxOutputTokens_Fails(int badValue)
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(MaxOutputTokens: badValue),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("maxOutputTokens");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WorkflowValidator.MaxOverrideMaxLoopDurationSeconds + 1)]
    public async Task OutOfRangeMaxLoopDurationSeconds_Fails(int badValue)
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(MaxLoopDurationSeconds: badValue),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("maxLoopDurationSeconds");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WorkflowValidator.MaxOverrideMaxConsecutiveNonMutatingCalls + 1)]
    public async Task OutOfRangeMaxConsecutiveNonMutatingCalls_Fails(int badValue)
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(MaxConsecutiveNonMutatingCalls: badValue),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("maxConsecutiveNonMutatingCalls");
    }

    [Fact]
    public async Task UnknownToolIdentifier_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(
                AdditionalToolIdentifiers: new[] { "not_a_real_tool" }),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("not_a_real_tool");
    }

    [Fact]
    public async Task MalformedMcpIdentifier_Fails()
    {
        // Missing the tool segment — `mcp:server` is not a complete grant.
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(
                AdditionalToolIdentifiers: new[] { "mcp:shortcut" }),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("mcp:shortcut");
    }

    [Fact]
    public async Task DuplicateToolIdentifier_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(
                AdditionalToolIdentifiers: new[] { KnownHostTool, KnownHostTool }),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("more than once");
    }

    [Fact]
    public async Task BlankToolIdentifier_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(
                AdditionalToolIdentifiers: new[] { "  " }),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("blank");
    }

    [Fact]
    public async Task ValidHostAndMcpTools_Validate()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildWorkflow(fx, node => node with
        {
            AgentOverrides = new AgentInvocationOverrides(
                AdditionalToolIdentifiers: new[] { KnownHostTool, "mcp:shortcut:stories-create" }),
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed" });
        await fx.SeedAgentAsync("worker", new[] { "Completed" });
        await fx.SeedAgentAsync("sink", new[] { "Completed" });
        return fx;
    }

    private static (WorkflowNodeDto[] Nodes, WorkflowEdgeDto[] Edges) BuildWorkflow(
        TestFixture _,
        Func<WorkflowNodeDto, WorkflowNodeDto> agentCustomization)
    {
        var startId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();

        var agentNode = new WorkflowNodeDto(
            Id: agentId,
            Kind: WorkflowNodeKind.Agent,
            AgentKey: "worker",
            AgentVersion: 1,
            OutputScript: null,
            OutputPorts: new[] { "Completed" },
            LayoutX: 0,
            LayoutY: 0);

        agentNode = agentCustomization(agentNode);

        var nodes = new[]
        {
            new WorkflowNodeDto(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                new[] { "Completed" }, 0, 0),
            agentNode,
            new WorkflowNodeDto(sinkId, WorkflowNodeKind.Agent, "sink", 1, null,
                new[] { "Completed" }, 0, 0),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", agentId, "in", false, 0),
            new WorkflowEdgeDto(agentId, "Completed", sinkId, "in", false, 1),
        };

        return (nodes, edges);
    }

    private sealed class TestFixture
    {
        public CodeFlowDbContext DbContext { get; }
        public IWorkflowRepository WorkflowRepo { get; }
        public IAgentConfigRepository AgentRepo { get; }

        private TestFixture(CodeFlowDbContext db, IWorkflowRepository wr, IAgentConfigRepository ar)
        {
            DbContext = db;
            WorkflowRepo = wr;
            AgentRepo = ar;
        }

        public static Task<TestFixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
                .UseInMemoryDatabase($"agent-overrides-validator-tests-{Guid.NewGuid():N}")
                .Options;
            var db = new CodeFlowDbContext(options);
            var workflowRepo = new WorkflowRepository(db);
            var agentRepo = new AgentConfigRepository(db);
            return Task.FromResult(new TestFixture(db, workflowRepo, agentRepo));
        }

        public async Task SeedAgentAsync(string key, IReadOnlyList<string> declaredOutputs, int version = 1)
        {
            var outputsJson = string.Join(",", declaredOutputs.Select(o =>
                $$"""{ "kind": "{{o}}" }"""));
            var configJson = $$"""
                {
                    "type": "agent",
                    "provider": "openai",
                    "model": "gpt-test",
                    "outputs": [ {{outputsJson}} ]
                }
                """;
            DbContext.Agents.Add(new AgentConfigEntity
            {
                Key = key,
                Version = version,
                ConfigJson = configJson,
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            await DbContext.SaveChangesAsync();
        }

        public Task<ValidationResult> ValidateAsync(
            string key,
            IReadOnlyList<WorkflowNodeDto> nodes,
            IReadOnlyList<WorkflowEdgeDto> edges,
            IReadOnlyList<WorkflowInputDto>? inputs = null) =>
            WorkflowValidator.ValidateAsync(
                key,
                $"Test workflow {key}",
                maxRoundsPerRound: 3,
                nodes,
                edges,
                inputs,
                DbContext,
                WorkflowRepo,
                AgentRepo,
                CancellationToken.None);
    }
}
