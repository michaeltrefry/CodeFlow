using System.Net;

namespace CodeFlow.Runtime.LMStudio;

public static class LMStudioEndpoint
{
    public const string DefaultContainerHost = "host.docker.internal";

    public static Uri NormalizeForCurrentProcess(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!IsRunningInContainer() || !IsLoopbackHost(endpoint.Host))
        {
            return endpoint;
        }

        var containerHost = Environment.GetEnvironmentVariable("LMStudio__ContainerHost");
        if (string.IsNullOrWhiteSpace(containerHost))
        {
            containerHost = DefaultContainerHost;
        }

        return new UriBuilder(endpoint)
        {
            Host = containerHost.Trim()
        }.Uri;
    }

    private static bool IsRunningInContainer() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
