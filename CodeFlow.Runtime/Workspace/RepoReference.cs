namespace CodeFlow.Runtime.Workspace;

public sealed record RepoReference(string Host, string Owner, string Name)
{
    public string Slug => $"{Owner}-{Name}";

    public string MirrorRelativePath =>
        Path.Combine(Host, Owner, Name + ".git");

    public static RepoReference Parse(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Repo URL is not a valid absolute URI: '{url}'.", nameof(url));
        }

        if (uri.Scheme == Uri.UriSchemeFile)
        {
            return ParseFileUri(uri, url);
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException(
                $"Repo URL must be an http(s) or file URL: '{url}'.",
                nameof(url));
        }

        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2)
        {
            throw new ArgumentException(
                $"Repo URL must include owner and repo: '{url}'.",
                nameof(url));
        }

        var owner = segments[0];
        var name = StripGitSuffix(segments[1]);

        return new RepoReference(uri.Host.ToLowerInvariant(), owner, name);
    }

    private static RepoReference ParseFileUri(Uri uri, string originalUrl)
    {
        var segments = uri.AbsolutePath
            .TrimStart('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 1)
        {
            throw new ArgumentException(
                $"Repo URL must include a repo path: '{originalUrl}'.",
                nameof(originalUrl));
        }

        var name = StripGitSuffix(segments[^1]);
        var owner = segments.Length >= 2 ? segments[^2] : "local";
        return new RepoReference("local", owner, name);
    }

    private static string StripGitSuffix(string name)
    {
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
