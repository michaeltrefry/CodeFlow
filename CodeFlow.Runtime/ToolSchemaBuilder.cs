using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

/// <summary>
/// Builds the tool schemas the runtime exposes to the model on every invocation: the static
/// <c>fail</c> / <c>setContext</c> / <c>setWorkflow</c> schemas plus the per-call <c>submit</c>
/// schema whose <c>decision</c> enum tracks the agent's declared output ports.
///
/// <para>
/// Carved out of <c>InvocationLoop</c> (sc-177) so schema construction is testable in
/// isolation and adding a new runtime tool doesn't require touching the loop.
/// </para>
/// </summary>
internal static class ToolSchemaBuilder
{
    public static readonly ToolSchema FailTool = new(
        InvocationLoop.FailToolName,
        "Fail the current agent invocation with a reason.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reason"] = new JsonObject
                {
                    ["type"] = "string"
                }
            },
            ["required"] = new JsonArray("reason")
        });

    public static readonly ToolSchema SetContextTool = new(
        InvocationLoop.SetContextToolName,
        "Persist a value into the workflow context bag under the given key. Visible to "
        + "downstream agents as `{{ context.<key> }}` in templates and `context.<key>` in "
        + "Logic-node scripts. Updates accumulate during this invocation and are committed "
        + "atomically once `submit` completes; if the agent fails, pending writes are discarded.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "string" },
                ["value"] = new JsonObject()
            },
            ["required"] = new JsonArray("key", "value")
        });

    public static readonly ToolSchema SetWorkflowTool = new(
        InvocationLoop.SetWorkflowToolName,
        "Persist a value into the per-trace-tree workflow bag under the given key. Visible to "
        + "downstream agents and subflow children as `{{ workflow.<key> }}` in templates and "
        + "`workflow.<key>` in scripts. Same lifecycle rules as `setContext` — committed on "
        + "successful `submit`, discarded on failure.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "string" },
                ["value"] = new JsonObject()
            },
            ["required"] = new JsonArray("key", "value")
        });

    public static ToolSchema BuildSubmitTool(IReadOnlyList<string> declaredPortNames)
    {
        ArgumentNullException.ThrowIfNull(declaredPortNames);

        var properties = new JsonObject
        {
            ["payload"] = new JsonObject()
        };

        var decisionSchema = new JsonObject
        {
            ["type"] = "string"
        };

        if (declaredPortNames.Count > 0)
        {
            var enumArray = new JsonArray();
            foreach (var name in declaredPortNames)
            {
                enumArray.Add(name);
            }
            decisionSchema["enum"] = enumArray;
            decisionSchema["description"] =
                "The output port to route this invocation to. Must match one of the agent's "
                + "declared output port names.";
        }
        else
        {
            decisionSchema["description"] =
                "The output port to route this invocation to. The agent has no declared outputs; "
                + $"omit this field to default to '{InvocationLoop.DefaultPortName}'.";
        }

        properties["decision"] = decisionSchema;

        var required = declaredPortNames.Count > 0
            ? new JsonArray("decision")
            : new JsonArray();

        return new ToolSchema(
            InvocationLoop.SubmitToolName,
            "Submit the final decision for this agent invocation.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required
            });
    }
}
