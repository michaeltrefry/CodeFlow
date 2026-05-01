namespace CodeFlow.Api.WorkflowPackages;

public sealed class WorkflowPackageResolutionException : Exception
{
    public WorkflowPackageResolutionException(string message)
        : base(message)
    {
        MissingReferences = Array.Empty<MissingPackageReference>();
        ValidationErrors = Array.Empty<WorkflowPackageValidationError>();
    }

    public WorkflowPackageResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
        MissingReferences = Array.Empty<MissingPackageReference>();
        ValidationErrors = Array.Empty<WorkflowPackageValidationError>();
    }

    public WorkflowPackageResolutionException(
        string message,
        IReadOnlyList<MissingPackageReference> missingReferences)
        : base(message)
    {
        MissingReferences = missingReferences ?? Array.Empty<MissingPackageReference>();
        ValidationErrors = Array.Empty<WorkflowPackageValidationError>();
    }

    public WorkflowPackageResolutionException(
        string message,
        IReadOnlyList<WorkflowPackageValidationError> validationErrors)
        : base(message)
    {
        MissingReferences = Array.Empty<MissingPackageReference>();
        ValidationErrors = validationErrors ?? Array.Empty<WorkflowPackageValidationError>();
    }

    /// <summary>
    /// V8: every entity the resolver could not load while walking the workflow's dependency
    /// graph. Empty for non-self-containment failures (parse errors, config nulls, etc.).
    /// </summary>
    public IReadOnlyList<MissingPackageReference> MissingReferences { get; }

    /// <summary>
    /// Per-workflow authoring validation errors collected while importing a package. Empty for
    /// structural package resolution failures.
    /// </summary>
    public IReadOnlyList<WorkflowPackageValidationError> ValidationErrors { get; }
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
