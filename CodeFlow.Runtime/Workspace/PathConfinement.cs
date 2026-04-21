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
        var resolved = ResolveSymlinksIfExists(combined);

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

    private static string ResolveSymlinksIfExists(string candidate)
    {
        if (File.Exists(candidate))
        {
            var fileInfo = new FileInfo(candidate);
            var resolved = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
            return resolved is null ? candidate : Path.GetFullPath(resolved.FullName);
        }

        if (Directory.Exists(candidate))
        {
            var dirInfo = new DirectoryInfo(candidate);
            var resolved = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
            return resolved is null ? candidate : Path.GetFullPath(resolved.FullName);
        }

        return candidate;
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
