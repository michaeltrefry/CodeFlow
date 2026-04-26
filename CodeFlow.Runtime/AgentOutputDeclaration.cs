using System.Text.Json;

namespace CodeFlow.Runtime;

/// <summary>
/// Declares one decision kind an agent may emit. Surfaced to the workflow editor so it can
/// auto-populate a node's output-port list, and used at invocation time to build the response-
/// format block appended to the system prompt when any <see cref="PayloadExample"/> is set.
/// </summary>
/// <param name="ContentOptional">
/// When true, the runtime accepts an empty assistant message body when the agent submits to
/// this port. Default false: the loop rejects empty submits and reminds the agent to write
/// content first (see <see cref="InvocationLoop"/>). Set to true for sentinel ports like
/// <c>Cancelled</c> or <c>Skip</c> whose decision carries the meaning and whose downstream
/// consumers do not read the artifact body. The implicit <c>Failed</c> port is always
/// content-optional regardless of this flag — failure reasons live in the decision payload.
/// </param>
public sealed record AgentOutputDeclaration(
    string Kind,
    string? Description,
    JsonElement? PayloadExample,
    bool ContentOptional = false);
