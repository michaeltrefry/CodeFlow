using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class SkillRepository(CodeFlowDbContext dbContext) : ISkillRepository
{
    public async Task<IReadOnlyList<Skill>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Skills.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(skill => !skill.IsArchived);
        }

        var entities = await query
            .OrderBy(skill => skill.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<Skill?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Skills
            .AsNoTracking()
            .SingleOrDefaultAsync(skill => skill.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeName(name);
        var entity = await dbContext.Skills
            .AsNoTracking()
            .SingleOrDefaultAsync(skill => skill.Name == normalized, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<long> CreateAsync(SkillCreate create, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);

        var now = DateTime.UtcNow;
        var entity = new SkillEntity
        {
            Name = NormalizeName(create.Name),
            Body = Require(create.Body, nameof(create.Body)),
            CreatedAtUtc = now,
            CreatedBy = Trim(create.CreatedBy),
            UpdatedAtUtc = now,
            UpdatedBy = Trim(create.CreatedBy),
        };

        dbContext.Skills.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    public async Task UpdateAsync(long id, SkillUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var entity = await dbContext.Skills
            .SingleOrDefaultAsync(skill => skill.Id == id, cancellationToken)
            ?? throw new SkillNotFoundException(id);

        entity.Name = NormalizeName(update.Name);
        entity.Body = Require(update.Body, nameof(update.Body));
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedBy = Trim(update.UpdatedBy);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Skills
            .SingleOrDefaultAsync(skill => skill.Id == id, cancellationToken)
            ?? throw new SkillNotFoundException(id);

        if (!entity.IsArchived)
        {
            entity.IsArchived = true;
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static Skill Map(SkillEntity entity) => new(
        Id: entity.Id,
        Name: entity.Name,
        Body: entity.Body,
        CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
        CreatedBy: entity.CreatedBy,
        UpdatedAtUtc: DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc),
        UpdatedBy: entity.UpdatedBy,
        IsArchived: entity.IsArchived);

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    private static string Require(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
