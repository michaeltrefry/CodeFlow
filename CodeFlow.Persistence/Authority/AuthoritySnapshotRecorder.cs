using System.Text.Json;
using CodeFlow.Runtime.Authority;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Persistence.Authority;

public sealed class AuthoritySnapshotRecorder : IAuthoritySnapshotRecorder
{
    private readonly IAuthorityResolver resolver;
    private readonly CodeFlowDbContext dbContext;
    private readonly IRefusalEventSink? refusalSink;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly ILogger<AuthoritySnapshotRecorder> logger;

    public AuthoritySnapshotRecorder(
        IAuthorityResolver resolver,
        CodeFlowDbContext dbContext,
        IRefusalEventSink? refusalSink,
        ILogger<AuthoritySnapshotRecorder> logger,
        Func<DateTimeOffset>? nowProvider = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(logger);

        this.resolver = resolver;
        this.dbContext = dbContext;
        this.refusalSink = refusalSink;
        this.logger = logger;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<EnvelopeResolutionResult> ResolveAndRecordAsync(
        AuthoritySnapshotInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var resolution = await resolver.ResolveAsync(
            new ResolveAuthorityRequest(
                AgentKey: input.AgentKey,
                TraceId: input.TraceId,
                WorkflowKey: input.WorkflowKey,
                WorkflowVersion: input.WorkflowVersion,
                ContextTier: input.ContextTier),
            cancellationToken);

        var nowUtc = nowProvider().UtcDateTime;

        dbContext.AgentInvocationAuthority.Add(new AgentInvocationAuthorityEntity
        {
            Id = Guid.NewGuid(),
            TraceId = input.TraceId,
            RoundId = input.RoundId,
            AgentKey = input.AgentKey,
            AgentVersion = input.AgentVersion,
            WorkflowKey = input.WorkflowKey,
            WorkflowVersion = input.WorkflowVersion,
            EnvelopeJson = JsonSerializer.Serialize(resolution.Envelope, AuthorityJson.Options),
            BlockedAxesJson = JsonSerializer.Serialize(resolution.BlockedAxes, AuthorityJson.Options),
            TiersJson = JsonSerializer.Serialize(resolution.Tiers, AuthorityJson.Options),
            ResolvedAtUtc = nowUtc
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        if (refusalSink is not null && resolution.BlockedAxes.Count > 0)
        {
            var occurredAt = new DateTimeOffset(nowUtc, TimeSpan.Zero);
            foreach (var blocked in resolution.BlockedAxes)
            {
                await refusalSink.RecordAsync(
                    new RefusalEvent(
                        Id: Guid.NewGuid(),
                        TraceId: input.TraceId,
                        AssistantConversationId: null,
                        Stage: RefusalStages.Admission,
                        Code: blocked.Code,
                        Reason: blocked.Reason,
                        Axis: blocked.Axis,
                        Path: blocked.Tier,
                        DetailJson: JsonSerializer.Serialize(blocked, AuthorityJson.Options),
                        OccurredAt: occurredAt),
                    cancellationToken);
            }
        }

        logger.LogDebug(
            "Recorded authority snapshot for trace {TraceId} round {RoundId} agent {AgentKey} (blocked={Blocked})",
            input.TraceId,
            input.RoundId,
            input.AgentKey,
            resolution.BlockedAxes.Count);

        return resolution;
    }
}
