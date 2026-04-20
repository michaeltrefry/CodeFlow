namespace CodeFlow.Host.DeadLetter;

public sealed class DeadLetterOptions
{
    public const string SectionName = "DeadLetter";

    public string ManagementHost { get; set; } = "127.0.0.1";

    public int ManagementPort { get; set; } = 15672;

    public bool UseHttps { get; set; }

    public string VirtualHost { get; set; } = "codeflow";

    public string Username { get; set; } = "codeflow";

    public string Password { get; set; } = "codeflow_dev";

    public string ErrorQueueSuffix { get; set; } = "_error";

    public int MaxPeekPerQueue { get; set; } = 25;
}
