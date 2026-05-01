using System.Diagnostics;
using System.Text;

namespace CodeFlow.Runtime.Container;

public interface IDockerCommandRunner
{
    Task<DockerCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long stdoutMaxBytes,
        long stderrMaxBytes,
        CancellationToken cancellationToken = default);
}

public sealed record DockerCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    bool TimedOut)
{
    public bool OutputTruncated => StandardOutputTruncated || StandardErrorTruncated;
}

public sealed class DockerCliCommandRunner : IDockerCommandRunner
{
    public async Task<DockerCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long stdoutMaxBytes,
        long stderrMaxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new BoundedOutputBuffer(stdoutMaxBytes);
        var stderr = new BoundedOutputBuffer(stderrMaxBytes);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Unable to start the 'docker' process. Ensure Docker is installed and available on PATH.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            return new DockerCommandResult(
                ExitCode: -1,
                StandardOutput: stdout.ToString(),
                StandardError: stderr.ToString(),
                StandardOutputTruncated: stdout.Truncated,
                StandardErrorTruncated: stderr.Truncated,
                TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await process.WaitForExitAsync(CancellationToken.None);

        return new DockerCommandResult(
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            stdout.Truncated,
            stderr.Truncated,
            TimedOut: false);
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

    private sealed class BoundedOutputBuffer(long maxBytes)
    {
        private readonly StringBuilder builder = new();
        private long bytesWritten;

        public bool Truncated { get; private set; }

        public void AppendLine(string line) => Append(line + Environment.NewLine);

        private void Append(string value)
        {
            if (Truncated || string.IsNullOrEmpty(value))
            {
                return;
            }

            var remaining = maxBytes - bytesWritten;
            if (remaining <= 0)
            {
                Truncated = true;
                return;
            }

            var encoded = Encoding.UTF8.GetBytes(value);
            if (encoded.LongLength <= remaining)
            {
                builder.Append(value);
                bytesWritten += encoded.LongLength;
                return;
            }

            var prefix = Encoding.UTF8.GetString(encoded, 0, (int)Math.Max(0, remaining));
            builder.Append(prefix);
            bytesWritten = maxBytes;
            Truncated = true;
        }

        public override string ToString() => builder.ToString();
    }
}
