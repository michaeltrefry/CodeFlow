using CodeFlow.Persistence.Authority;
using CodeFlow.Runtime.Authority;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests.Authority;

/// <summary>
/// Round-trip tests for the sc-285 refusal stream. Real MariaDB via Testcontainers so the
/// migration, indexes, and EF mappings are exercised against the same schema production sees.
/// </summary>
public sealed class RefusalEventPersistenceTests : IAsyncLifetime
{
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

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task RefusalEventRepository_round_trips_and_orders_by_occurred_at()
    {
        var traceId = Guid.NewGuid();
        var otherTraceId = Guid.NewGuid();

        await using (var seedContext = CreateDbContext())
        {
            seedContext.RefusalEvents.AddRange(
                Row(traceId, code: "first", occurredAt: DateTime.UtcNow.AddMinutes(-10)),
                Row(traceId, code: "second", occurredAt: DateTime.UtcNow.AddMinutes(-5)),
                Row(otherTraceId, code: "noise", occurredAt: DateTime.UtcNow.AddMinutes(-1)));
            await seedContext.SaveChangesAsync();
        }

        await using var context = CreateDbContext();
        var repo = new RefusalEventRepository(context);

        var refusals = await repo.ListByTraceAsync(traceId);

        refusals.Should().HaveCount(2);
        refusals.Select(r => r.Code).Should().BeEquivalentTo(new[] { "first", "second" }, opts => opts.WithStrictOrdering());
        refusals.Should().OnlyContain(r => r.TraceId == traceId);
    }

    [Fact]
    public async Task RefusalEventRepository_ListByAssistantConversation_filters_correctly()
    {
        var conversationId = Guid.NewGuid();
        await using (var seedContext = CreateDbContext())
        {
            seedContext.RefusalEvents.AddRange(
                Row(traceId: null, assistantConversationId: conversationId, code: "ambiguity"),
                Row(traceId: null, assistantConversationId: Guid.NewGuid(), code: "noise"));
            await seedContext.SaveChangesAsync();
        }

        await using var context = CreateDbContext();
        var repo = new RefusalEventRepository(context);

        var refusals = await repo.ListByAssistantConversationAsync(conversationId);

        refusals.Should().ContainSingle().Which.Code.Should().Be("ambiguity");
    }

    [Fact]
    public async Task EfRefusalEventSink_persists_record_through_a_fresh_scope()
    {
        var services = new ServiceCollection();
        services.AddDbContext<CodeFlowDbContext>(options =>
            CodeFlowDbContextOptions.Configure(options, connectionString!));
        await using var provider = services.BuildServiceProvider();

        var sink = new EfRefusalEventSink(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfRefusalEventSink>.Instance);

        var traceId = Guid.NewGuid();
        var refusal = new RefusalEvent(
            Id: Guid.NewGuid(),
            TraceId: traceId,
            AssistantConversationId: null,
            Stage: RefusalStages.Tool,
            Code: "preimage-mismatch",
            Reason: "stale preimage",
            Axis: "workspace-mutation",
            Path: "src/main.txt",
            DetailJson: "{\"expected\":\"abc\",\"actual\":\"def\"}",
            OccurredAt: DateTimeOffset.UtcNow);

        await sink.RecordAsync(refusal);

        await using var verify = CreateDbContext();
        var repo = new RefusalEventRepository(verify);
        var stored = await repo.ListByTraceAsync(traceId);

        stored.Should().ContainSingle();
        stored.Single().Code.Should().Be("preimage-mismatch");
        stored.Single().DetailJson.Should().Contain("expected");
    }

    [Fact]
    public async Task EfRefusalEventSink_does_not_throw_when_db_is_unreachable()
    {
        var services = new ServiceCollection();
        services.AddDbContext<CodeFlowDbContext>(options =>
            CodeFlowDbContextOptions.Configure(options, "Server=127.0.0.1;Port=1;Database=nope;User=u;Password=p;"));
        await using var provider = services.BuildServiceProvider();

        var sink = new EfRefusalEventSink(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfRefusalEventSink>.Instance);

        // Sink contract: refusal recording must NEVER break the calling tool's primary flow.
        // The structured payload is already in the ToolResult that reaches the LLM, so a sink
        // failure on a dead DB is logged and swallowed.
        var act = async () => await sink.RecordAsync(new RefusalEvent(
            Id: Guid.NewGuid(),
            TraceId: Guid.NewGuid(),
            AssistantConversationId: null,
            Stage: RefusalStages.Tool,
            Code: "any",
            Reason: "any",
            Axis: null,
            Path: null,
            DetailJson: null,
            OccurredAt: DateTimeOffset.UtcNow));

        await act.Should().NotThrowAsync();
    }

    private static RefusalEventEntity Row(
        Guid? traceId = null,
        Guid? assistantConversationId = null,
        string code = "code",
        DateTime? occurredAt = null)
    {
        return new RefusalEventEntity
        {
            Id = Guid.NewGuid(),
            TraceId = traceId,
            AssistantConversationId = assistantConversationId,
            Stage = RefusalStages.Tool,
            Code = code,
            Reason = "test",
            Axis = "test-axis",
            Path = null,
            DetailJson = null,
            OccurredAtUtc = occurredAt ?? DateTime.UtcNow
        };
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
