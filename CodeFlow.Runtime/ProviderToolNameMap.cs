using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeFlow.Runtime;

internal sealed class ProviderToolNameMap
{
    private static readonly Regex InvalidCharacters = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, string> providerToInternal;
    private readonly Dictionary<string, string> internalToProvider;

    private ProviderToolNameMap(
        Dictionary<string, string> providerToInternal,
        Dictionary<string, string> internalToProvider)
    {
        this.providerToInternal = providerToInternal;
        this.internalToProvider = internalToProvider;
    }

    public static ProviderToolNameMap Create(IReadOnlyList<ToolSchema>? tools)
    {
        var providerToInternal = new Dictionary<string, string>(StringComparer.Ordinal);
        var internalToProvider = new Dictionary<string, string>(StringComparer.Ordinal);

        if (tools is null)
        {
            return new ProviderToolNameMap(providerToInternal, internalToProvider);
        }

        foreach (var tool in tools)
        {
            var providerName = BuildProviderName(tool.Name, providerToInternal);
            providerToInternal[providerName] = tool.Name;
            internalToProvider[tool.Name] = providerName;
        }

        return new ProviderToolNameMap(providerToInternal, internalToProvider);
    }

    public string ToProviderName(string internalName)
    {
        return internalToProvider.TryGetValue(internalName, out var providerName)
            ? providerName
            : internalName;
    }

    public string ToInternalName(string providerName)
    {
        return providerToInternal.TryGetValue(providerName, out var internalName)
            ? internalName
            : providerName;
    }

    private static string BuildProviderName(
        string internalName,
        IReadOnlyDictionary<string, string> providerToInternal)
    {
        var candidate = InvalidCharacters.Replace(internalName, "_");
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = "tool";
        }

        if (!providerToInternal.TryGetValue(candidate, out var existing) || string.Equals(existing, internalName, StringComparison.Ordinal))
        {
            return candidate;
        }

        var suffix = ComputeShortHash(internalName);
        var deduped = $"{candidate}_{suffix}";

        while (providerToInternal.TryGetValue(deduped, out existing) && !string.Equals(existing, internalName, StringComparison.Ordinal))
        {
            suffix = ComputeShortHash($"{internalName}:{deduped}");
            deduped = $"{candidate}_{suffix}";
        }

        return deduped;
    }

    private static string ComputeShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes[..4]).ToLowerInvariant();
    }
}
