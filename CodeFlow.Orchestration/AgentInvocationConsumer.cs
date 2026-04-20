using CodeFlow.Contracts;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Orchestration;

public sealed class AgentInvocationConsumer : IConsumer<AgentInvokeRequested>
{
    private const int HitlInputPreviewLength = 2048;

    private readonly IAgentConfigRepository agentConfigRepository;
    private readonly IArtifactStore artifactStore;
    private readonly IAgentInvoker agentInvoker;
    private readonly CodeFlowDbContext dbContext;

    public AgentInvocationConsumer(
        IAgentConfigRepository agentConfigRepository,
        IArtifactStore artifactStore,
        IAgentInvoker agentInvoker,
        CodeFlowDbContext dbContext)
    {
        this.agentConfigRepository = agentConfigRepository ?? throw new ArgumentNullException(nameof(agentConfigRepository));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.agentInvoker = agentInvoker ?? throw new ArgumentNullException(nameof(agentInvoker));
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task Consume(ConsumeContext<AgentInvokeRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        var startedAt = DateTimeOffset.UtcNow;
        var agentConfig = await agentConfigRepository.GetAsync(
            message.AgentKey,
            message.AgentVersion,
            context.CancellationToken);

        await using var inputStream = await artifactStore.ReadAsync(
            message.InputRef,
            context.CancellationToken);
        var input = await ReadInputAsync(inputStream, context.CancellationToken);

        if (agentConfig.Kind == AgentKind.Hitl)
        {
            await CreateHitlTaskAsync(message, input, context.CancellationToken);
            return;
        }

        var invocationResult = await agentInvoker.InvokeAsync(
            agentConfig.Configuration,
            input,
            context.CancellationToken);

        await using var outputStream = new MemoryStream(Encoding.UTF8.GetBytes(invocationResult.Output));
        var outputRef = await artifactStore.WriteAsync(
            outputStream,
            new ArtifactMetadata(
                message.TraceId,
                message.RoundId,
                Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: $"{message.AgentKey}-output.txt"),
            context.CancellationToken);

        await context.Publish(
            new AgentInvocationCompleted(
                message.TraceId,
                message.RoundId,
                message.AgentKey,
                message.AgentVersion,
                outputRef,
                MapDecisionKind(invocationResult.Decision.Kind),
                BuildDecisionPayload(invocationResult.Decision),
                DateTimeOffset.UtcNow - startedAt,
                MapTokenUsage(invocationResult.TokenUsage)),
            context.CancellationToken);
    }

    private async Task CreateHitlTaskAsync(
        AgentInvokeRequested message,
        string? input,
        CancellationToken cancellationToken)
    {
        var preview = input is null
            ? null
            : input.Length > HitlInputPreviewLength
                ? input[..HitlInputPreviewLength]
                : input;

        var existing = await dbContext.HitlTasks
            .FirstOrDefaultAsync(
                task => task.TraceId == message.TraceId
                    && task.RoundId == message.RoundId
                    && task.AgentKey == message.AgentKey,
                cancellationToken);

        if (existing is not null)
        {
            return;
        }

        var entity = new HitlTaskEntity
        {
            TraceId = message.TraceId,
            RoundId = message.RoundId,
            AgentKey = message.AgentKey,
            AgentVersion = message.AgentVersion,
            WorkflowKey = message.WorkflowKey,
            WorkflowVersion = message.WorkflowVersion,
            InputRef = message.InputRef.ToString(),
            InputPreview = preview,
            State = HitlTaskState.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        dbContext.HitlTasks.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<string?> ReadInputAsync(
        Stream inputStream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);

        return string.IsNullOrEmpty(content) ? null : content;
    }

    private static CodeFlow.Contracts.AgentDecisionKind MapDecisionKind(Runtime.AgentDecisionKind decisionKind)
    {
        return decisionKind switch
        {
            Runtime.AgentDecisionKind.Completed => CodeFlow.Contracts.AgentDecisionKind.Completed,
            Runtime.AgentDecisionKind.Approved => CodeFlow.Contracts.AgentDecisionKind.Approved,
            Runtime.AgentDecisionKind.ApprovedWithActions => CodeFlow.Contracts.AgentDecisionKind.ApprovedWithActions,
            Runtime.AgentDecisionKind.Rejected => CodeFlow.Contracts.AgentDecisionKind.Rejected,
            Runtime.AgentDecisionKind.Failed => CodeFlow.Contracts.AgentDecisionKind.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(decisionKind), decisionKind, "Unsupported decision kind.")
        };
    }

    private static CodeFlow.Contracts.TokenUsage MapTokenUsage(Runtime.TokenUsage? tokenUsage)
    {
        return tokenUsage is null
            ? new CodeFlow.Contracts.TokenUsage(0, 0, 0)
            : new CodeFlow.Contracts.TokenUsage(tokenUsage.InputTokens, tokenUsage.OutputTokens, tokenUsage.TotalTokens);
    }

    private static JsonElement BuildDecisionPayload(AgentDecision decision)
    {
        var json = new JsonObject
        {
            ["kind"] = decision.Kind.ToString()
        };

        switch (decision)
        {
            case ApprovedWithActionsDecision approvedWithActions:
                json["actions"] = new JsonArray(approvedWithActions.Actions
                    .Select(static action => (JsonNode?)JsonValue.Create(action))
                    .ToArray());
                break;

            case RejectedDecision rejected:
                json["reasons"] = new JsonArray(rejected.Reasons
                    .Select(static reason => (JsonNode?)JsonValue.Create(reason))
                    .ToArray());
                break;

            case FailedDecision failed:
                json["reason"] = failed.Reason;
                break;
        }

        if (decision.DecisionPayload is not null)
        {
            json["payload"] = decision.DecisionPayload.DeepClone();
        }

        using var document = JsonDocument.Parse(json.ToJsonString());
        return document.RootElement.Clone();
    }
}
