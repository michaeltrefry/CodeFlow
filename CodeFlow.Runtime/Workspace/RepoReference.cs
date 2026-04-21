namespace CodeFlow.Runtime.Workspace;

public sealed record RepoReference(string Host, string Owner, string Name)
{
    public string Slug => $"{Owner}-{Name}";

    public string MirrorRelativePath =>
        Path.Combine(Host, Owner, Name + ".git");

    public static RepoReference Parse(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                $"Repo URL must be an absolute http(s) URL: '{url}'.",
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
        var name = segments[1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return new RepoReference(uri.Host.ToLowerInvariant(), owner, name);
    }
}
