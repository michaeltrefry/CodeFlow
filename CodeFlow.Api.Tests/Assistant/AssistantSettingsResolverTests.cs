using CodeFlow.Api.Assistant;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Tests.Assistant;

public sealed class AssistantSettingsResolverTests
{
    [Fact]
    public async Task ResolveAsync_LmStudioWithoutApiKey_AllowsLocalProvider()
    {
        var resolver = CreateResolver(
            new AssistantOptions { Provider = LlmProviderKeys.LmStudio },
            assistantSettings: null,
            new StubProviderSettingsRepository(new LlmProviderSettings(
                Provider: LlmProviderKeys.LmStudio,
                HasApiKey: false,
                EndpointUrl: "http://localhost:1234/v1",
                ApiVersion: null,
                Models: ["qwen2.5-coder-32b"],
                UpdatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow)));

        var config = await resolver.ResolveAsync();

        config.Provider.Should().Be(LlmProviderKeys.LmStudio);
        config.Model.Should().Be("qwen2.5-coder-32b");
    }

    [Fact]
    public async Task ResolveAsync_OpenAiWithoutApiKey_StillRejectsMissingSecret()
    {
        var resolver = CreateResolver(
            new AssistantOptions { Provider = LlmProviderKeys.OpenAi },
            assistantSettings: null,
            new StubProviderSettingsRepository(new LlmProviderSettings(
                Provider: LlmProviderKeys.OpenAi,
                HasApiKey: false,
                EndpointUrl: "https://api.openai.com/v1",
                ApiVersion: null,
                Models: ["gpt-5.4-mini"],
                UpdatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow)));

        var act = async () => await resolver.ResolveAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Assistant provider 'openai' has no API key configured.");
    }

    [Fact]
    public void FallbackApiKey_LmStudio_ReturnsLocalPlaceholderForSdkCredential()
    {
        LlmProviderAuthentication.RequiresApiKey(LlmProviderKeys.LmStudio).Should().BeFalse();
        LlmProviderAuthentication.FallbackApiKey(LlmProviderKeys.LmStudio)
            .Should().Be(LlmProviderAuthentication.LocalLmStudioApiKeyPlaceholder);
    }

    private static AssistantSettingsResolver CreateResolver(
        AssistantOptions options,
        AssistantSettings? assistantSettings,
        ILlmProviderSettingsRepository providerSettings)
    {
        return new AssistantSettingsResolver(
            Options.Create(options),
            new StubAssistantSettingsRepository(assistantSettings),
            providerSettings);
    }

    private sealed class StubAssistantSettingsRepository(AssistantSettings? settings) : IAssistantSettingsRepository
    {
        public Task<AssistantSettings?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task<AssistantSettings> SetAsync(
            AssistantSettingsWrite write,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubProviderSettingsRepository(LlmProviderSettings settings) : ILlmProviderSettingsRepository
    {
        public Task<IReadOnlyList<LlmProviderSettings>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmProviderSettings>>([settings]);

        public Task<LlmProviderSettings?> GetAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(settings.Provider, LlmProviderKeys.Canonicalize(provider), StringComparison.Ordinal)
                ? settings
                : null);

        public Task<string?> GetDecryptedApiKeyAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(settings.HasApiKey ? "test-key" : null);

        public Task SetAsync(LlmProviderSettingsWrite write, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
