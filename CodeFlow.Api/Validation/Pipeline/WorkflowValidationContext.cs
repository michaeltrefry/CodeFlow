using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Api.Validation.Pipeline;

/// <summary>
/// Shared context handed to every <see cref="IWorkflowValidationRule"/> when the pipeline runs.
/// Mirrors the inputs the existing fail-fast <see cref="WorkflowValidator"/> takes, plus the
/// repository/db handles rules need for cross-entity lookups (e.g. resolving the pinned agent
/// version a workflow node references).
/// </summary>
/// <param name="Key">Workflow key the draft will be saved under. Empty when validating a brand-new
/// draft that has not picked a key yet.</param>
/// <param name="Name">Human-readable workflow name.</param>
/// <param name="MaxRoundsPerRound">Workflow-level cap on agent rounds within a single round.</param>
/// <param name="Nodes">Authored nodes — graph topology + per-node config.</param>
/// <param name="Edges">Authored edges connecting node output ports to downstream node inputs.</param>
/// <param name="Inputs">Declared workflow inputs (the trace caller's contract).</param>
/// <param name="DbContext">Read-only access to the persistence layer for cross-entity lookups.</param>
/// <param name="WorkflowRepository">Resolves subflow / ReviewLoop pinned children.</param>
/// <param name="AgentRepository">Resolves pinned agent versions referenced by nodes.</param>
/// <param name="AgentRoleRepository">Resolves role assignments for agents (V5 role-assignment
/// validation). Roles are per-agent (not per-agent-version), so this lookup is keyed by
/// agent key alone.</param>
public sealed record WorkflowValidationContext(
    string Key,
    string? Name,
    int? MaxRoundsPerRound,
    IReadOnlyList<WorkflowNodeDto> Nodes,
    IReadOnlyList<WorkflowEdgeDto> Edges,
    IReadOnlyList<WorkflowInputDto>? Inputs,
    CodeFlowDbContext DbContext,
    IWorkflowRepository WorkflowRepository,
    IAgentConfigRepository AgentRepository,
    IAgentRoleRepository AgentRoleRepository);
