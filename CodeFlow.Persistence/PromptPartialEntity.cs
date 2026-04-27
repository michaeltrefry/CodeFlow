namespace CodeFlow.Persistence;

/// <summary>
/// A versioned, immutable Scriban partial. Agents pin a specific (Key, Version) pair just like
/// they pin agent versions; the renderer resolves <c>{{ include "@scope/key" }}</c> against the
/// pinned set at invocation time.
///
/// Stock partials ship under the <c>@codeflow/...</c> scope (e.g. <c>@codeflow/reviewer-base</c>).
/// Author-defined partials may use bare keys or any other scope. Storage is the same.
/// </summary>
public sealed class PromptPartialEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Scoped, dot-or-slash-delimited key. Examples: <c>@codeflow/reviewer-base</c>,
    /// <c>my-team/triage-rubric</c>, <c>house-style</c>.
    /// </summary>
    public string Key { get; set; } = null!;

    public int Version { get; set; }

    /// <summary>
    /// Scriban template body. Rendered as a partial via <c>{{ include "key" }}</c>; can reference
    /// the same script scope as the parent template (variables, functions, etc).
    /// </summary>
    public string Body { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    /// <summary>
    /// True for stock partials shipped by the platform (e.g. <c>@codeflow/...</c>). System
    /// partials cannot be deleted via the API; new versions can still be added.
    /// </summary>
    public bool IsSystemManaged { get; set; }
}
