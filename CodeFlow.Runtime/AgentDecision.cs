using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

/// <summary>
/// Flat representation of a terminal agent decision. <see cref="PortName"/> is the
/// author-defined output port name that drives saga routing. <see cref="Payload"/>
/// is the LLM-supplied payload (or runtime-supplied failure context for the implicit
/// <c>Failed</c> port).
/// </summary>
public sealed record AgentDecision(string PortName, JsonNode? Payload = null);
