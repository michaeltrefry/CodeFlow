using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace CodeFlow.Api.Auth;

public sealed class PermissionsApiClient : IPermissionsApiClient
{
    private readonly HttpClient httpClient;
    private readonly CompanyAuthOptions options;
    private readonly ILogger<PermissionsApiClient> logger;

    public PermissionsApiClient(
        HttpClient httpClient,
        IOptions<AuthOptions> options,
        ILogger<PermissionsApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.httpClient = httpClient;
        this.options = options.Value.Company;
        this.logger = logger;

        if (!string.IsNullOrWhiteSpace(this.options.PermissionsApiBaseUrl)
            && Uri.TryCreate(this.options.PermissionsApiBaseUrl, UriKind.Absolute, out var baseUri))
        {
            httpClient.BaseAddress = baseUri;
        }

        if (!string.IsNullOrWhiteSpace(this.options.PermissionsApiApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                this.options.PermissionsApiApiKey);
        }
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (httpClient.BaseAddress is null)
        {
            logger.LogDebug(
                "PermissionsApi base URL not configured; returning no permissions for user {UserId}.",
                userId);
            return Array.Empty<string>();
        }

        try
        {
            using var response = await httpClient.GetAsync(
                $"permissions/{Uri.EscapeDataString(userId)}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "PermissionsApi returned {StatusCode} for user {UserId}.",
                    response.StatusCode,
                    userId);
                return Array.Empty<string>();
            }

            var permissions = await response.Content.ReadFromJsonAsync<IReadOnlyList<string>>(cancellationToken);
            return permissions ?? Array.Empty<string>();
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "PermissionsApi call failed for user {UserId}.", userId);
            return Array.Empty<string>();
        }
    }
}
