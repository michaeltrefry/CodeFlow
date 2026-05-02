namespace CodeFlow.Api.Assistant.Skills;

/// <summary>
/// Source of <see cref="AssistantSkill"/>s. The default implementation reads from embedded
/// markdown resources at startup; future implementations may pull from the database to support
/// admin-authored skills (out of scope for the AS- epic v1).
/// </summary>
public interface IAssistantSkillProvider
{
    /// <summary>
    /// All skills, in catalog order (alphabetical by <see cref="AssistantSkill.Key"/>). May be
    /// empty when no skills are registered — that's the v1 baseline before content migrates.
    /// </summary>
    IReadOnlyList<AssistantSkill> List();

    /// <summary>
    /// Look up a single skill by key. Returns null when no skill with that key exists; callers
    /// (the load tool) translate null into a tool error so the model can recover.
    /// </summary>
    AssistantSkill? Get(string key);
}
