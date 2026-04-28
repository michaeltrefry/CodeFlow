using System.Text.Json;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class TokenUsageRecordPersistenceTests : IAsyncLifetime
{
    private readonly MariaDbContainer container = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_token_usage")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await container.DisposeAsync();
    }

    [Fact]
    public async Task AddAsync_RoundTrip_PreservesAllFieldsAndArbitraryUsageJson()
    {
        var options = await BuildOptionsAndMigrateAsync();

        var traceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var scopeChain = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var recordedAt = DateTime.UtcNow;

        // Includes a field the schema doesn't know about (`some_future_field`) plus a nested
        // object — round-trip must preserve every key verbatim, otherwise slices 2-4 lose
        // provider-reported fields the moment a provider adds a new one.
        var usageJson = """
        {
            "input_tokens": 100,
            "output_tokens": 50,
            "cache_creation_input_tokens": 200,
            "cache_read_input_tokens": 25,
            "reasoning_tokens": 10,
            "some_future_field": "future_value",
            "nested": { "a": 1, "b": [2, 3, 4] }
        }
        """;

        var recordId = Guid.NewGuid();

        await using (var scope = new CodeFlowDbContext(options))
        {
            using var usageDocument = JsonDocument.Parse(usageJson);
            var repo = new TokenUsageRecordRepository(scope);
            await repo.AddAsync(new TokenUsageRecord(
                Id: recordId,
                TraceId: traceId,
                NodeId: nodeId,
                InvocationId: invocationId,
                ScopeChain: scopeChain,
                Provider: "anthropic",
                Model: "claude-opus-4-7",
                RecordedAtUtc: recordedAt,
                Usage: usageDocument.RootElement.Clone()));
        }

        await using (var scope = new CodeFlowDbContext(options))
        {
            var repo = new TokenUsageRecordRepository(scope);
            var loaded = await repo.ListByTraceAsync(traceId);

            loaded.Should().ContainSingle();
            var record = loaded[0];

            record.Id.Should().Be(recordId);
            record.TraceId.Should().Be(traceId);
            record.NodeId.Should().Be(nodeId);
            record.InvocationId.Should().Be(invocationId);
            record.ScopeChain.Should().Equal(scopeChain);
            record.Provider.Should().Be("anthropic");
            record.Model.Should().Be("claude-opus-4-7");
            record.RecordedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
            record.RecordedAtUtc.Should().BeCloseTo(recordedAt, TimeSpan.FromMilliseconds(1));

            record.Usage.GetProperty("input_tokens").GetInt32().Should().Be(100);
            record.Usage.GetProperty("output_tokens").GetInt32().Should().Be(50);
            record.Usage.GetProperty("cache_creation_input_tokens").GetInt32().Should().Be(200);
            record.Usage.GetProperty("cache_read_input_tokens").GetInt32().Should().Be(25);
            record.Usage.GetProperty("reasoning_tokens").GetInt32().Should().Be(10);
            record.Usage.GetProperty("some_future_field").GetString().Should().Be("future_value");

            var nested = record.Usage.GetProperty("nested");
            nested.GetProperty("a").GetInt32().Should().Be(1);
            nested.GetProperty("b").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(2, 3, 4);
        }
    }

    [Fact]
    public async Task ListByTraceAsync_ReturnsRecordsOrderedByTimestampThenId_AndFiltersByTrace()
    {
        var options = await BuildOptionsAndMigrateAsync();

        var traceA = Guid.NewGuid();
        var traceB = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var baseTime = DateTime.UtcNow;

        await using (var scope = new CodeFlowDbContext(options))
        {
            var repo = new TokenUsageRecordRepository(scope);

            using var usage = JsonDocument.Parse("{\"input_tokens\":1}");
            await repo.AddAsync(NewRecord(traceA, nodeId, baseTime.AddSeconds(2), usage.RootElement));
            await repo.AddAsync(NewRecord(traceA, nodeId, baseTime.AddSeconds(1), usage.RootElement));
            await repo.AddAsync(NewRecord(traceB, nodeId, baseTime, usage.RootElement));
        }

        await using (var scope = new CodeFlowDbContext(options))
        {
            var repo = new TokenUsageRecordRepository(scope);
            var aRecords = await repo.ListByTraceAsync(traceA);

            aRecords.Should().HaveCount(2);
            aRecords[0].RecordedAtUtc.Should().BeCloseTo(baseTime.AddSeconds(1), TimeSpan.FromMilliseconds(1));
            aRecords[1].RecordedAtUtc.Should().BeCloseTo(baseTime.AddSeconds(2), TimeSpan.FromMilliseconds(1));

            (await repo.ListByTraceAsync(traceB)).Should().HaveCount(1);
            (await repo.ListByTraceAsync(Guid.NewGuid())).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task AddAsync_EmptyScopeChain_RoundTripsAsEmpty()
    {
        var options = await BuildOptionsAndMigrateAsync();
        var traceId = Guid.NewGuid();

        await using (var scope = new CodeFlowDbContext(options))
        {
            using var usage = JsonDocument.Parse("{}");
            var repo = new TokenUsageRecordRepository(scope);
            await repo.AddAsync(new TokenUsageRecord(
                Id: Guid.NewGuid(),
                TraceId: traceId,
                NodeId: Guid.NewGuid(),
                InvocationId: Guid.NewGuid(),
                ScopeChain: Array.Empty<Guid>(),
                Provider: "openai",
                Model: "gpt-4o",
                RecordedAtUtc: DateTime.UtcNow,
                Usage: usage.RootElement.Clone()));
        }

        await using (var scope = new CodeFlowDbContext(options))
        {
            var repo = new TokenUsageRecordRepository(scope);
            var loaded = await repo.ListByTraceAsync(traceId);
            loaded.Should().ContainSingle();
            loaded[0].ScopeChain.Should().BeEmpty();
        }
    }

    private async Task<DbContextOptions<CodeFlowDbContext>> BuildOptionsAndMigrateAsync()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, container.GetConnectionString());
        await using var migrationContext = new CodeFlowDbContext(builder.Options);
        await migrationContext.Database.MigrateAsync();
        return builder.Options;
    }

    private static TokenUsageRecord NewRecord(Guid traceId, Guid nodeId, DateTime recordedAt, JsonElement usage)
    {
        return new TokenUsageRecord(
            Id: Guid.NewGuid(),
            TraceId: traceId,
            NodeId: nodeId,
            InvocationId: Guid.NewGuid(),
            ScopeChain: Array.Empty<Guid>(),
            Provider: "openai",
            Model: "gpt-4o",
            RecordedAtUtc: recordedAt,
            Usage: usage.Clone());
    }
}
