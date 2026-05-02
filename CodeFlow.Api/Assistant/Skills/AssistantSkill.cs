namespace CodeFlow.Api.Assistant.Skills;

/// <summary>
/// One assistant skill — a chunk of curated CodeFlow knowledge the homepage assistant can pull
/// into a conversation on demand. <see cref="Key"/> is the slug the model passes to
/// <c>load_assistant_skill</c>; <see cref="Name"/> and <see cref="Description"/> appear in the
/// catalog rendered by <c>list_assistant_skills</c>; <see cref="Body"/> is the markdown that lands
/// in the transcript when the skill is loaded.
/// </summary>
public sealed record AssistantSkill(
    string Key,
    string Name,
    string Description,
    string Body);
