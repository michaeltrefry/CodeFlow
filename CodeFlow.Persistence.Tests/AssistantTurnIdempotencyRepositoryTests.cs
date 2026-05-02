using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

/// <summary>
/// sc-525 — Repository tests for the assistant turn idempotency table. Real MariaDB via
/// Testcontainers because the contract — unique-index-backed claim, terminal flush, expired
/// purge — is the schema/EF integration; running against the in-memory provider would skip
/// the unique-violation path entirely.
/// </summary>
public sealed class AssistantTurnIdempotencyRepositoryTests : IAsyncLifetime
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
    public async Task TryClaimAsync_returns_Claimed_for_first_request()
    {
        var conversationId = Guid.NewGuid();
        var key = NewKey();

        await using var ctx = CreateDbContext();
        var repo = new AssistantTurnIdempotencyRepository(ctx);
        var now = DateTime.UtcNow;

        var outcome = await repo.TryClaimAsync(
            conversationId,
            key,
            "user-1",
            "hash-a",
            now,
            TimeSpan.FromMinutes(10));

        outcome.Should().BeOfType<AssistantTurnClaimOutcome.Claimed>();
        var claimed = (AssistantTurnClaimOutcome.Claimed)outcome;
        claimed.Record.Status.Should().Be(AssistantTurnIdempotencyStatus.InFlight);
        claimed.Record.ConversationId.Should().Be(conversationId);
        claimed.Record.IdempotencyKey.Should().Be(key);
        claimed.Record.UserId.Should().Be("user-1");
        claimed.Record.RequestHash.Should().Be("hash-a");
        claimed.Record.ExpiresAtUtc.Should().BeCloseTo(now.AddMinutes(10), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TryClaimAsync_returns_Existing_on_duplicate_key()
    {
        var conversationId = Guid.NewGuid();
        var key = NewKey();
        var now = DateTime.UtcNow;

        await using (var ctx = CreateDbContext())
        {
            var repo = new AssistantTurnIdempotencyRepository(ctx);
            await repo.TryClaimAsync(conversationId, key, "user-1", "hash-a", now, TimeSpan.FromMinutes(10));
        }

        await using (var ctx = CreateDbContext())
        {
            var repo = new AssistantTurnIdempotencyRepository(ctx);
            var second = await repo.TryClaimAsync(
                conversationId,
                key,
                "user-1",
                "hash-a",
                now.AddSeconds(1),
                TimeSpan.FromMinutes(10));

            second.Should().BeOfType<AssistantTurnClaimOutcome.Existing>();
            ((AssistantTurnClaimOutcome.Existing)second).Record.RequestHash.Should().Be("hash-a");
        }
    }

    [Fact]
    public async Task TryClaimAsync_distinct_keys_dont_collide()
    {
        var conversationId = Guid.NewGuid();
        var keyA = NewKey();
        var keyB = NewKey();

        await using var ctx = CreateDbContext();
        var repo = new AssistantTurnIdempotencyRepository(ctx);

        var first = await repo.TryClaimAsync(conversationId, keyA, "u", "h", DateTime.UtcNow, TimeSpan.FromMinutes(10));
        var second = await repo.TryClaimAsync(conversationId, keyB, "u", "h", DateTime.UtcNow, TimeSpan.FromMinutes(10));

        first.Should().BeOfType<AssistantTurnClaimOutcome.Claimed>();
        second.Should().BeOfType<AssistantTurnClaimOutcome.Claimed>();
    }

    [Fact]
    public async Task TryClaimAsync_distinct_conversations_dont_collide()
    {
        var key = NewKey();

        await using var ctx = CreateDbContext();
        var repo = new AssistantTurnIdempotencyRepository(ctx);

        var first = await repo.TryClaimAsync(Guid.NewGuid(), key, "u", "h", DateTime.UtcNow, TimeSpan.FromMinutes(10));
        var second = await repo.TryClaimAsync(Guid.NewGuid(), key, "u", "h", DateTime.UtcNow, TimeSpan.FromMinutes(10));

        first.Should().BeOfType<AssistantTurnClaimOutcome.Claimed>();
        second.Should().BeOfType<AssistantTurnClaimOutcome.Claimed>();
    }

    [Fact]
    public async Task MarkTerminalAsync_persists_events_and_status()
    {
        var conversationId = Guid.NewGuid();
        var key = NewKey();

        Guid recordId;
        await using (var ctx = CreateDbContext())
        {
            var repo = new AssistantTurnIdempotencyRepository(ctx);
            var outcome = await repo.TryClaimAsync(conversationId, key, "u", "h", DateTime.UtcNow, TimeSpan.FromMinutes(10));
            recordId = ((AssistantTurnClaimOutcome.Claimed)outcome).Record.Id;
        }

        await using (var ctx = CreateDbContext())
        {
            var repo = new AssistantTurnIdempotencyRepository(ctx);
            await repo.MarkTerminalAsync(
                recordId,
                AssistantTurnIdempotencyStatus.Completed,
                """[{"event":"text-delta","payload":"hi"}]""",
                DateTime.UtcNow);
        }

        await using (var ctx = CreateDbContext())
        {
            var repo = new AssistantTurnIdempotencyRepository(ctx);
            var record = await repo.GetByIdAsync(recordId);

            record.Should().NotBeNull();
            record!.Status.Should().Be(AssistantTurnIdempotencyStatus.Completed);
            record.EventsJson.Should().Contain("text-delta");
            record.CompletedAtUtc.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task MarkTerminalAsync_rejects_InFlight_terminal()
    {
        var conversationId = Guid.NewGuid();
        await using var ctx = CreateDbContext();
        var repo = new AssistantTurnIdempotencyRepository(ctx);

        var outcome = await repo.TryClaimAsync(conversationId, NewKey(), "u", "h", DateTime.UtcNow, TimeSpan.FromMinutes(10));
        var id = ((AssistantTurnClaimOutcome.Claimed)outcome).Record.Id;

        await FluentActions.Invoking(() => repo.MarkTerminalAsync(
                id,
                AssistantTurnIdempotencyStatus.InFlight,
                "[]",
                DateTime.UtcNow))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PurgeExpiredAsync_removes_only_expired_rows()
    {
        var now = DateTime.UtcNow;

        await using (var ctx = CreateDbContext())
        {
            var repo = new AssistantTurnIdempotencyRepository(ctx);
            // One expired (TTL of -1 minute relative to now).
            await repo.TryClaimAsync(Guid.NewGuid(), NewKey(), "u", "h", now.AddMinutes(-30), TimeSpan.FromMinutes(5));
            // One still valid (TTL of +10 minutes).
            await repo.TryClaimAsync(Guid.NewGuid(), NewKey(), "u", "h", now, TimeSpan.FromMinutes(10));
        }

        await using (var ctx = CreateDbContext())
        {
            var repo = new AssistantTurnIdempotencyRepository(ctx);
            var deleted = await repo.PurgeExpiredAsync(now);

            deleted.Should().Be(1);
            (await ctx.AssistantTurnIdempotency.CountAsync()).Should().Be(1);
        }
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
