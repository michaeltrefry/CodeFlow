using System.Net.Http.Headers;

namespace CodeFlow.Runtime.Workspace;

public sealed class GitHostVerifier : IGitHostVerifier
{
    private readonly HttpClient httpClient;

    public GitHostVerifier(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
    }

    public async Task<GitHostVerificationResult> VerifyAsync(
        GitHostMode mode,
        string? baseUrl,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var (endpoint, authHeader) = mode switch
        {
            GitHostMode.GitHub => (
                new Uri("https://api.github.com/user"),
                new AuthenticationHeaderValue("Bearer", token)),
            GitHostMode.GitLab => (
                BuildGitLabUserEndpoint(baseUrl),
                new AuthenticationHeaderValue("Bearer", token)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported git host mode."),
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = authHeader;
        request.Headers.UserAgent.ParseAdd("CodeFlow");
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new GitHostVerificationResult(Success: true, Error: null);
            }

            return new GitHostVerificationResult(
                Success: false,
                Error: $"Verification failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (HttpRequestException ex)
        {
            return new GitHostVerificationResult(Success: false, Error: ex.Message);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return new GitHostVerificationResult(Success: false, Error: ex.Message);
        }
        catch (TaskCanceledException)
        {
            return new GitHostVerificationResult(Success: false, Error: "Verification timed out.");
        }
    }

    private static Uri BuildGitLabUserEndpoint(string? baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl, nameof(baseUrl));

        var trimmed = baseUrl.TrimEnd('/');
        return new Uri(trimmed + "/api/v4/user");
    }
}
