namespace CodeFlow.Persistence;

public sealed class SkillNotFoundException : Exception
{
    public SkillNotFoundException(long id)
        : base($"No skill with id {id}.")
    {
    }

    public SkillNotFoundException(string name)
        : base($"No skill with name '{name}'.")
    {
    }
}
