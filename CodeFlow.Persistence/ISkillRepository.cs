namespace CodeFlow.Persistence;

public interface ISkillRepository
{
    Task<IReadOnlyList<Skill>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default);

    Task<Skill?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(SkillCreate create, CancellationToken cancellationToken = default);

    Task UpdateAsync(long id, SkillUpdate update, CancellationToken cancellationToken = default);

    Task ArchiveAsync(long id, CancellationToken cancellationToken = default);
}
