namespace CodeFlow.Runtime.Container;

/// <summary>
/// Configuration for the out-of-process sandbox controller backend (sc-532). Bound from the
/// <c>SandboxController</c> configuration section. Required when
/// <see cref="ContainerToolOptions.Backend"/> is <see cref="ContainerBackend.SandboxController"/>.
/// </summary>
public sealed class SandboxControllerOptions
{
    public const string SectionName = "SandboxController";

    /// <summary>
    /// Base URL of the controller's mTLS endpoint, e.g.
    /// <c>https://codeflow-sandbox-controller:8443</c>. Path components (e.g. <c>/run</c>)
    /// are appended by the runner.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>PEM-encoded client cert used for mTLS to the controller.</summary>
    public string ClientCertPath { get; set; } = string.Empty;

    /// <summary>PEM-encoded private key paired with <see cref="ClientCertPath"/>.</summary>
    public string ClientKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded CA bundle the controller's server cert is signed by. The runner verifies
    /// the chain against this bundle only — never the OS trust store.
    /// </summary>
    public string ServerCAPath { get; set; } = string.Empty;

    /// <summary>
    /// Expected Common Name on the controller's server cert. The runner pins this in addition
    /// to the CA chain check; a swapped server cert with a different CN is rejected.
    /// </summary>
    public string ServerCommonName { get; set; } = "codeflow-sandbox-controller";

    /// <summary>
    /// Per-request HTTP timeout. Should be larger than the largest per-job timeout the
    /// controller will accept; a smaller value causes legitimate long jobs to be cancelled
    /// from the C# side.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30 * 60;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Url))
        {
            errors.Add("SandboxController:Url must be set when Backend is SandboxController.");
        }
        else if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("SandboxController:Url must be an absolute https URL.");
        }
        if (string.IsNullOrWhiteSpace(ClientCertPath)) errors.Add("SandboxController:ClientCertPath must be set.");
        if (string.IsNullOrWhiteSpace(ClientKeyPath)) errors.Add("SandboxController:ClientKeyPath must be set.");
        if (string.IsNullOrWhiteSpace(ServerCAPath)) errors.Add("SandboxController:ServerCAPath must be set.");
        if (string.IsNullOrWhiteSpace(ServerCommonName)) errors.Add("SandboxController:ServerCommonName must be set.");
        if (RequestTimeoutSeconds <= 0) errors.Add("SandboxController:RequestTimeoutSeconds must be greater than zero.");
        return errors;
    }
}
