using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Persistence.Authority;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for serializing authority-snapshot fields.
/// Web defaults (camelCase property names) match the rest of CodeFlow's JSON surfaces; nulls
/// are dropped so persisted envelopes only carry tiers that had an opinion. Enums round-trip
/// as their string names so a future schema change to <see cref="CodeFlow.Runtime.Authority.NetworkPolicy"/>
/// or similar doesn't silently break older rows.
/// </summary>
public static class AuthorityJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
