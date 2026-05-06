using CodeFlow.Api.Assistant.Artifacts;
using CodeFlow.Api.TokenTracking;
using CodeFlow.Api.TraceBundle;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// AA-9 (sc-800): per-turn factory for the trace-scoped artifact producers
/// (<see cref="DiagnoseTraceTool"/>, <see cref="ExportEvidenceBundleTool"/>). Mirrors
/// <see cref="WorkflowDraftAssistantToolFactory"/> — both tools depend on the conversation's
/// workspace + the artifact recorder to produce a downloadable artifact, so they're built
/// per-turn with a workspace context rather than registered scoped in DI.
/// </summary>
/// <remarks>
/// Diagnose works without a workspace (falls back to result-only mode); evidence-bundle
/// requires a workspace (otherwise refuses with an error). The factory always provides
/// both — the chat panel exposes them through the dispatcher when a workspace is present.
/// </remarks>
public sealed class TraceProducerToolFactory
{
    private readonly CodeFlowDbContext dbContext;
    private readonly ITokenUsageRecordRepository tokenUsageRepository;
    private readonly TraceEvidenceBundleBuilder bundleBuilder;
    private readonly IArtifactRecorder artifactRecorder;

    public TraceProducerToolFactory(
        CodeFlowDbContext dbContext,
        ITokenUsageRecordRepository tokenUsageRepository,
        TraceEvidenceBundleBuilder bundleBuilder,
        IArtifactRecorder artifactRecorder)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(tokenUsageRepository);
        ArgumentNullException.ThrowIfNull(bundleBuilder);
        ArgumentNullException.ThrowIfNull(artifactRecorder);
        this.dbContext = dbContext;
        this.tokenUsageRepository = tokenUsageRepository;
        this.bundleBuilder = bundleBuilder;
        this.artifactRecorder = artifactRecorder;
    }

    public IReadOnlyList<IAssistantTool> Build(ToolWorkspaceContext? conversationWorkspace)
    {
        return new IAssistantTool[]
        {
            new DiagnoseTraceTool(dbContext, tokenUsageRepository, conversationWorkspace, artifactRecorder),
            new ExportEvidenceBundleTool(bundleBuilder, conversationWorkspace, artifactRecorder),
        };
    }
}
