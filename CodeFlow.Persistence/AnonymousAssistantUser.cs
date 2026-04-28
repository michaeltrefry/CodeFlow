namespace CodeFlow.Persistence;

/// <summary>
/// Convention for synthetic user identifiers used by ephemeral demo-mode (unauthenticated)
/// assistant conversations on the homepage. Stored verbatim in <c>assistant_conversations.user_id</c>
/// so the unique index <c>(user_id, scope_key)</c> still applies — each anonymous visitor gets
/// their own row, with knowledge of the conversation guid acting as the access token.
/// </summary>
public static class AnonymousAssistantUser
{
    public const string Prefix = "anon:";

    public static bool IsAnonymous(string? userId)
        => !string.IsNullOrEmpty(userId) && userId.StartsWith(Prefix, StringComparison.Ordinal);

    public static string New()
        => Prefix + Guid.NewGuid().ToString("N");
}
