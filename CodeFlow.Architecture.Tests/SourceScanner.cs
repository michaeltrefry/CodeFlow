using System.Runtime.CompilerServices;

namespace CodeFlow.Architecture.Tests;

/// <summary>
/// Walks .cs source files inside the repo and reports lines containing any of a set of
/// forbidden substring patterns. Used to express architectural boundary invariants as xUnit
/// tests rather than hoping reviewers catch every drift.
/// </summary>
internal static class SourceScanner
{
    public static string RepoRoot { get; } = ResolveRepoRoot();

    /// <summary>
    /// Scans every .cs file under <paramref name="projectRelativeRoot"/> (relative to repo root)
    /// for any of the supplied substring patterns. Skips bin/, obj/, and lines whose first
    /// non-whitespace token is a single-line or block-comment marker. Returns one record per
    /// matching line so a failing test can show the call site.
    /// </summary>
    public static IReadOnlyList<SourceMatch> Scan(
        string projectRelativeRoot,
        IReadOnlyList<string> forbiddenPatterns,
        IReadOnlyList<string>? excludeRelativePathFragments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRelativeRoot);
        ArgumentNullException.ThrowIfNull(forbiddenPatterns);

        var root = Path.Combine(RepoRoot, projectRelativeRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Architecture test target '{projectRelativeRoot}' not found at '{root}'.");
        }

        var matches = new List<SourceMatch>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
            if (IsBuildOutput(relative))
            {
                continue;
            }

            if (excludeRelativePathFragments is not null
                && excludeRelativePathFragments.Any(frag => relative.Contains(frag, StringComparison.Ordinal)))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsCommentLine(line))
                {
                    continue;
                }

                foreach (var pattern in forbiddenPatterns)
                {
                    if (line.Contains(pattern, StringComparison.Ordinal))
                    {
                        matches.Add(new SourceMatch(relative, i + 1, pattern, line.Trim()));
                    }
                }
            }
        }

        return matches;
    }

    private static bool IsBuildOutput(string relativePath)
    {
        return relativePath.Contains("/bin/", StringComparison.Ordinal)
            || relativePath.Contains("/obj/", StringComparison.Ordinal);
    }

    private static bool IsCommentLine(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        if (trimmed.StartsWith("//"))
        {
            return true;
        }

        if (trimmed[0] == '*')
        {
            return true;
        }

        if (trimmed.StartsWith("/*"))
        {
            return true;
        }

        return false;
    }

    private static string ResolveRepoRoot([CallerFilePath] string callerFilePath = "")
    {
        // The scanner source lives at <repo-root>/CodeFlow.Architecture.Tests/SourceScanner.cs.
        // Walk up until we find CodeFlow.slnx so the resolution survives both local builds
        // (where AppContext.BaseDirectory points into bin/) and any CI sandboxing.
        var dir = Path.GetDirectoryName(callerFilePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "CodeFlow.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            "Could not locate CodeFlow.slnx walking up from the architecture test source.");
    }
}

internal sealed record SourceMatch(string RelativePath, int Line, string Pattern, string Snippet)
{
    public override string ToString() => $"{RelativePath}:{Line} matched '{Pattern}': {Snippet}";
}
