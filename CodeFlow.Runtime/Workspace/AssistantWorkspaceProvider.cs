namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Resolves the workspace directory the homepage assistant's host tools operate against when
/// an agent role is assigned. Two flavours: the conversation's own dir (created lazily, isolated
/// per chat) or a code-aware trace's dir (existing path on the shared workdir volume — read-only
/// from the assistant's perspective in that the assistant did not create it).
/// </summary>
public interface IAssistantWorkspaceProvider
{
    /// <summary>
    /// Returns a <see cref="ToolWorkspaceContext"/> rooted at
    /// <c>{AssistantWorkspaceRoot}/{conversationId:N}</c>. Creates the directory if it does not
    /// exist; the caller may invoke this multiple times per conversation cheaply.
    /// </summary>
    ToolWorkspaceContext GetOrCreateConversationWorkspace(Guid conversationId);

    /// <summary>
    /// Returns a <see cref="ToolWorkspaceContext"/> rooted at
    /// <c>{WorkingDirectoryRoot}/{traceId:N}</c> — the code-aware-workflow per-trace workdir.
    /// Throws <see cref="DirectoryNotFoundException"/> if the dir does not exist (the trace had
    /// no code-aware step, or the workdir was swept). Never creates: the assistant treats the
    /// trace workspace as observation-only data, not its own.
    /// </summary>
    ToolWorkspaceContext GetTraceWorkspace(Guid traceId);
}

public sealed class AssistantWorkspaceProvider : IAssistantWorkspaceProvider
{
    private readonly WorkspaceOptions options;

    public AssistantWorkspaceProvider(WorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    public ToolWorkspaceContext GetOrCreateConversationWorkspace(Guid conversationId)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation id is required.", nameof(conversationId));
        }

        var root = string.IsNullOrWhiteSpace(options.AssistantWorkspaceRoot)
            ? WorkspaceOptions.DefaultAssistantWorkspaceRoot
            : options.AssistantWorkspaceRoot;
        var path = Path.Combine(root, conversationId.ToString("N"));
        Directory.CreateDirectory(path);
        return new ToolWorkspaceContext(conversationId, path);
    }

    public ToolWorkspaceContext GetTraceWorkspace(Guid traceId)
    {
        if (traceId == Guid.Empty)
        {
            throw new ArgumentException("Trace id is required.", nameof(traceId));
        }

        var root = string.IsNullOrWhiteSpace(options.WorkingDirectoryRoot)
            ? WorkspaceOptions.DefaultWorkingDirectoryRoot
            : options.WorkingDirectoryRoot;
        var path = Path.Combine(root, traceId.ToString("N"));
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"Trace {traceId:N} has no workspace at '{path}'. The trace either had no code-aware step or its workdir has been swept.");
        }
        return new ToolWorkspaceContext(traceId, path);
    }
}
