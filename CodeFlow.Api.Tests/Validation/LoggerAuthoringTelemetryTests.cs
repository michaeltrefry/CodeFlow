using CodeFlow.Api.Validation.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// O1 — confirms <see cref="LoggerAuthoringTelemetry"/> writes one structured log entry per
/// event with the documented event name and stable property keys. Substituting recording
/// providers in tests is the canonical way to assert structured logging behavior.
/// </summary>
public sealed class LoggerAuthoringTelemetryTests
{
    [Fact]
    public void ValidatorFired_EmitsEventWithFindingShape()
    {
        var (telemetry, sink) = Build();
        var nodeId = Guid.NewGuid();
        var finding = new WorkflowValidationFinding(
            RuleId: "port-coupling",
            Severity: WorkflowValidationSeverity.Warning,
            Message: "wired but undeclared",
            Location: new WorkflowValidationLocation(NodeId: nodeId));

        telemetry.ValidatorFired("my-flow", finding);

        var entry = sink.Records.Should().ContainSingle().Subject;
        entry.Message.Should().StartWith("workflow.validator.fired");
        entry.Properties.Should().Contain("WorkflowKey", "my-flow");
        entry.Properties.Should().Contain("RuleId", "port-coupling");
        entry.Properties.Should().Contain("Severity", WorkflowValidationSeverity.Warning);
        entry.Properties.Should().Contain("NodeId", nodeId);
    }

    [Fact]
    public void ValidatorBlockedSave_EmitsAggregateEventWithRuleIds()
    {
        var (telemetry, sink) = Build();

        telemetry.ValidatorBlockedSave("my-flow", new[] { "port-coupling", "role-assignment" });

        var entry = sink.Records.Should().ContainSingle().Subject;
        entry.Message.Should().StartWith("workflow.validator.blocked_save");
        entry.Properties.Should().Contain("WorkflowKey", "my-flow");
        // Stable comma-joined ids — log aggregators can split if needed.
        entry.Properties.Should().Contain("ErrorRuleIds", "port-coupling,role-assignment");
    }

    [Fact]
    public void FeatureUsed_EmitsEventWithFeatureIdAndInstanceCount()
    {
        var (telemetry, sink) = Build();

        telemetry.FeatureUsed("my-flow", "mirror-to-workflow-var", instances: 3);

        var entry = sink.Records.Should().ContainSingle().Subject;
        entry.Message.Should().StartWith("workflow.feature.used");
        entry.Properties.Should().Contain("WorkflowKey", "my-flow");
        entry.Properties.Should().Contain("FeatureId", "mirror-to-workflow-var");
        entry.Properties.Should().Contain("Instances", 3);
    }

    [Fact]
    public void FeatureUsed_RejectsBlankFeatureId()
    {
        var (telemetry, _) = Build();

        var act = () => telemetry.FeatureUsed("my-flow", " ");

        act.Should().Throw<ArgumentException>();
    }

    private static (LoggerAuthoringTelemetry Telemetry, RecordingLoggerSink Sink) Build()
    {
        var sink = new RecordingLoggerSink();
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RecordingLoggerProvider(sink));
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        return (new LoggerAuthoringTelemetry(loggerFactory.CreateLogger<LoggerAuthoringTelemetry>()), sink);
    }

    private sealed record RecordingEntry(string Message, IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLoggerSink
    {
        public List<RecordingEntry> Records { get; } = new();
    }

    private sealed class RecordingLoggerProvider(RecordingLoggerSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(sink);
        public void Dispose() { }
    }

    private sealed class RecordingLogger(RecordingLoggerSink sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var kvp in structured)
                {
                    properties[kvp.Key] = kvp.Value;
                }
            }
            sink.Records.Add(new RecordingEntry(formatter(state, exception), properties));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
