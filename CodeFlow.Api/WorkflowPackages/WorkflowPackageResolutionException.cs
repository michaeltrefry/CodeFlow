namespace CodeFlow.Api.WorkflowPackages;

public sealed class WorkflowPackageResolutionException : Exception
{
    public WorkflowPackageResolutionException(string message)
        : base(message)
    {
        MissingReferences = Array.Empty<MissingPackageReference>();
    }

    public WorkflowPackageResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
        MissingReferences = Array.Empty<MissingPackageReference>();
    }

    public WorkflowPackageResolutionException(
        string message,
        IReadOnlyList<MissingPackageReference> missingReferences)
        : base(message)
    {
        MissingReferences = missingReferences ?? Array.Empty<MissingPackageReference>();
    }

    /// <summary>
    /// V8: every entity the resolver could not load while walking the workflow's dependency
    /// graph. Empty for non-self-containment failures (parse errors, config nulls, etc.).
    /// </summary>
    public IReadOnlyList<MissingPackageReference> MissingReferences { get; }
}

/// <summary>
/// One unresolved reference encountered during workflow package resolution. Versioned for
/// agents and workflows; unversioned for roles, skills, and MCP servers (those are unversioned
/// in storage).
/// </summary>
public sealed record MissingPackageReference(
    PackageReferenceKind Kind,
    string Key,
    int? Version,
    string ReferencedBy);

public enum PackageReferenceKind
{
    Workflow,
    Agent,
    Role,
    Skill,
    McpServer,
}
