using System.Diagnostics;

namespace CodeFlow.Runtime.Tests.Workspace;

internal static class GitTestRepo
{
    public static string CreateTempDirectory(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start git.");

        process.WaitForExit(30_000);

        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed: {err}");
        }
    }

    public static string InitRepo(string prefix = "codeflow-gittest")
    {
        var dir = CreateTempDirectory(prefix);
        RunGit(dir, "init", "-b", "main");
        RunGit(dir, "config", "user.email", "test@codeflow.local");
        RunGit(dir, "config", "user.name", "CodeFlow Test");
        RunGit(dir, "commit", "--allow-empty", "-m", "initial");
        return dir;
    }

    public static void SafeDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
