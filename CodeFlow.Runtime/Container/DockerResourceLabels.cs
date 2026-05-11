namespace CodeFlow.Runtime.Container;

public static class DockerResourceLabels
{
    public const string Managed = "codeflow.managed";

    /// <summary>
    /// Per-saga correlation id. Used by <c>DockerLifecycleService</c> to scope cleanup to
    /// the saga that spawned the container. Subflow sagas have distinct correlation ids
    /// from the root trace, so cleanup of an inner saga doesn't kill sibling-saga work.
    /// </summary>
    public const string Workflow = "codeflow.workflow";

    /// <summary>
    /// Root trace id (no-dashes / N form). The on-disk workspace directory is named after
    /// this and is shared across every saga in the trace; host tools that need to resolve
    /// the workspace path (sandbox-controller, future filesystem-scoped tools) read this
    /// label rather than <see cref="Workflow"/>, which would point at a subflow saga that
    /// has no on-disk dir of its own.
    /// </summary>
    public const string Trace = "codeflow.trace";

    public const string CreatedAt = "codeflow.createdAt";

    public const string ResourceKind = "codeflow.resource";

    public const string ManagedValue = "true";

    public const string ContainerKind = "container";

    public const string CacheVolumeKind = "cache-volume";
}
