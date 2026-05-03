using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class WebSearchProviderSettingsRepositoryTests : IAsyncLifetime
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
    public async Task GetAsync_returns_null_when_no_row_has_been_written()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new WebSearchProviderSettingsRepository(context, protector);

        var current = await repo.GetAsync();

        current.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_round_trips_provider_and_encrypts_api_key()
    {
        await using var writeContext = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new WebSearchProviderSettingsRepository(writeContext, protector);

        await repo.SetAsync(new WebSearchProviderSettingsWrite(
            Provider: WebSearchProviderKeys.Brave,
            EndpointUrl: null,
            Token: WebSearchProviderTokenUpdate.Replace("brave-key-1234"),
            UpdatedBy: "tester"));

        await using var readContext = CreateDbContext();
        var readRepo = new WebSearchProviderSettingsRepository(readContext, protector);

        var settings = await readRepo.GetAsync();
        settings.Should().NotBeNull();
        settings!.Provider.Should().Be(WebSearchProviderKeys.Brave);
        settings.HasApiKey.Should().BeTrue();
        settings.UpdatedBy.Should().Be("tester");

        var apiKey = await readRepo.GetDecryptedApiKeyAsync();
        apiKey.Should().Be("brave-key-1234");

        // Defense-in-depth: the at-rest blob is real ciphertext, not the plaintext.
        var raw = await readContext.WebSearchProviders
            .AsNoTracking()
            .Where(e => e.Id == WebSearchProviderSettingsEntity.SingletonId)
            .Select(e => e.EncryptedApiKey)
            .SingleAsync();
        raw.Should().NotBeNull();
        raw!.Length.Should().BeGreaterThan("brave-key-1234".Length);
    }

    [Fact]
    public async Task SetAsync_with_Preserve_keeps_existing_token_on_an_update()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new WebSearchProviderSettingsRepository(context, protector);

        await repo.SetAsync(new WebSearchProviderSettingsWrite(
            Provider: WebSearchProviderKeys.Brave,
            EndpointUrl: null,
            Token: WebSearchProviderTokenUpdate.Replace("original-key"),
            UpdatedBy: "tester"));

        await repo.SetAsync(new WebSearchProviderSettingsWrite(
            Provider: WebSearchProviderKeys.Brave,
            EndpointUrl: "https://custom.example.com/search",
            Token: WebSearchProviderTokenUpdate.Preserve(),
            UpdatedBy: "tester"));

        var apiKey = await repo.GetDecryptedApiKeyAsync();
        apiKey.Should().Be("original-key");

        var settings = await repo.GetAsync();
        settings!.EndpointUrl.Should().Be("https://custom.example.com/search");
    }

    [Fact]
    public async Task SetAsync_with_Clear_removes_the_stored_token_but_preserves_the_row()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new WebSearchProviderSettingsRepository(context, protector);

        await repo.SetAsync(new WebSearchProviderSettingsWrite(
            Provider: WebSearchProviderKeys.Brave,
            EndpointUrl: null,
            Token: WebSearchProviderTokenUpdate.Replace("key"),
            UpdatedBy: "tester"));

        await repo.SetAsync(new WebSearchProviderSettingsWrite(
            Provider: WebSearchProviderKeys.None,
            EndpointUrl: null,
            Token: WebSearchProviderTokenUpdate.Clear(),
            UpdatedBy: "tester"));

        var apiKey = await repo.GetDecryptedApiKeyAsync();
        apiKey.Should().BeNull();

        var settings = await repo.GetAsync();
        settings!.Provider.Should().Be(WebSearchProviderKeys.None);
        settings.HasApiKey.Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_rejects_an_unknown_provider_key()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new WebSearchProviderSettingsRepository(context, protector);

        var act = () => repo.SetAsync(new WebSearchProviderSettingsWrite(
            Provider: "kagi",
            EndpointUrl: null,
            Token: WebSearchProviderTokenUpdate.Preserve(),
            UpdatedBy: null));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetAsync_rejects_Replace_with_blank_value()
    {
        await using var context = CreateDbContext();
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        var repo = new WebSearchProviderSettingsRepository(context, protector);

        var act = () => repo.SetAsync(new WebSearchProviderSettingsWrite(
            Provider: WebSearchProviderKeys.Brave,
            EndpointUrl: null,
            Token: WebSearchProviderTokenUpdate.Replace("   "),
            UpdatedBy: null));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
