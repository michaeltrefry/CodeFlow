using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Integration;

/// <summary>
/// Slice 5 of the Token Usage Tracking epic. Integration tests for
/// <c>GET /api/traces/{id}/token-usage</c>: returns rollups at every level for a single trace,
/// with provider+model breakdowns. Auth is <c>TracesRead</c> (mirrors the rest of the inspector
/// detail endpoints).
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class TraceTokenUsageEndpointTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public TraceTokenUsageEndpointTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetTokenUsage_NestedSubflowsAndMultipleProviders_ReturnsCorrectRollupsAtEveryLevel()
    {
        // A single trace with: two top-level OpenAI calls on node A, one Anthropic call on node B
        // nested inside a subflow, and one OpenAI call on node B nested inside both the subflow
        // and a deeper ReviewLoop. Expected:
        //   - Trace total sums all four; ByProviderModel splits openai vs anthropic.
        //   - ByNode rolls up A and B separately.
        //   - ByInvocation has four entries (one per (NodeId, InvocationId)).
        //   - ByScope has two entries (subflow + reviewLoop), each summing inclusive of descendants.
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await SeedTraceAsync(traceId, correlationId);

        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var subflowId = Guid.NewGuid();
        var reviewLoopId = Guid.NewGuid();
        var invocationA1 = Guid.NewGuid();
        var invocationA2 = Guid.NewGuid();
        var invocationB1 = Guid.NewGuid();
        var invocationB2 = Guid.NewGuid();

        await SeedRecordsAsync(
            (traceId, nodeA, Array.Empty<Guid>(), invocationA1, "openai", "gpt-5",
                """{"input_tokens":100,"output_tokens":50,"output_tokens_details":{"reasoning_tokens":12}}"""),
            (traceId, nodeA, Array.Empty<Guid>(), invocationA2, "openai", "gpt-5",
                """{"input_tokens":40,"output_tokens":20}"""),
            (traceId, nodeB, new[] { subflowId }, invocationB1, "anthropic", "claude-sonnet-4",
                """{"input_tokens":30,"output_tokens":10,"cache_read_input_tokens":5}"""),
            (traceId, nodeB, new[] { subflowId, reviewLoopId }, invocationB2, "openai", "gpt-5",
                """{"input_tokens":15,"output_tokens":7}"""));

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/traces/{traceId}/token-usage");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<TokenUsagePayload>();
        dto.Should().NotBeNull();
        dto!.TraceId.Should().Be(traceId);

        // Per-call raw records: 4.
        dto.Records.Should().HaveCount(4);

        // Trace total: 100+40+30+15 = 185 input, 50+20+10+7 = 87 output.
        dto.Total.CallCount.Should().Be(4);
        dto.Total.Totals["input_tokens"].Should().Be(185);
        dto.Total.Totals["output_tokens"].Should().Be(87);
        dto.Total.Totals["cache_read_input_tokens"].Should().Be(5);
        dto.Total.Totals["output_tokens_details.reasoning_tokens"].Should().Be(12);

        // Two provider+model combos at the trace level.
        dto.Total.ByProviderModel.Should().HaveCount(2);
        var openAiTotal = dto.Total.ByProviderModel.Single(b => b.Provider == "openai");
        openAiTotal.Totals["input_tokens"].Should().Be(155); // 100 + 40 + 15
        var anthropicTotal = dto.Total.ByProviderModel.Single(b => b.Provider == "anthropic");
        anthropicTotal.Totals["input_tokens"].Should().Be(30);
        anthropicTotal.Totals["cache_read_input_tokens"].Should().Be(5);

        // Per-node rollups.
        dto.ByNode.Should().HaveCount(2);
        var nodeARollup = dto.ByNode.Single(r => r.NodeId == nodeA);
        nodeARollup.Rollup.CallCount.Should().Be(2);
        nodeARollup.Rollup.Totals["input_tokens"].Should().Be(140);
        var nodeBRollup = dto.ByNode.Single(r => r.NodeId == nodeB);
        nodeBRollup.Rollup.CallCount.Should().Be(2);
        nodeBRollup.Rollup.Totals["input_tokens"].Should().Be(45);
        // Two providers contributed to node B.
        nodeBRollup.Rollup.ByProviderModel.Should().HaveCount(2);

        // Per-invocation rollups: 4 entries, each with CallCount = 1.
        dto.ByInvocation.Should().HaveCount(4);
        dto.ByInvocation.Should().AllSatisfy(r => r.Rollup.CallCount.Should().Be(1));

        // Per-scope rollups — subflow includes both descendants; reviewLoop only its own call.
        dto.ByScope.Should().HaveCount(2);
        var subflowRollup = dto.ByScope.Single(s => s.ScopeId == subflowId);
        subflowRollup.Rollup.CallCount.Should().Be(2);
        subflowRollup.Rollup.Totals["input_tokens"].Should().Be(45); // 30 + 15
        subflowRollup.Rollup.Totals["output_tokens"].Should().Be(17); // 10 + 7
        subflowRollup.Rollup.ByProviderModel.Should().HaveCount(2);

        var reviewLoopRollup = dto.ByScope.Single(s => s.ScopeId == reviewLoopId);
        reviewLoopRollup.Rollup.CallCount.Should().Be(1);
        reviewLoopRollup.Rollup.Totals["input_tokens"].Should().Be(15);
    }

    [Fact]
    public async Task GetTokenUsage_TraceExistsButNoRecordsYet_Returns200WithEmptyRollups()
    {
        // A live trace that hasn't issued an LLM call yet still needs to return a valid empty
        // shape so the inspector can render an empty-state pane. (Distinguished from "no such
        // trace" via 404 in the next test.)
        var traceId = Guid.NewGuid();
        await SeedTraceAsync(traceId, Guid.NewGuid());

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/traces/{traceId}/token-usage");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<TokenUsagePayload>();
        dto!.TraceId.Should().Be(traceId);
        dto.Records.Should().BeEmpty();
        dto.Total.CallCount.Should().Be(0);
        dto.Total.Totals.Should().BeEmpty();
        dto.Total.ByProviderModel.Should().BeEmpty();
        dto.ByInvocation.Should().BeEmpty();
        dto.ByNode.Should().BeEmpty();
        dto.ByScope.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTokenUsage_WorkflowSagaTrace_LabelsStreamKindAsWorkflow()
    {
        // HAA-14: trace inspector reads streamKind to render the right panel title. Default
        // workflow saga path must report 'workflow'.
        var traceId = Guid.NewGuid();
        await SeedTraceAsync(traceId, Guid.NewGuid());

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/traces/{traceId}/token-usage");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<TokenUsagePayload>();
        dto!.StreamKind.Should().Be("workflow");
    }

    [Fact]
    public async Task GetTokenUsage_AssistantSyntheticTrace_LabelsStreamKindAsAssistant()
    {
        // HAA-14: an assistant conversation's synthetic trace id has no saga row but DOES live
        // in the AssistantConversations table. The endpoint must accept it and label the stream
        // as 'assistant' so the panel renders the Assistant title + chip.
        var (conversationId, syntheticTraceId) = await SeedAssistantConversationAsync();

        // Land a token usage record against the synthetic trace so the rollup is non-empty —
        // verifies the panel hydrates as if it were any other trace, just labeled differently.
        await SeedRecordsAsync(
            (syntheticTraceId, conversationId, Array.Empty<Guid>(), Guid.NewGuid(), "anthropic", "claude-sonnet-4",
                """{"input_tokens":12,"output_tokens":7}"""));

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/traces/{syntheticTraceId}/token-usage");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<TokenUsagePayload>();
        dto!.StreamKind.Should().Be("assistant");
        dto.TraceId.Should().Be(syntheticTraceId);
        dto.Total.CallCount.Should().Be(1);
        dto.Total.Totals["input_tokens"].Should().Be(12);
    }

    [Fact]
    public async Task GetTokenUsage_NoSuchTrace_Returns404()
    {
        // A token-usage row could exist for a trace that no longer has a saga (e.g., raced delete)
        // but we want the inspector contract to be: 404 means "no such trace", not "no records".
        // Without a saga row, the endpoint short-circuits to 404 even if orphan records exist.
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/traces/{Guid.NewGuid()}/token-usage");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task SeedTraceAsync(Guid traceId, Guid correlationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        db.WorkflowSagas.Add(new WorkflowSagaStateEntity
        {
            CorrelationId = correlationId,
            TraceId = traceId,
            CurrentState = "Running",
            CurrentNodeId = Guid.NewGuid(),
            CurrentAgentKey = "trace-agent",
            CurrentRoundId = Guid.NewGuid(),
            RoundCount = 1,
            AgentVersionsJson = """{"trace-agent":1}""",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = 0,
            LogicEvaluationCount = 0,
            WorkflowKey = "token-usage-test-flow",
            WorkflowVersion = 1,
            InputsJson = """{"input":"hello"}""",
            CurrentInputRef = "file:///tmp/input.bin",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Version = 1
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid ConversationId, Guid SyntheticTraceId)> SeedAssistantConversationAsync()
    {
        // Direct DB seed (rather than POST /api/assistant/conversations) so the test stays
        // independent of the resolver's cookie minting and exercises the token endpoint's
        // synthetic-trace lookup path explicitly.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var conversationId = Guid.NewGuid();
        var syntheticTraceId = Guid.NewGuid();
        db.AssistantConversations.Add(new AssistantConversationEntity
        {
            Id = conversationId,
            UserId = "test-user-token-stream",
            ScopeKind = AssistantConversationScopeKind.Homepage,
            ScopeKey = "homepage",
            SyntheticTraceId = syntheticTraceId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (conversationId, syntheticTraceId);
    }

    private async Task SeedRecordsAsync(
        params (Guid TraceId, Guid NodeId, IReadOnlyList<Guid> ScopeChain, Guid InvocationId, string Provider, string Model, string UsageJson)[] records)
    {
        using var scope = factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITokenUsageRecordRepository>();

        var baseTime = DateTime.UtcNow;
        for (var i = 0; i < records.Length; i++)
        {
            var (traceId, nodeId, scopeChain, invocationId, provider, model, usageJson) = records[i];
            using var doc = JsonDocument.Parse(usageJson);
            await repo.AddAsync(new TokenUsageRecord(
                Id: Guid.NewGuid(),
                TraceId: traceId,
                NodeId: nodeId,
                InvocationId: invocationId,
                ScopeChain: scopeChain,
                Provider: provider,
                Model: model,
                // Stagger timestamps so ordering is deterministic across the test run.
                RecordedAtUtc: baseTime.AddSeconds(i),
                Usage: doc.RootElement.Clone()));
        }
    }

    // Local DTO mirrors the API contract — kept in-test so the test fails loudly if the API
    // shape drifts. Includes only the fields these tests inspect.
    private sealed record TokenUsagePayload(
        Guid TraceId,
        string StreamKind,
        TokenUsageRollupPayload Total,
        IReadOnlyList<JsonElement> Records,
        IReadOnlyList<TokenUsageInvocationPayload> ByInvocation,
        IReadOnlyList<TokenUsageNodePayload> ByNode,
        IReadOnlyList<TokenUsageScopePayload> ByScope);

    private sealed record TokenUsageRollupPayload(
        int CallCount,
        Dictionary<string, long> Totals,
        IReadOnlyList<TokenUsageProviderModelPayload> ByProviderModel);

    private sealed record TokenUsageProviderModelPayload(
        string Provider,
        string Model,
        Dictionary<string, long> Totals);

    private sealed record TokenUsageInvocationPayload(
        Guid NodeId,
        Guid InvocationId,
        TokenUsageRollupPayload Rollup);

    private sealed record TokenUsageNodePayload(
        Guid NodeId,
        TokenUsageRollupPayload Rollup);

    private sealed record TokenUsageScopePayload(
        Guid ScopeId,
        TokenUsageRollupPayload Rollup);
}
