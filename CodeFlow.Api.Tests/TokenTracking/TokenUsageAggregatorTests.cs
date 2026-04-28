using System.Text.Json;
using CodeFlow.Api.TokenTracking;
using CodeFlow.Persistence;
using FluentAssertions;

namespace CodeFlow.Api.Tests.TokenTracking;

/// <summary>
/// Slice 5 of the Token Usage Tracking epic. The aggregator is a pure in-memory transform from
/// the persisted record stream — these tests pin its rollup math, JSON-flattening behavior, and
/// scope nesting semantics without spinning up the host.
/// </summary>
public sealed class TokenUsageAggregatorTests
{
    [Fact]
    public void Aggregate_EmptyRecordList_ReturnsEmptyRollupsAndNoBreakdowns()
    {
        var traceId = Guid.NewGuid();

        var result = TokenUsageAggregator.Aggregate(traceId, Array.Empty<TokenUsageRecord>());

        result.TraceId.Should().Be(traceId);
        result.Records.Should().BeEmpty();
        result.ByInvocation.Should().BeEmpty();
        result.ByNode.Should().BeEmpty();
        result.ByScope.Should().BeEmpty();
        result.Total.CallCount.Should().Be(0);
        result.Total.Totals.Should().BeEmpty();
        result.Total.ByProviderModel.Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_FlattensNestedNumericLeavesByDottedJsonPath()
    {
        // The aggregator must walk arbitrary nested objects and sum numeric leaves keyed by
        // dotted JSON path. This keeps the rollup schema-less — providers can add new fields and
        // the totals dictionary picks them up without an aggregator change.
        var traceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var record = MakeRecord(
            traceId: traceId,
            nodeId: nodeId,
            scopeChain: Array.Empty<Guid>(),
            provider: "openai",
            model: "gpt-5",
            usageJson: """
            {
              "input_tokens": 100,
              "output_tokens": 50,
              "total_tokens": 150,
              "input_tokens_details": { "cached_tokens": 25 },
              "output_tokens_details": { "reasoning_tokens": 12 }
            }
            """);

        var result = TokenUsageAggregator.Aggregate(traceId, new[] { record });

        result.Total.CallCount.Should().Be(1);
        result.Total.Totals.Should().ContainKey("input_tokens").WhoseValue.Should().Be(100);
        result.Total.Totals.Should().ContainKey("output_tokens").WhoseValue.Should().Be(50);
        result.Total.Totals.Should().ContainKey("total_tokens").WhoseValue.Should().Be(150);
        result.Total.Totals.Should().ContainKey("input_tokens_details.cached_tokens").WhoseValue.Should().Be(25);
        result.Total.Totals.Should().ContainKey("output_tokens_details.reasoning_tokens").WhoseValue.Should().Be(12);
    }

    [Fact]
    public void Aggregate_MultiProviderModelTrace_BuildsByProviderModelBreakdown()
    {
        // A trace can call multiple providers / models. The trace-level total sums everything,
        // and ByProviderModel partitions the same data by (provider, model) so the inspector can
        // surface a breakdown when there's more than one combo.
        var traceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var openAi = MakeRecord(traceId, nodeId, Array.Empty<Guid>(), "openai", "gpt-5",
            """{"input_tokens":100,"output_tokens":50}""");
        var anthropic = MakeRecord(traceId, nodeId, Array.Empty<Guid>(), "anthropic", "claude-sonnet-4",
            """{"input_tokens":40,"output_tokens":20,"cache_read_input_tokens":10}""");

        var result = TokenUsageAggregator.Aggregate(traceId, new[] { openAi, anthropic });

        result.Total.Totals["input_tokens"].Should().Be(140);
        result.Total.Totals["output_tokens"].Should().Be(70);
        result.Total.Totals["cache_read_input_tokens"].Should().Be(10);

        result.Total.ByProviderModel.Should().HaveCount(2);
        var openAiBreakdown = result.Total.ByProviderModel.Single(b => b.Provider == "openai");
        openAiBreakdown.Model.Should().Be("gpt-5");
        openAiBreakdown.Totals["input_tokens"].Should().Be(100);
        openAiBreakdown.Totals.Should().NotContainKey("cache_read_input_tokens");

        var anthropicBreakdown = result.Total.ByProviderModel.Single(b => b.Provider == "anthropic");
        anthropicBreakdown.Totals["cache_read_input_tokens"].Should().Be(10);
    }

    [Fact]
    public void Aggregate_SingleProviderModel_StillProducesByProviderModelBreakdown()
    {
        // Always populate ByProviderModel — even when there's only one combo — so the UI
        // doesn't have to special-case the single-combo path.
        var traceId = Guid.NewGuid();
        var record = MakeRecord(traceId, Guid.NewGuid(), Array.Empty<Guid>(), "openai", "gpt-5",
            """{"input_tokens":100,"output_tokens":50}""");

        var result = TokenUsageAggregator.Aggregate(traceId, new[] { record });

        result.Total.ByProviderModel.Should().ContainSingle();
        result.Total.ByProviderModel[0].Provider.Should().Be("openai");
        result.Total.ByProviderModel[0].Totals["input_tokens"].Should().Be(100);
    }

    [Fact]
    public void Aggregate_GroupsByInvocationAndNode()
    {
        var traceId = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var invocationA1 = Guid.NewGuid();
        var invocationA2 = Guid.NewGuid();
        var invocationB1 = Guid.NewGuid();

        var records = new[]
        {
            MakeRecord(traceId, nodeA, Array.Empty<Guid>(), "openai", "gpt-5",
                """{"input_tokens":10,"output_tokens":5}""",
                invocationId: invocationA1),
            MakeRecord(traceId, nodeA, Array.Empty<Guid>(), "openai", "gpt-5",
                """{"input_tokens":20,"output_tokens":15}""",
                invocationId: invocationA2),
            MakeRecord(traceId, nodeB, Array.Empty<Guid>(), "openai", "gpt-5",
                """{"input_tokens":7,"output_tokens":3}""",
                invocationId: invocationB1),
        };

        var result = TokenUsageAggregator.Aggregate(traceId, records);

        result.ByInvocation.Should().HaveCount(3);
        var firstA = result.ByInvocation.Single(r => r.NodeId == nodeA && r.InvocationId == invocationA1);
        firstA.Rollup.CallCount.Should().Be(1);
        firstA.Rollup.Totals["input_tokens"].Should().Be(10);

        result.ByNode.Should().HaveCount(2);
        var nodeARollup = result.ByNode.Single(r => r.NodeId == nodeA);
        nodeARollup.Rollup.CallCount.Should().Be(2);
        nodeARollup.Rollup.Totals["input_tokens"].Should().Be(30);
        nodeARollup.Rollup.Totals["output_tokens"].Should().Be(20);

        var nodeBRollup = result.ByNode.Single(r => r.NodeId == nodeB);
        nodeBRollup.Rollup.CallCount.Should().Be(1);
    }

    [Fact]
    public void Aggregate_NestedScopes_RollupIncludesAllDescendants()
    {
        // A subflow with a nested ReviewLoop should accumulate every descendant's tokens. The
        // record's ScopeChain stores [parent, child, grandchild...] excluding root; for any scope
        // id appearing in any record's chain, the rollup sums every record whose chain contains
        // that id (inclusive of descendants).
        var traceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var subflowId = Guid.NewGuid();
        var reviewLoopId = Guid.NewGuid();

        var topLevelCall = MakeRecord(traceId, nodeId, Array.Empty<Guid>(), "openai", "gpt-5",
            """{"input_tokens":10,"output_tokens":5}""");
        var subflowOnly = MakeRecord(traceId, nodeId, new[] { subflowId }, "openai", "gpt-5",
            """{"input_tokens":20,"output_tokens":10}""");
        var nestedInBoth = MakeRecord(traceId, nodeId, new[] { subflowId, reviewLoopId }, "openai", "gpt-5",
            """{"input_tokens":40,"output_tokens":15}""");

        var result = TokenUsageAggregator.Aggregate(traceId, new[] { topLevelCall, subflowOnly, nestedInBoth });

        result.ByScope.Should().HaveCount(2);

        var subflowRollup = result.ByScope.Single(r => r.ScopeId == subflowId);
        subflowRollup.Rollup.CallCount.Should().Be(2);
        subflowRollup.Rollup.Totals["input_tokens"].Should().Be(60);
        subflowRollup.Rollup.Totals["output_tokens"].Should().Be(25);

        var reviewLoopRollup = result.ByScope.Single(r => r.ScopeId == reviewLoopId);
        reviewLoopRollup.Rollup.CallCount.Should().Be(1);
        reviewLoopRollup.Rollup.Totals["input_tokens"].Should().Be(40);

        // Trace total covers all three records — no scope id in ByScope fans out to the root.
        result.Total.CallCount.Should().Be(3);
        result.Total.Totals["input_tokens"].Should().Be(70);
    }

    [Fact]
    public void Aggregate_PerCallRecordDtoCarriesVerbatimUsageAndPerCallTotals()
    {
        var traceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var record = MakeRecord(traceId, nodeId, Array.Empty<Guid>(), "openai", "gpt-5",
            """{"input_tokens":100,"output_tokens":50,"output_tokens_details":{"reasoning_tokens":12}}""");

        var result = TokenUsageAggregator.Aggregate(traceId, new[] { record });

        result.Records.Should().ContainSingle();
        var dto = result.Records[0];
        dto.RecordId.Should().Be(record.Id);
        dto.NodeId.Should().Be(nodeId);
        dto.InvocationId.Should().Be(record.InvocationId);
        dto.Provider.Should().Be("openai");
        dto.Model.Should().Be("gpt-5");

        // Verbatim usage element preserved.
        dto.Usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32().Should().Be(12);

        // Per-call totals are this single record's flattened sums.
        dto.Totals["input_tokens"].Should().Be(100);
        dto.Totals["output_tokens_details.reasoning_tokens"].Should().Be(12);
    }

    [Fact]
    public void Aggregate_NonNumericFieldsSilentlySkippedFromTotalsButPreservedOnRecordDto()
    {
        // Strings, booleans, arrays, and nulls are not summable. They must be skipped from the
        // totals dictionary cleanly (not crash), while remaining intact in the per-call DTO's
        // verbatim Usage element.
        var traceId = Guid.NewGuid();
        var record = MakeRecord(traceId, Guid.NewGuid(), Array.Empty<Guid>(), "lmstudio", "qwen2.5-7b",
            """{"input_tokens":42,"output_tokens":9,"lmstudio_runtime":"llama.cpp","cache_breakdown":[1,2,3]}""");

        var result = TokenUsageAggregator.Aggregate(traceId, new[] { record });

        result.Total.Totals.Should().ContainKey("input_tokens");
        result.Total.Totals.Should().NotContainKey("lmstudio_runtime");
        result.Total.Totals.Should().NotContainKey("cache_breakdown");

        // Non-numeric fields still round-trip on the per-call DTO so the inspector can surface
        // them as metadata if it wants to.
        result.Records[0].Usage.GetProperty("lmstudio_runtime").GetString().Should().Be("llama.cpp");
        result.Records[0].Usage.GetProperty("cache_breakdown").GetArrayLength().Should().Be(3);
    }

    private static TokenUsageRecord MakeRecord(
        Guid traceId,
        Guid nodeId,
        IReadOnlyList<Guid> scopeChain,
        string provider,
        string model,
        string usageJson,
        Guid? invocationId = null)
    {
        var doc = JsonDocument.Parse(usageJson);
        return new TokenUsageRecord(
            Id: Guid.NewGuid(),
            TraceId: traceId,
            NodeId: nodeId,
            InvocationId: invocationId ?? Guid.NewGuid(),
            ScopeChain: scopeChain,
            Provider: provider,
            Model: model,
            RecordedAtUtc: DateTime.UtcNow,
            Usage: doc.RootElement.Clone());
    }
}
