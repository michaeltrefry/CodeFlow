using CodeFlow.Api.WorkflowTemplates;

namespace CodeFlow.Api.Dtos;

public sealed record WorkflowTemplateSummaryDto(
    string Id,
    string Name,
    string Description,
    WorkflowTemplateCategory Category);

public sealed record MaterializeWorkflowTemplateRequest(
    string? NamePrefix);

public sealed record MaterializeWorkflowTemplateResponse(
    string EntryWorkflowKey,
    int EntryWorkflowVersion,
    IReadOnlyList<MaterializedEntityDto> CreatedEntities);

public sealed record MaterializedEntityDto(
    MaterializedEntityKind Kind,
    string Key,
    int Version);
