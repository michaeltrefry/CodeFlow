namespace CodeFlow.Runtime.Tests.Workspace;

internal static class VcsIntegrationEnv
{
    public const string GitHubTokenVar = "CODEFLOW_GITHUB_TEST_TOKEN";
    public const string GitHubRepoVar = "CODEFLOW_GITHUB_TEST_REPO";

    public const string GitLabTokenVar = "CODEFLOW_GITLAB_TEST_TOKEN";
    public const string GitLabUrlVar = "CODEFLOW_GITLAB_TEST_URL";
    public const string GitLabRepoVar = "CODEFLOW_GITLAB_TEST_REPO";

    public static (string token, string owner, string name)? GitHub()
    {
        var token = Environment.GetEnvironmentVariable(GitHubTokenVar);
        var repo = Environment.GetEnvironmentVariable(GitHubRepoVar);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        var parts = repo.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        return (token, parts[0], parts[1]);
    }

    public static (string token, string baseUrl, string owner, string name)? GitLab()
    {
        var token = Environment.GetEnvironmentVariable(GitLabTokenVar);
        var url = Environment.GetEnvironmentVariable(GitLabUrlVar);
        var repo = Environment.GetEnvironmentVariable(GitLabRepoVar);
        if (string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(url) ||
            string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        var parts = repo.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        return (token, url, parts[0], parts[1]);
    }
}
