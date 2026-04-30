namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Stable pointer to a stored notification template. Audit records include this pair so a
/// later replay can reproduce exactly which template/version produced a given delivery.
/// Concrete template storage and rendering land in sc-63.
/// </summary>
/// <param name="TemplateId">Logical template id (e.g. <c>hitl-task-pending/email/default</c>).</param>
/// <param name="Version">Monotonic version of the template at render time.</param>
public sealed record NotificationTemplateRef(
    string TemplateId,
    int Version);
