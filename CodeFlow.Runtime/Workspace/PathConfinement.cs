namespace CodeFlow.Runtime.Workspace;

public static class PathConfinement
{
    public static string Resolve(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new PathConfinementException(
                $"Absolute paths are not permitted in workspace operations: '{relativePath}'.");
        }

        var normalizedRoot = NormalizeExistingDirectory(root);
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!IsContained(normalizedRoot, combined))
        {
            throw new PathConfinementException(
                $"Path '{relativePath}' resolves outside of the workspace root '{normalizedRoot}'.");
        }

        var resolved = ResolveSymlinkedComponents(normalizedRoot, combined);

        if (!IsContained(normalizedRoot, resolved))
        {
            throw new PathConfinementException(
                $"Path '{relativePath}' resolves outside of the workspace root '{normalizedRoot}'.");
        }

        return resolved;
    }

    public static bool TryResolve(string root, string relativePath, out string resolvedPath)
    {
        try
        {
            resolvedPath = Resolve(root, relativePath);
            return true;
        }
        catch (PathConfinementException)
        {
            resolvedPath = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if any existing segment of <paramref name="relativePath"/> under
    /// <paramref name="root"/> is itself a symlink (file or directory). Missing tail segments
    /// are ignored — only segments that exist on disk are inspected.
    /// </summary>
    public static bool ContainsSymlink(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relativePath);

        var normalizedRoot = NormalizeExistingDirectory(root);
        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = normalizedRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            if (Directory.Exists(current))
            {
                if (new DirectoryInfo(current).LinkTarget is not null)
                {
                    return true;
                }

                continue;
            }

            if (File.Exists(current))
            {
                if (new FileInfo(current).LinkTarget is not null)
                {
                    return true;
                }

                continue;
            }

            // Segment does not exist; nothing further to check on disk.
            break;
        }

        return false;
    }

    private static string NormalizeExistingDirectory(string root)
    {
        var fullRoot = Path.GetFullPath(root);

        if (Directory.Exists(fullRoot))
        {
            var resolved = new DirectoryInfo(fullRoot).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? fullRoot;
            return TrimTrailingSeparator(Path.GetFullPath(resolved));
        }

        return TrimTrailingSeparator(fullRoot);
    }

    private static string ResolveSymlinkedComponents(string root, string candidate)
    {
        if (!Directory.Exists(root))
        {
            return candidate;
        }

        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
        {
            return root;
        }

        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);

            if (Directory.Exists(current))
            {
                var dirInfo = new DirectoryInfo(current);
                var resolved = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null)
                {
                    current = Path.GetFullPath(resolved.FullName);
                }

                continue;
            }

            if (File.Exists(current))
            {
                var fileInfo = new FileInfo(current);
                var resolved = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null)
                {
                    current = Path.GetFullPath(resolved.FullName);
                }
            }

            if (i + 1 >= segments.Length)
            {
                return current;
            }

            return Path.GetFullPath(Path.Combine(
                current,
                Path.Combine(segments[(i + 1)..])));
        }

        return current;
    }

    private static bool IsContained(string root, string candidate)
    {
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(candidate, root, comparison)
            || candidate.StartsWith(rootWithSeparator, comparison);
    }

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1)
        {
            return path;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path[..1] : trimmed;
    }
}
