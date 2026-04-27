namespace CodeFlow.Persistence;

/// <summary>
/// CRUD over <see cref="WorkflowFixtureEntity"/>. Fixtures are mutable per
/// <c>(workflowKey, fixtureKey)</c> — author edits replace the row in place.
/// </summary>
public interface IWorkflowFixtureRepository
{
    Task<IReadOnlyList<WorkflowFixtureEntity>> ListForWorkflowAsync(
        string workflowKey,
        CancellationToken cancellationToken);

    Task<WorkflowFixtureEntity?> GetAsync(long id, CancellationToken cancellationToken);

    Task<WorkflowFixtureEntity?> GetByKeyAsync(
        string workflowKey,
        string fixtureKey,
        CancellationToken cancellationToken);

    Task<WorkflowFixtureEntity> CreateAsync(
        WorkflowFixtureEntity fixture,
        CancellationToken cancellationToken);

    Task<WorkflowFixtureEntity> UpdateAsync(
        WorkflowFixtureEntity fixture,
        CancellationToken cancellationToken);

    Task DeleteAsync(long id, CancellationToken cancellationToken);
}
