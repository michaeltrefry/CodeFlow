using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.Validation.Pipeline.Rules;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Tests for F1's <see cref="WorkflowValidationPipeline"/>: rule ordering, exception isolation,
/// finding aggregation, and the canary <see cref="StartNodeAdvisoryRule"/>.
/// </summary>
public sealed class WorkflowValidationPipelineTests
{
    [Fact]
    public async Task RunAsync_AggregatesFindings_FromAllRules()
    {
        var rules = new IWorkflowValidationRule[]
        {
            new StubRule("rule-a", new[]
            {
                Finding("rule-a", WorkflowValidationSeverity.Warning, "warn from a")
            }),
            new StubRule("rule-b", new[]
            {
                Finding("rule-b", WorkflowValidationSeverity.Error, "blocker from b")
            }),
        };
        var pipeline = new WorkflowValidationPipeline(rules, NullLogger<WorkflowValidationPipeline>.Instance);
        var context = await BuildContextAsync();

        var report = await pipeline.RunAsync(context, CancellationToken.None);

        report.Findings.Should().HaveCount(2);
        report.HasErrors.Should().BeTrue();
        report.HasWarnings.Should().BeTrue();
        // Errors should come first so the editor surfaces blockers at the top of the panel.
        report.Findings[0].Severity.Should().Be(WorkflowValidationSeverity.Error);
        report.Findings[0].RuleId.Should().Be("rule-b");
        report.Findings[1].Severity.Should().Be(WorkflowValidationSeverity.Warning);
    }

    [Fact]
    public async Task RunAsync_RespectsRuleOrder()
    {
        var executionOrder = new List<string>();
        var rules = new IWorkflowValidationRule[]
        {
            new StubRule("late", Array.Empty<WorkflowValidationFinding>(), Order: 200, OnRun: () => executionOrder.Add("late")),
            new StubRule("early", Array.Empty<WorkflowValidationFinding>(), Order: 1, OnRun: () => executionOrder.Add("early")),
            new StubRule("middle", Array.Empty<WorkflowValidationFinding>(), Order: 100, OnRun: () => executionOrder.Add("middle")),
        };
        var pipeline = new WorkflowValidationPipeline(rules, NullLogger<WorkflowValidationPipeline>.Instance);

        await pipeline.RunAsync(await BuildContextAsync(), CancellationToken.None);

        executionOrder.Should().Equal("early", "middle", "late");
    }

    [Fact]
    public async Task RunAsync_RuleException_DoesNotStopPipeline()
    {
        var rules = new IWorkflowValidationRule[]
        {
            new StubRule("crashes", _ => throw new InvalidOperationException("boom")),
            new StubRule("survives", new[]
            {
                Finding("survives", WorkflowValidationSeverity.Warning, "I still ran")
            }),
        };
        var pipeline = new WorkflowValidationPipeline(rules, NullLogger<WorkflowValidationPipeline>.Instance);

        var report = await pipeline.RunAsync(await BuildContextAsync(), CancellationToken.None);

        report.Findings.Should().HaveCount(2);
        report.Findings.Should().Contain(f => f.RuleId == "pipeline-error" && f.Message.Contains("crashes"));
        report.Findings.Should().Contain(f => f.RuleId == "survives");
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var rules = new IWorkflowValidationRule[] { new StubRule("never-runs", Array.Empty<WorkflowValidationFinding>()) };
        var pipeline = new WorkflowValidationPipeline(rules, NullLogger<WorkflowValidationPipeline>.Instance);

        var act = async () => await pipeline.RunAsync(await BuildContextAsync(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StartNodeAdvisoryRule_FlagsStartWithoutInputScript()
    {
        var rule = new StartNodeAdvisoryRule();
        var startId = Guid.NewGuid();
        var nodes = new[]
        {
            new WorkflowNodeDto(
                Id: startId,
                Kind: WorkflowNodeKind.Start,
                AgentKey: "kickoff",
                AgentVersion: 1,
                OutputScript: null,
                OutputPorts: new[] { "Completed" },
                LayoutX: 0, LayoutY: 0,
                InputScript: null),
        };
        var context = await BuildContextAsync(nodes);

        var findings = await rule.RunAsync(context, CancellationToken.None);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Warning);
        findings[0].Location!.NodeId.Should().Be(startId);
    }

    [Fact]
    public async Task RunAsync_CompletesWithinPerfBudget_OnFiftyNodeWorkflow()
    {
        // CR4: pipeline should complete in <500 ms for typical workflows up to ~50 nodes. The
        // canary rule is O(n); we just need to confirm registration + dispatch overhead leaves
        // plenty of headroom.
        var rules = new IWorkflowValidationRule[] { new StartNodeAdvisoryRule() };
        var pipeline = new WorkflowValidationPipeline(rules, NullLogger<WorkflowValidationPipeline>.Instance);
        var nodes = Enumerable.Range(0, 50)
            .Select(i => new WorkflowNodeDto(
                Id: Guid.NewGuid(),
                Kind: i == 0 ? WorkflowNodeKind.Start : WorkflowNodeKind.Agent,
                AgentKey: "kickoff",
                AgentVersion: 1,
                OutputScript: null,
                OutputPorts: new[] { "Completed" },
                LayoutX: 0, LayoutY: 0))
            .ToArray();
        var context = await BuildContextAsync(nodes);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var report = await pipeline.RunAsync(context, CancellationToken.None);
        stopwatch.Stop();

        report.Should().NotBeNull();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(500,
            "validators must run in <500 ms on 50-node workflows (CR4)");
    }

    [Fact]
    public async Task StartNodeAdvisoryRule_PassesWhenStartHasInputScript()
    {
        var rule = new StartNodeAdvisoryRule();
        var nodes = new[]
        {
            new WorkflowNodeDto(
                Id: Guid.NewGuid(),
                Kind: WorkflowNodeKind.Start,
                AgentKey: "kickoff",
                AgentVersion: 1,
                OutputScript: null,
                OutputPorts: new[] { "Completed" },
                LayoutX: 0, LayoutY: 0,
                InputScript: "setInput(input.text);"),
        };
        var context = await BuildContextAsync(nodes);

        var findings = await rule.RunAsync(context, CancellationToken.None);

        findings.Should().BeEmpty();
    }

    private static WorkflowValidationFinding Finding(
        string ruleId,
        WorkflowValidationSeverity severity,
        string message) =>
        new(ruleId, severity, message);

    private static async Task<WorkflowValidationContext> BuildContextAsync(
        IReadOnlyList<WorkflowNodeDto>? nodes = null,
        IReadOnlyList<WorkflowEdgeDto>? edges = null)
    {
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
            .UseInMemoryDatabase($"pipeline-tests-{Guid.NewGuid():N}")
            .Options;
        var db = new CodeFlowDbContext(options);
        var workflowRepo = new WorkflowRepository(db);
        var agentRepo = new AgentConfigRepository(db);

        await Task.CompletedTask;
        return new WorkflowValidationContext(
            Key: "test-flow",
            Name: "Test flow",
            MaxRoundsPerRound: 3,
            Nodes: nodes ?? Array.Empty<WorkflowNodeDto>(),
            Edges: edges ?? Array.Empty<WorkflowEdgeDto>(),
            Inputs: null,
            DbContext: db,
            WorkflowRepository: workflowRepo,
            AgentRepository: agentRepo);
    }

    private sealed class StubRule : IWorkflowValidationRule
    {
        private readonly Func<WorkflowValidationContext, Task<IReadOnlyList<WorkflowValidationFinding>>> body;
        private readonly Action? onRun;
        public string RuleId { get; }
        public int Order { get; }

        public StubRule(
            string ruleId,
            IReadOnlyList<WorkflowValidationFinding> staticFindings,
            int Order = 100,
            Action? OnRun = null)
        {
            RuleId = ruleId;
            this.Order = Order;
            body = _ => Task.FromResult(staticFindings);
            onRun = OnRun;
        }

        public StubRule(
            string ruleId,
            Func<WorkflowValidationContext, Task<IReadOnlyList<WorkflowValidationFinding>>> body)
        {
            RuleId = ruleId;
            Order = 100;
            this.body = body;
        }

        public Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
            WorkflowValidationContext context,
            CancellationToken cancellationToken)
        {
            onRun?.Invoke();
            return body(context);
        }
    }
}
