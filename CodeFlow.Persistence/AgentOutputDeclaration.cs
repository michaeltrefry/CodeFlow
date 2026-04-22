using System.Text.Json;

namespace CodeFlow.Persistence;

/// <summary>
/// Declares one decision kind an agent may emit, surfaced to the workflow editor so it can
/// auto-populate a node's output-port list. `Kind` is a free-form string — it becomes the
/// port name on the workflow edge.
/// </summary>
public sealed record AgentOutputDeclaration(
    string Kind,
    string? Description,
    JsonElement? PayloadExample);
