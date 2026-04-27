namespace CodeFlow.Persistence;

/// <summary>
/// A reusable bundle of mock LLM responses for dry-running a workflow without burning real
/// tokens (T1). Fixtures are keyed by workflow key (not version) so the same fixture can be
/// reused across versions of the same workflow as long as the agent set and decision shape
/// haven't changed materially.
///
/// Mock responses are stored as a single JSON document — see <see cref="MockResponsesJson"/>
/// — to keep schema changes additive without per-agent or per-key column churn.
/// </summary>
public sealed class WorkflowFixtureEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Workflow key this fixture is associated with. A fixture may target a particular workflow
    /// or be designed to flow through a subflow chain — either way it pins to the entry workflow
    /// the author chooses to dry-run.
    /// </summary>
    public string WorkflowKey { get; set; } = null!;

    /// <summary>
    /// Author-supplied stable identifier for the fixture (e.g. <c>happy-path</c>,
    /// <c>reviewer-rejects-once</c>). Unique within a workflow key.
    /// </summary>
    public string FixtureKey { get; set; } = null!;

    /// <summary>
    /// Human-readable name shown in UIs.
    /// </summary>
    public string DisplayName { get; set; } = null!;

    /// <summary>
    /// Optional starting input artifact text for the dry-run. When null, the dry-run uses
    /// whatever the request body provides.
    /// </summary>
    public string? StartingInput { get; set; }

    /// <summary>
    /// JSON object keyed by agent key. Each value is an ordered array of mock responses, each
    /// shaped as <c>{ "decision": "Approved", "payload": {...optional...}, "output": "..." }</c>.
    /// The dry-run executor consumes responses in order; if an agent runs out of mocks the
    /// run fails with a clear error.
    /// </summary>
    public string MockResponsesJson { get; set; } = "{}";

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
