using System.Collections.Generic;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Builds the environment-variable set that points <c>git</c> at the per-trace credential file
/// (epic 658). Used by both <see cref="WorkspaceHostToolService"/> when the agent spawns
/// <c>git</c> via <c>run_command</c> and by <see cref="VcsHostToolService"/> when the platform
/// runs <c>git clone</c> on the agent's behalf — same env shape, same per-trace file path, no
/// global gitconfig mutation so concurrent traces in the same worker can't collide.
///
/// Format follows <c>git-config(1)</c>'s ad-hoc-config protocol: <c>GIT_CONFIG_COUNT</c> +
/// numbered <c>GIT_CONFIG_KEY_n</c> / <c>GIT_CONFIG_VALUE_n</c> pairs. The credential helper
/// is git's built-in <c>store</c> helper pointed at the file path — no custom helper script
/// in the path.
/// </summary>
public static class GitCredentialEnv
{
    /// <summary>
    /// Returns the env-var dictionary, or an empty dictionary when <paramref name="credentialRoot"/>
    /// is null/empty (the platform isn't configured to run with git auth, e.g. tests that don't
    /// need it). Callers add the returned entries to <c>ProcessStartInfo.Environment</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(string? credentialRoot, Guid traceId)
    {
        if (string.IsNullOrWhiteSpace(credentialRoot))
        {
            return EmptyEnv;
        }

        var credentialFile = GitCredentialFile.BuildPath(credentialRoot, traceId);
        // The `store` helper accepts a single positional arg pointing at the file. Quoting
        // around the path keeps git from splitting on spaces if an operator overrode
        // `Workspace__GitCredentialRoot` to a path with whitespace.
        var helperValue = $"store --file=\"{credentialFile}\"";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "credential.helper",
            ["GIT_CONFIG_VALUE_0"] = helperValue,
            ["GIT_CONFIG_KEY_1"] = "credential.useHttpPath",
            ["GIT_CONFIG_VALUE_1"] = "true",
        };
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyEnv =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
