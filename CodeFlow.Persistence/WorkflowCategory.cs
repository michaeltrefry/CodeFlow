namespace CodeFlow.Persistence;

/// <summary>
/// Top-level classification of a workflow row. Workflow list groupings and
/// filters key off this value; it is persisted as a string column.
/// </summary>
public enum WorkflowCategory
{
    Workflow = 0,
    Subflow = 1,
    Loop = 2
}
