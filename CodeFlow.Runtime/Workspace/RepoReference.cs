using System.Security.Cryptography;
using System.Text;

namespace CodeFlow.Runtime.Workspace;

public sealed record RepoReference(string Host, string Owner, string Name)
{
    /// <summary>
    /// Human-readable identifier combining the full owner path and repo name. Forward slashes in
    /// <see cref="Owner"/> are flattened to dashes for filesystem/branch friendliness.
    /// </summary>
    public string Slug => Sanitize($"{Owner}/{Name}");

    /// <summary>
    /// Collision-free identity derived from the full parsed <c>(Host, Owner, Name)</c> tuple.
    /// Used as the workspace dictionary key and as the suffix on local worktree paths so that two
    /// different repo URLs with slug-colliding paths (e.g. <c>a/b-c</c> vs. <c>a-b/c</c>) can
    /// never share a workspace entry.
    /// </summary>
    public string IdentityKey => ComputeIdentityKey(Host, Owner, Name);

    public string MirrorRelativePath
    {
        get
        {
            var parts = new List<string> { Host };
            parts.AddRange(Owner.Split('/', StringSplitOptions.RemoveEmptyEntries));
            parts.Add(Name + ".git");
            return Path.Combine(parts.ToArray());
        }
    }

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

        // Preserve the full path so nested GitLab-style repos
        // (e.g. group/subgroup/repo) get a distinct identity instead of aliasing to the first
        // two segments.
        var name = StripGitSuffix(segments[^1]);
        var owner = string.Join('/', segments[..^1]);

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
        var owner = segments.Length >= 2
            ? string.Join('/', segments[..^1])
            : "local";
        return new RepoReference("local", owner, name);
    }

    private static string StripGitSuffix(string name)
    {
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string Sanitize(string value)
    {
        return value
            .Replace('/', '-')
            .Replace('\\', '-')
            .Replace(' ', '-');
    }

    private static string ComputeIdentityKey(string host, string owner, string name)
    {
        var bytes = Encoding.UTF8.GetBytes($"{host.ToLowerInvariant()}|{owner}|{name}");
        var hash = SHA256.HashData(bytes);
        // 16 hex chars = 64 bits of entropy. Collision-safe for any realistic cache.
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
