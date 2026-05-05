using System.Text;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Append-only string buffer that caps total UTF-8 byte length and flips a Truncated flag
/// once the cap is reached. Shared by `WorkspaceHostToolService` and `DockerCommandRunner`
/// for capturing process stdout/stderr without unbounded memory growth.
/// </summary>
internal sealed class BoundedOutputBuffer(long maxBytes)
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
