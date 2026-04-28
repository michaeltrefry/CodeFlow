using CodeFlow.Persistence;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Resolves which tools the assistant may advertise + invoke for a given conversation. Per-turn
/// gate so demo-mode (anonymous) homepage conversations get system-prompt knowledge only and
/// authenticated users get the full registry.
/// </summary>
public interface IAssistantToolPolicy
{
    IReadOnlyList<IAssistantTool> ResolveAllowedTools(AssistantConversation conversation);
}

public sealed class AssistantToolPolicy(AssistantToolDispatcher dispatcher) : IAssistantToolPolicy
{
    public IReadOnlyList<IAssistantTool> ResolveAllowedTools(AssistantConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (AnonymousAssistantUser.IsAnonymous(conversation.UserId))
        {
            return Array.Empty<IAssistantTool>();
        }

        return dispatcher.Tools.ToArray();
    }
}
