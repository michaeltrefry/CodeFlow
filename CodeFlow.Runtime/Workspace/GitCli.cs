using System.Diagnostics;
using System.Text;

namespace CodeFlow.Runtime.Workspace;

public sealed class GitCli : IGitCli
{
    private readonly WorkspaceOptions options;

    public GitCli(WorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    public async Task CloneMirrorAsync(
        string originUrl,
        string destinationMirrorPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationMirrorPath);

        await RunAsync(
            workingDirectory: null,
            arguments: ["clone", "--mirror", "--", originUrl, destinationMirrorPath],
            cancellationToken: cancellationToken);
    }

    public async Task<GitCloneResult> CloneAsync(
        string originUrl,
        string destinationPath,
        string? branch = null,
        int? depth = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var args = new List<string> { "clone" };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add("--branch");
            args.Add(branch);
        }
        if (depth is int d && d > 0)
        {
            args.Add("--depth");
            args.Add(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        args.Add("--");
        args.Add(originUrl);
        args.Add(destinationPath);

        await RunAsync(
            workingDirectory: null,
            arguments: args,
            environmentVariables: environmentVariables,
            cancellationToken: cancellationToken);

        var resolvedBranch = string.IsNullOrWhiteSpace(branch)
            ? await GetSymbolicHeadAsync(destinationPath, cancellationToken)
            : branch!;
        var head = await RevParseAsync(destinationPath, "HEAD", cancellationToken);
        // Default branch via remote origin's HEAD pointer; falls back to the resolved branch
        // when the remote didn't advertise one (rare, but shallow clones of single branches
        // sometimes leave origin/HEAD unset).
        string defaultBranch;
        try
        {
            var remoteHead = await RunRawAsync(
                destinationPath,
                ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"],
                cancellationToken);
            defaultBranch = remoteHead.ExitCode == 0
                ? StripOriginPrefix(remoteHead.StandardOutput.Trim())
                : resolvedBranch;
        }
        catch (GitCommandException)
        {
            defaultBranch = resolvedBranch;
        }

        return new GitCloneResult(resolvedBranch, head, defaultBranch);
    }

    private static string StripOriginPrefix(string symbolicRef)
    {
        const string prefix = "origin/";
        return symbolicRef.StartsWith(prefix, StringComparison.Ordinal)
            ? symbolicRef[prefix.Length..]
            : symbolicRef;
    }

    public async Task FetchAsync(string mirrorPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorPath);

        await RunAsync(
            workingDirectory: mirrorPath,
            arguments: ["fetch", "--prune", "--prune-tags"],
            cancellationToken: cancellationToken);
    }

    public async Task WorktreeAddAsync(
        string mirrorPath,
        string worktreePath,
        string branchName,
        string? startPoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        var args = new List<string> { "worktree", "add", "-b", branchName, "--", worktreePath };
        if (!string.IsNullOrWhiteSpace(startPoint))
        {
            args.Add(startPoint);
        }

        await RunAsync(mirrorPath, args, cancellationToken);
    }

    public async Task WorktreeRemoveAsync(
        string mirrorPath,
        string worktreePath,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        var args = new List<string> { "worktree", "remove" };
        if (force)
        {
            args.Add("--force");
        }

        args.Add("--");
        args.Add(worktreePath);

        await RunAsync(mirrorPath, args, cancellationToken);
    }

    public async Task CreateBranchAsync(
        string worktreePath,
        string branchName,
        string? startPoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        var args = new List<string> { "checkout", "-b", branchName };
        if (!string.IsNullOrWhiteSpace(startPoint))
        {
            args.Add(startPoint);
        }

        await RunAsync(worktreePath, args, cancellationToken);
    }

    public async Task CheckoutAsync(
        string worktreePath,
        string branchOrRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchOrRef);

        await RunAsync(worktreePath, ["checkout", "--", branchOrRef], cancellationToken);
    }

    public async Task AddAsync(
        string worktreePath,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        var args = new List<string> { "add" };
        if (paths is null || paths.Count == 0)
        {
            args.Add("-A");
        }
        else
        {
            args.Add("--");
            args.AddRange(paths);
        }

        await RunAsync(worktreePath, args, cancellationToken);
    }

    public async Task<bool> CommitAsync(
        string worktreePath,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var result = await RunRawAsync(
            worktreePath,
            ["commit", "-m", message],
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return true;
        }

        var combined = result.StandardOutput + "\n" + result.StandardError;
        if (combined.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("no changes added to commit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new GitCommandException(
            ["commit", "-m", message],
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }

    public async Task PushAsync(
        string worktreePath,
        string? remote = null,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        var args = new List<string> { "push" };
        if (!string.IsNullOrWhiteSpace(remote))
        {
            args.Add(remote);
            if (!string.IsNullOrWhiteSpace(branch))
            {
                args.Add(branch);
            }
        }

        await RunAsync(worktreePath, args, cancellationToken);
    }

    public async Task SetRemoteUrlAsync(
        string worktreePath,
        string remoteName,
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        await RunAsync(
            workingDirectory: worktreePath,
            arguments: ["remote", "set-url", remoteName, url],
            cancellationToken: cancellationToken);
    }

    public async Task<string> RevParseAsync(
        string worktreePath,
        string rev,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rev);

        var result = await RunAsync(worktreePath, ["rev-parse", rev], cancellationToken);
        return result.StandardOutput.Trim();
    }

    public async Task<string> GetSymbolicHeadAsync(
        string gitDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitDirectory);

        var result = await RunAsync(gitDirectory, ["symbolic-ref", "--short", "HEAD"], cancellationToken);
        return result.StandardOutput.Trim();
    }

    public async Task<IReadOnlyList<string>> LsFilesAsync(
        string worktreePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        var result = await RunAsync(worktreePath, ["ls-files"], cancellationToken);
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<IReadOnlyList<GitStatusEntry>> StatusAsync(
        string worktreePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        var result = await RunAsync(worktreePath, ["status", "--porcelain"], cancellationToken);
        var entries = new List<GitStatusEntry>();
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            if (line.Length < 3)
            {
                continue;
            }

            var code = line[..2];
            var path = line[3..].Trim();
            entries.Add(new GitStatusEntry(code, path));
        }

        return entries;
    }

    private Task<GitCommandResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => RunAsync(workingDirectory, arguments, environmentVariables: null, cancellationToken);

    private async Task<GitCommandResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken)
    {
        var result = await RunRawAsync(workingDirectory, arguments, environmentVariables, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new GitCommandException(
                arguments,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError);
        }

        return result;
    }

    private Task<GitCommandResult> RunRawAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => RunRawAsync(workingDirectory, arguments, environmentVariables: null, cancellationToken);

    private async Task<GitCommandResult> RunRawAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (environmentVariables is { Count: > 0 })
        {
            foreach (var kv in environmentVariables)
            {
                startInfo.Environment[kv.Key] = kv.Value;
            }
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdoutBuffer.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderrBuffer.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Unable to start the 'git' process. Ensure git is installed and available on PATH.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(options.GitCommandTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"git {string.Join(' ', arguments)} exceeded timeout of {options.GitCommandTimeout}.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await process.WaitForExitAsync(CancellationToken.None);

        return new GitCommandResult(
            process.ExitCode,
            stdoutBuffer.ToString(),
            stderrBuffer.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
