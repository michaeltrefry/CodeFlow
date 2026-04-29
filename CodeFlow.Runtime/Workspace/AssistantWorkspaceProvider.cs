namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Resolves the per-conversation workspace directory the homepage assistant's host tools operate
/// against when an agent role is assigned. Directory is created lazily on first call so demo
/// users / role-less conversations never touch disk.
/// </summary>
public interface IAssistantWorkspaceProvider
{
    /// <summary>
    /// Returns a <see cref="ToolWorkspaceContext"/> rooted at
    /// <c>{AssistantWorkspaceRoot}/{conversationId:N}</c>. Creates the directory if it does not
    /// exist; the caller may invoke this multiple times per conversation cheaply.
    /// </summary>
    ToolWorkspaceContext GetOrCreateWorkspace(Guid conversationId);
}

public sealed class AssistantWorkspaceProvider : IAssistantWorkspaceProvider
{
    private readonly WorkspaceOptions options;

    public AssistantWorkspaceProvider(WorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    public ToolWorkspaceContext GetOrCreateWorkspace(Guid conversationId)
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
}
