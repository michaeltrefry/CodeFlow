using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class WorkflowFixtureRepository : IWorkflowFixtureRepository
{
    private readonly CodeFlowDbContext db;

    public WorkflowFixtureRepository(CodeFlowDbContext db)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<IReadOnlyList<WorkflowFixtureEntity>> ListForWorkflowAsync(
        string workflowKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        return await db.Set<WorkflowFixtureEntity>()
            .AsNoTracking()
            .Where(fixture => fixture.WorkflowKey == workflowKey)
            .OrderBy(fixture => fixture.FixtureKey)
            .ToListAsync(cancellationToken);
    }

    public Task<WorkflowFixtureEntity?> GetAsync(long id, CancellationToken cancellationToken)
    {
        return db.Set<WorkflowFixtureEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(fixture => fixture.Id == id, cancellationToken);
    }

    public Task<WorkflowFixtureEntity?> GetByKeyAsync(
        string workflowKey,
        string fixtureKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureKey);
        return db.Set<WorkflowFixtureEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                fixture => fixture.WorkflowKey == workflowKey && fixture.FixtureKey == fixtureKey,
                cancellationToken);
    }

    public async Task<WorkflowFixtureEntity> CreateAsync(
        WorkflowFixtureEntity fixture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var now = DateTime.UtcNow;
        fixture.CreatedAtUtc = now;
        fixture.UpdatedAtUtc = now;
        db.Set<WorkflowFixtureEntity>().Add(fixture);
        await db.SaveChangesAsync(cancellationToken);
        return fixture;
    }

    public async Task<WorkflowFixtureEntity> UpdateAsync(
        WorkflowFixtureEntity fixture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var tracked = await db.Set<WorkflowFixtureEntity>()
            .FirstOrDefaultAsync(f => f.Id == fixture.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Workflow fixture {fixture.Id} does not exist.");

        tracked.WorkflowKey = fixture.WorkflowKey;
        tracked.FixtureKey = fixture.FixtureKey;
        tracked.DisplayName = fixture.DisplayName;
        tracked.StartingInput = fixture.StartingInput;
        tracked.MockResponsesJson = fixture.MockResponsesJson;
        tracked.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return tracked;
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var tracked = await db.Set<WorkflowFixtureEntity>()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (tracked is null)
        {
            return;
        }
        db.Set<WorkflowFixtureEntity>().Remove(tracked);
        await db.SaveChangesAsync(cancellationToken);
    }
}
