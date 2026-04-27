using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class PromptPartialPersistenceTests : IAsyncLifetime
{
    private readonly MariaDbContainer container = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_partial_persistence")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task PromptPartial_RoundTripsThroughRepository_AndIsImmutablePerVersion()
    {
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(options, container.GetConnectionString());

        await using (var migrationContext = new CodeFlowDbContext(options.Options))
        {
            await migrationContext.Database.MigrateAsync();
        }

        const string key = "@codeflow/reviewer-base";

        // First version: append v1 with a baseline body.
        await using (var db = new CodeFlowDbContext(options.Options))
        {
            var repo = new PromptPartialRepository(db);
            var v1 = await repo.CreateNewVersionAsync(key, "Approve unless critical gap.", "claude", isSystemManaged: true, CancellationToken.None);
            v1.Should().Be(1);

            var fetched = await repo.GetAsync(key, 1, CancellationToken.None);
            fetched.Body.Should().Be("Approve unless critical gap.");
            fetched.IsSystemManaged.Should().BeTrue();

            var latest = await repo.GetLatestVersionAsync(key, CancellationToken.None);
            latest.Should().Be(1);
        }

        // Append v2 with a new body. v1 must remain untouched (immutability).
        await using (var db = new CodeFlowDbContext(options.Options))
        {
            var repo = new PromptPartialRepository(db);
            var v2 = await repo.CreateNewVersionAsync(key, "v2 body with more detail.", "claude", isSystemManaged: true, CancellationToken.None);
            v2.Should().Be(2);

            var v1Body = (await repo.GetAsync(key, 1, CancellationToken.None)).Body;
            v1Body.Should().Be("Approve unless critical gap.", "earlier versions are immutable");

            var latest = await repo.GetLatestVersionAsync(key, CancellationToken.None);
            latest.Should().Be(2);
        }

        // Resolve a mixed pin set. Missing entries are silently absent.
        await using (var db = new CodeFlowDbContext(options.Options))
        {
            var repo = new PromptPartialRepository(db);
            var bodies = await repo.ResolveBodiesAsync(
                new[] { (key, 1), (key, 2), ("@codeflow/never-existed", 1) },
                CancellationToken.None);

            // ResolveBodiesAsync is keyed by partial Key — when both versions are pinned the
            // last-loaded body wins. F3's contract says callers pin exactly one version per key
            // (cascade-bump tooling enforces this in the editor); the test exercises the lookup
            // mechanics, not the pin-uniqueness rule.
            bodies.Should().ContainKey(key);
            bodies.Should().NotContainKey("@codeflow/never-existed");
        }

        // GetAsync on missing throws PromptPartialNotFoundException.
        await using (var db = new CodeFlowDbContext(options.Options))
        {
            var repo = new PromptPartialRepository(db);
            var act = async () => await repo.GetAsync("@codeflow/nope", 1, CancellationToken.None);
            await act.Should().ThrowAsync<PromptPartialNotFoundException>();
        }
    }
}
