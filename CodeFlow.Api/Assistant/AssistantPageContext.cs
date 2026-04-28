using System.Text;

namespace CodeFlow.Api.Assistant;

/// <summary>
/// Snapshot of what the user is currently looking at in the UI, sent by the client on each
/// chat turn. The server formats it into a system-message snippet (see
/// <see cref="AssistantPageContextFormatter"/>) and prepends to the assistant's system prompt
/// so the model can resolve implicit references ("this trace", "this node") without the user
/// having to paste IDs.
///
/// <para>Mirrors the client's <c>PageContext</c> discriminator. The client controls what it
/// sends — this is contextual hinting, not authorization. Tool access is still gated by the
/// authenticated user's permissions.</para>
/// </summary>
public sealed record AssistantPageContext(
    string Kind,
    string? Route,
    string? EntityType,
    string? EntityId,
    string? SelectedNodeId,
    string? SelectedScriptSlot);

public static class AssistantPageContextFormatter
{
    /// <summary>
    /// Renders <paramref name="context"/> as a block the model can read alongside the system
    /// prompt. Returns null when the context is empty or has nothing meaningful to say.
    /// </summary>
    public static string? FormatAsSystemMessage(AssistantPageContext? context)
    {
        if (context is null) return null;
        if (string.IsNullOrWhiteSpace(context.Kind)) return null;

        var sb = new StringBuilder();
        sb.AppendLine("<current-page-context>");
        sb.Append("Kind: ").AppendLine(context.Kind);
        if (!string.IsNullOrWhiteSpace(context.Route))
        {
            sb.Append("Route: ").AppendLine(context.Route);
        }
        if (!string.IsNullOrWhiteSpace(context.EntityType) && !string.IsNullOrWhiteSpace(context.EntityId))
        {
            sb.Append("Entity: ").Append(context.EntityType).Append('=').AppendLine(context.EntityId);
        }
        if (!string.IsNullOrWhiteSpace(context.SelectedNodeId))
        {
            sb.Append("Selected node: ").AppendLine(context.SelectedNodeId);
        }
        if (!string.IsNullOrWhiteSpace(context.SelectedScriptSlot))
        {
            sb.Append("Selected script slot: ").AppendLine(context.SelectedScriptSlot);
        }

        var hint = HintFor(context.Kind);
        if (!string.IsNullOrEmpty(hint))
        {
            sb.AppendLine();
            sb.AppendLine(hint);
        }

        sb.Append("</current-page-context>");
        return sb.ToString();
    }

    private static string HintFor(string kind) => kind switch
    {
        "home"            => "The user is on the home page; questions are general.",
        "trace"           => "The user is viewing a trace. \"This trace\" refers to the entity above. Use trace tools to fetch details.",
        "workflow-editor" => "The user is editing a workflow. \"This workflow\" refers to the entity above. \"This node\" refers to the selected node, if any.",
        "agent-editor"    => "The user is editing an agent. \"This agent\" refers to the entity above.",
        "library"         => "The user is browsing the library.",
        "traces-list"     => "The user is browsing a list of traces.",
        "other"           => string.Empty,
        _                 => string.Empty,
    };
}
