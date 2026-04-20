using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

internal static class AgentDecisionJson
{
    public static JsonObject ToJsonObject(AgentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var json = new JsonObject
        {
            ["kind"] = decision.Kind.ToString()
        };

        switch (decision)
        {
            case ApprovedWithActionsDecision approvedWithActions:
                json["actions"] = new JsonArray(approvedWithActions.Actions
                    .Select(static action => (JsonNode?)JsonValue.Create(action))
                    .ToArray());
                break;

            case RejectedDecision rejected:
                json["reasons"] = new JsonArray(rejected.Reasons
                    .Select(static reason => (JsonNode?)JsonValue.Create(reason))
                    .ToArray());
                break;

            case FailedDecision failed:
                json["reason"] = failed.Reason;
                break;
        }

        if (decision.DecisionPayload is not null)
        {
            json["payload"] = decision.DecisionPayload.DeepClone();
        }

        return json;
    }
}
