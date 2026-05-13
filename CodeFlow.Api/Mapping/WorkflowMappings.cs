using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;

namespace CodeFlow.Api.Mapping;

internal static class WorkflowMappings
{
    public static WorkflowSummaryDto ToSummaryDto(this Workflow workflow) => new(
        Key: workflow.Key,
        LatestVersion: workflow.Version,
        Name: workflow.Name,
        Category: workflow.Category,
        Tags: workflow.TagsOrEmpty,
        NodeCount: workflow.Nodes.Count,
        EdgeCount: workflow.Edges.Count,
        InputCount: workflow.Inputs.Count,
        CreatedAtUtc: workflow.CreatedAtUtc,
        IsRetired: workflow.IsRetired);

    public static WorkflowDetailDto ToDetailDto(this Workflow workflow) => new(
        Key: workflow.Key,
        Version: workflow.Version,
        Name: workflow.Name,
        MaxStepsPerSaga: workflow.MaxStepsPerSaga,
        Category: workflow.Category,
        Tags: workflow.TagsOrEmpty,
        CreatedAtUtc: workflow.CreatedAtUtc,
        IsRetired: workflow.IsRetired,
        Nodes: workflow.Nodes
            .Select(node => new WorkflowNodeDto(
                Id: node.Id,
                Kind: node.Kind,
                AgentKey: node.AgentKey,
                AgentVersion: node.AgentVersion,
                OutputScript: node.OutputScript,
                OutputPorts: node.OutputPorts,
                LayoutX: node.LayoutX,
                LayoutY: node.LayoutY,
                SubflowKey: node.SubflowKey,
                SubflowVersion: node.SubflowVersion,
                ReviewMaxRounds: node.ReviewMaxRounds,
                LoopDecision: node.LoopDecision,
                InputScript: node.InputScript,
                OptOutLastRoundReminder: node.OptOutLastRoundReminder,
                RejectionHistory: node.RejectionHistory,
                MirrorOutputToWorkflowVar: node.MirrorOutputToWorkflowVar,
                OutputPortReplacements: node.OutputPortReplacements,
                Template: node.Template,
                OutputType: node.OutputType,
                SwarmProtocol: node.SwarmProtocol,
                SwarmN: node.SwarmN,
                ContributorAgentKey: node.ContributorAgentKey,
                ContributorAgentVersion: node.ContributorAgentVersion,
                SynthesizerAgentKey: node.SynthesizerAgentKey,
                SynthesizerAgentVersion: node.SynthesizerAgentVersion,
                CoordinatorAgentKey: node.CoordinatorAgentKey,
                CoordinatorAgentVersion: node.CoordinatorAgentVersion,
                SwarmTokenBudget: node.SwarmTokenBudget,
                CollectionExpression: node.CollectionExpression,
                ItemVar: node.ItemVar,
                GoalObjective: node.GoalObjective,
                GoalTokenBudget: node.GoalTokenBudget,
                GoalMaxIterations: node.GoalMaxIterations))
            .ToArray(),
        Edges: workflow.Edges
            .Select(edge => new WorkflowEdgeDto(
                FromNodeId: edge.FromNodeId,
                FromPort: edge.FromPort,
                ToNodeId: edge.ToNodeId,
                ToPort: edge.ToPort,
                RotatesRound: edge.RotatesRound,
                SortOrder: edge.SortOrder,
                IntentionalBackedge: edge.IntentionalBackedge))
            .ToArray(),
        Inputs: workflow.Inputs
            .Select(input => new WorkflowInputDto(
                Key: input.Key,
                DisplayName: input.DisplayName,
                Kind: input.Kind,
                Required: input.Required,
                DefaultValueJson: input.DefaultValueJson,
                Description: input.Description,
                Ordinal: input.Ordinal))
            .ToArray(),
        WorkflowVarsReads: workflow.WorkflowVarsReads,
        WorkflowVarsWrites: workflow.WorkflowVarsWrites);
}
