using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class McpServerRepositoryTests : IAsyncLifetime
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

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_round_trips_server_and_encrypts_bearer_token()
    {
        var key = $"artifacts-{Guid.NewGuid():N}";

        await using var writeContext = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(writeContext, protector);

        var id = await repo.CreateAsync(new McpServerCreate(
            Key: key,
            DisplayName: "Artifacts",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://artifacts.local/mcp",
            BearerTokenPlaintext: "super-secret-token",
            CreatedBy: "tester"));

        await using var readContext = CreateDbContext();
        var readRepo = new McpServerRepository(readContext, protector);

        var server = await readRepo.GetAsync(id);
        server.Should().NotBeNull();
        server!.Key.Should().Be(key);
        server.DisplayName.Should().Be("Artifacts");
        server.Transport.Should().Be(McpTransportKind.StreamableHttp);
        server.EndpointUrl.Should().Be("https://artifacts.local/mcp");
        server.HasBearerToken.Should().BeTrue();
        server.HealthStatus.Should().Be(McpServerHealthStatus.Unverified);
        server.CreatedBy.Should().Be("tester");
        server.IsArchived.Should().BeFalse();

        var raw = await readContext.McpServers
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => s.BearerTokenCipher)
            .SingleAsync();

        raw.Should().NotBeNull();
        raw!.Length.Should().BeGreaterThan("super-secret-token".Length);
    }

    [Fact]
    public async Task GetConnectionInfoAsync_returns_plaintext_token_for_runtime()
    {
        var key = $"search-{Guid.NewGuid():N}";

        await using var writeContext = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(writeContext, protector);

        await repo.CreateAsync(new McpServerCreate(
            Key: key,
            DisplayName: "Search",
            Transport: McpTransportKind.HttpSse,
            EndpointUrl: "https://search.local/mcp",
            BearerTokenPlaintext: "plain-text-token",
            CreatedBy: null));

        await using var readContext = CreateDbContext();
        var readRepo = new McpServerRepository(readContext, protector);

        var info = await readRepo.GetConnectionInfoAsync(key);

        info.Should().NotBeNull();
        info!.Key.Should().Be(key);
        info.Endpoint.ToString().Should().Be("https://search.local/mcp");
        info.Transport.Should().Be(McpTransportKind.HttpSse);
        info.BearerToken.Should().Be("plain-text-token");
    }

    [Fact]
    public async Task UpdateAsync_with_Clear_removes_bearer_token_ciphertext()
    {
        var key = $"svc-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(context, protector);

        var id = await repo.CreateAsync(new McpServerCreate(
            Key: key,
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerTokenPlaintext: "abc",
            CreatedBy: null));

        await repo.UpdateAsync(id, new McpServerUpdate(
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerToken: BearerTokenUpdate.Clear(),
            UpdatedBy: "admin"));

        var server = await repo.GetAsync(id);
        server!.HasBearerToken.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_with_Preserve_keeps_existing_ciphertext()
    {
        var key = $"svc-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(context, protector);

        var id = await repo.CreateAsync(new McpServerCreate(
            Key: key,
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerTokenPlaintext: "original",
            CreatedBy: null));

        await repo.UpdateAsync(id, new McpServerUpdate(
            DisplayName: "Svc Renamed",
            Transport: McpTransportKind.HttpSse,
            EndpointUrl: "https://svc.local/mcp",
            BearerToken: BearerTokenUpdate.Preserve(),
            UpdatedBy: "admin"));

        var info = await repo.GetConnectionInfoAsync(key);
        info!.BearerToken.Should().Be("original");

        var server = await repo.GetAsync(id);
        server!.DisplayName.Should().Be("Svc Renamed");
        server.Transport.Should().Be(McpTransportKind.HttpSse);
    }

    [Fact]
    public async Task UpdateAsync_with_Replace_substitutes_ciphertext()
    {
        var key = $"svc-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(context, protector);

        var id = await repo.CreateAsync(new McpServerCreate(
            Key: key,
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerTokenPlaintext: "original",
            CreatedBy: null));

        await repo.UpdateAsync(id, new McpServerUpdate(
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerToken: BearerTokenUpdate.Replace("rotated"),
            UpdatedBy: "admin"));

        var info = await repo.GetConnectionInfoAsync(key);
        info!.BearerToken.Should().Be("rotated");
    }

    [Fact]
    public async Task ArchiveAsync_marks_server_archived_and_hides_from_default_list()
    {
        var key = $"svc-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(context, protector);

        var id = await repo.CreateAsync(new McpServerCreate(
            Key: key,
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerTokenPlaintext: null,
            CreatedBy: null));

        await repo.ArchiveAsync(id);

        var listed = await repo.ListAsync(includeArchived: false);
        listed.Should().NotContain(s => s.Id == id);

        var listedAll = await repo.ListAsync(includeArchived: true);
        listedAll.Should().Contain(s => s.Id == id);

        var info = await repo.GetConnectionInfoAsync(key);
        info.Should().BeNull("archived servers should not be resolvable for runtime invocation");
    }

    [Fact]
    public async Task UpdateHealthAsync_persists_status_timestamp_and_error()
    {
        var key = $"svc-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(context, protector);

        var id = await repo.CreateAsync(new McpServerCreate(
            Key: key,
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerTokenPlaintext: null,
            CreatedBy: null));

        var verifiedAt = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
        await repo.UpdateHealthAsync(id, McpServerHealthStatus.Unhealthy, verifiedAt, "connection refused");

        var server = await repo.GetAsync(id);
        server!.HealthStatus.Should().Be(McpServerHealthStatus.Unhealthy);
        server.LastVerifiedAtUtc.Should().Be(verifiedAt);
        server.LastVerificationError.Should().Be("connection refused");
    }

    [Fact]
    public async Task ReplaceToolsAsync_swaps_tool_list_and_cascades_on_server_delete()
    {
        var key = $"svc-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(context, protector);

        var id = await repo.CreateAsync(new McpServerCreate(
            Key: key,
            DisplayName: "Svc",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://svc.local/mcp",
            BearerTokenPlaintext: null,
            CreatedBy: null));

        await repo.ReplaceToolsAsync(id,
        [
            new McpServerToolWrite("read", "read an artifact", """{"type":"object"}""", IsMutating: false),
            new McpServerToolWrite("write", "write an artifact", null, IsMutating: true),
        ]);

        var tools = await repo.GetToolsAsync(id);
        tools.Should().HaveCount(2);
        tools.Select(t => t.ToolName).Should().BeEquivalentTo(new[] { "read", "write" });
        tools.Single(t => t.ToolName == "write").IsMutating.Should().BeTrue();

        // Replace the tool list wholesale
        await repo.ReplaceToolsAsync(id,
        [
            new McpServerToolWrite("list", "list artifacts", null, IsMutating: false),
        ]);

        var afterReplace = await repo.GetToolsAsync(id);
        afterReplace.Should().HaveCount(1);
        afterReplace.Single().ToolName.Should().Be("list");

        // Delete the server and prove tools cascade
        var entity = await context.McpServers.SingleAsync(s => s.Id == id);
        context.McpServers.Remove(entity);
        await context.SaveChangesAsync();

        var orphanToolCount = await context.McpServerTools.CountAsync(t => t.ServerId == id);
        orphanToolCount.Should().Be(0);
    }

    [Fact]
    public async Task GetConnectionInfoAsync_matches_keys_case_insensitively()
    {
        // Defensive: role-grant identifiers are stored verbatim from the admin UI, so a grant
        // text like "mcp:codegraph:..." against a DB row keyed "CodeGraph" must still resolve.
        var key = $"GrAph-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(context, protector);

        await repo.CreateAsync(new McpServerCreate(
            Key: key,
            DisplayName: "Graph",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://graph.local/mcp",
            BearerTokenPlaintext: null,
            CreatedBy: null));

        var lowered = await repo.GetConnectionInfoAsync(key.ToLowerInvariant());
        lowered.Should().NotBeNull();
        lowered!.Endpoint.ToString().Should().Be("https://graph.local/mcp");

        var uppered = await repo.GetConnectionInfoAsync(key.ToUpperInvariant());
        uppered.Should().NotBeNull();

        var unrelated = await repo.GetConnectionInfoAsync($"unrelated-{Guid.NewGuid():N}");
        unrelated.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_throws_McpServerNotFoundException_for_unknown_id()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new McpServerRepository(context, protector);

        var act = () => repo.UpdateAsync(99999, new McpServerUpdate(
            DisplayName: "x",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "https://x/mcp",
            BearerToken: BearerTokenUpdate.Preserve(),
            UpdatedBy: null));

        await act.Should().ThrowAsync<McpServerNotFoundException>();
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
