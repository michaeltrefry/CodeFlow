using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Authority.Admission;

/// <summary>
/// Structured evidence emitted by an admission validator when it refuses to mint an
/// admitted handoff. Mirrors the shape of <see cref="RefusalEvent"/> deliberately so a
/// rejection can be persisted as one — admission rejections that flow through tool calls
/// ride the existing <see cref="ToolRegistry"/> refusal path (Stage = Tool) via a
/// <c>refusal</c> JSON payload; rejections that fire outside tool calls (API endpoints,
/// orchestrator handoffs) emit Stage = Handoff <see cref="RefusalEvent"/>s directly via
/// <see cref="IRefusalEventSink"/>.
/// </summary>
/// <param name="Code">Stable, machine-readable rejection code (e.g. <c>patch-malformed</c>).</param>
/// <param name="Reason">Human-readable explanation; safe to surface to operators and LLMs.</param>
/// <param name="Axis">
/// Finer-grained axis the rejection applied to — e.g. <c>workspace-mutation</c>,
/// <c>delivery</c>, <c>replay</c>. Reused from the existing tool refusal taxonomy when
/// applicable so governance queries can slice without parsing the code.
/// </param>
/// <param name="Path">Optional path or identifier the rejection targeted.</param>
/// <param name="Detail">Optional small JSON detail blob — keep it bounded; this is for evidence, not full request bodies.</param>
public sealed record Rejection(
    string Code,
    string Reason,
    string Axis,
    string? Path = null,
    JsonObject? Detail = null);

/// <summary>
/// Canonical rejection codes shared across admission validators. New codes are introduced
/// as new boundary types land; reuse existing codes when the meaning is identical.
/// </summary>
public static class RejectionCodes
{
    /// <summary>The raw request payload could not be parsed into a structured shape.</summary>
    public const string Malformed = "handoff-malformed";

    /// <summary>The request asks the boundary to do something the active envelope did not authorise.</summary>
    public const string EnvelopeBlocked = "handoff-envelope-blocked";

    /// <summary>A required pre-condition (preimage hash, parent trace lookup, …) was not satisfied.</summary>
    public const string PreconditionFailed = "handoff-precondition-failed";

    /// <summary>The request contains a value that violates a structural invariant (path escape, missing dependency, etc.).</summary>
    public const string InvariantViolated = "handoff-invariant-violated";
}
