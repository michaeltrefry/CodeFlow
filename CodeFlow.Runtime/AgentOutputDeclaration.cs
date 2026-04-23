using System.Text.Json;

namespace CodeFlow.Runtime;

/// <summary>
/// Declares one decision kind an agent may emit. Surfaced to the workflow editor so it can
/// auto-populate a node's output-port list, and used at invocation time to build the response-
/// format block appended to the system prompt when any <see cref="PayloadExample"/> is set.
/// </summary>
public sealed record AgentOutputDeclaration(
    string Kind,
    string? Description,
    JsonElement? PayloadExample);
