using System.Diagnostics;

namespace CodeFlow.Runtime.Observability;

public static class CodeFlowActivity
{
    public const string SourceName = "CodeFlow";
    public const string SourceVersion = "1.0.0";

    public static readonly ActivitySource Source = new(SourceName, SourceVersion);

    public static class TagNames
    {
        public const string TraceId = "codeflow.trace_id";
        public const string RoundId = "codeflow.round_id";
        public const string WorkflowKey = "codeflow.workflow.key";
        public const string WorkflowVersion = "codeflow.workflow.version";
        public const string AgentKey = "codeflow.agent.key";
        public const string AgentVersion = "codeflow.agent.version";
        public const string AgentProvider = "codeflow.agent.provider";
        public const string AgentModel = "codeflow.agent.model";
        public const string ToolName = "codeflow.tool.name";
        public const string DecisionKind = "codeflow.decision.kind";
        public const string FailureReason = "codeflow.failure.reason";
        public const string RetryAttempt = "codeflow.retry.attempt";
        public const string SagaState = "codeflow.saga.state";
    }

    /// <summary>
    /// Starts an activity rooted at the workflow trace ID so every downstream span
    /// (agent runtime, tool calls, bus publishes) shares the same W3C trace id.
    /// </summary>
    public static Activity? StartWorkflowRoot(string name, Guid workflowTraceId, ActivityKind kind = ActivityKind.Internal)
    {
        var traceId = ToOtelTraceId(workflowTraceId);
        var parent = new ActivityContext(
            traceId,
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded,
            isRemote: true);

        var activity = Source.StartActivity(name, kind, parent);
        activity?.SetTag(TagNames.TraceId, workflowTraceId);
        return activity;
    }

    public static Activity? StartChild(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return Source.StartActivity(name, kind);
    }

    public static ActivityTraceId ToOtelTraceId(Guid workflowTraceId)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (!workflowTraceId.TryWriteBytes(buffer))
        {
            throw new InvalidOperationException("Failed to convert workflow trace id to bytes.");
        }

        return ActivityTraceId.CreateFromBytes(buffer);
    }
}
