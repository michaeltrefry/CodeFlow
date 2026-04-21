using CodeFlow.Runtime.Workspace;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class GitHostSettingsRepositoryTests : IAsyncLifetime
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
    public async Task SetAsync_persists_encrypted_token_and_never_stores_plaintext()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new GitHostSettingsRepository(context, protector);

        await repo.SetAsync(new GitHostSettingsWrite(
            Mode: GitHostMode.GitHub,
            BaseUrl: null,
            Token: "ghp_initial_secret",
            UpdatedBy: "admin"));

        var row = await context.GitHostSettings.AsNoTracking().SingleAsync();

        row.Mode.Should().Be(GitHostMode.GitHub);
        row.BaseUrl.Should().BeNull();
        row.UpdatedBy.Should().Be("admin");
        row.EncryptedToken.Length.Should().BeGreaterThan("ghp_initial_secret".Length);
        System.Text.Encoding.UTF8.GetString(row.EncryptedToken)
            .Should().NotContain("ghp_initial_secret");
    }

    [Fact]
    public async Task GetAsync_returns_hasToken_but_never_token_itself()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new GitHostSettingsRepository(context, protector);

        await repo.SetAsync(new GitHostSettingsWrite(
            Mode: GitHostMode.GitLab,
            BaseUrl: "https://gitlab.example.com",
            Token: "glpat_secret",
            UpdatedBy: "admin"));

        var settings = await repo.GetAsync();

        settings.Should().NotBeNull();
        settings!.Mode.Should().Be(GitHostMode.GitLab);
        settings.BaseUrl.Should().Be("https://gitlab.example.com");
        settings.HasToken.Should().BeTrue();
    }

    [Fact]
    public async Task GetDecryptedTokenAsync_returns_plaintext_for_runtime()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new GitHostSettingsRepository(context, protector);

        await repo.SetAsync(new GitHostSettingsWrite(
            GitHostMode.GitHub,
            null,
            "ghp_runtime_secret",
            "admin"));

        var token = await repo.GetDecryptedTokenAsync();

        token.Should().Be("ghp_runtime_secret");
    }

    [Fact]
    public async Task SetAsync_upserts_to_a_single_row()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new GitHostSettingsRepository(context, protector);

        await repo.SetAsync(new GitHostSettingsWrite(GitHostMode.GitHub, null, "first", "admin"));
        await repo.SetAsync(new GitHostSettingsWrite(GitHostMode.GitHub, null, "second", "admin"));
        await repo.SetAsync(new GitHostSettingsWrite(GitHostMode.GitHub, null, "third", "admin"));

        var rowCount = await context.GitHostSettings.AsNoTracking().CountAsync();
        rowCount.Should().Be(1);

        (await repo.GetDecryptedTokenAsync()).Should().Be("third");
    }

    [Fact]
    public async Task SetAsync_clears_LastVerifiedAt_when_mode_changes()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new GitHostSettingsRepository(context, protector);

        await repo.SetAsync(new GitHostSettingsWrite(GitHostMode.GitHub, null, "ghp_token", "admin"));
        await repo.MarkVerifiedAsync(new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc));

        (await repo.GetAsync())!.LastVerifiedAtUtc.Should().NotBeNull();

        await repo.SetAsync(new GitHostSettingsWrite(
            GitHostMode.GitLab,
            "https://gitlab.example.com",
            "glpat_token",
            "admin"));

        (await repo.GetAsync())!.LastVerifiedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task MarkVerifiedAsync_throws_when_settings_not_configured()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new GitHostSettingsRepository(context, protector);

        var act = () => repo.MarkVerifiedAsync(DateTime.UtcNow);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SetAsync_normalizes_baseUrl_by_trimming_trailing_slash()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new GitHostSettingsRepository(context, protector);

        await repo.SetAsync(new GitHostSettingsWrite(
            GitHostMode.GitLab,
            "https://gitlab.example.com/",
            "glpat",
            "admin"));

        (await repo.GetAsync())!.BaseUrl.Should().Be("https://gitlab.example.com");
    }

    [Fact]
    public async Task SetAsync_requires_baseUrl_for_gitlab_mode()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new GitHostSettingsRepository(context, protector);

        var act = () => repo.SetAsync(new GitHostSettingsWrite(
            GitHostMode.GitLab,
            null,
            "token",
            "admin"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
