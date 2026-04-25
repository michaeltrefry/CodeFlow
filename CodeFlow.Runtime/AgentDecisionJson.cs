using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

internal static class AgentDecisionJson
{
    public static JsonObject ToJsonObject(AgentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var json = new JsonObject
        {
            ["portName"] = decision.PortName
        };

        if (decision.Payload is not null)
        {
            json["payload"] = decision.Payload.DeepClone();
        }

        return json;
    }
}
