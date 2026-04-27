using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

/// <summary>
/// Idempotently inserts every <see cref="SystemPromptPartials.All"/> entry into
/// <see cref="CodeFlowDbContext.PromptPartials"/> if a row at that (key, version) doesn't
/// already exist. Safe to call on every startup. Existing user-added partials at the same
/// key but different version are unaffected; existing system partial rows are not mutated
/// (immutable per (key, version)).
/// </summary>
public static class SystemPromptPartialSeeder
{
    public static async Task SeedAsync(CodeFlowDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Fetch every existing (key, version) for the seed keys in one round-trip so we can
        // decide which inserts are needed without per-row lookups.
        var seedKeys = SystemPromptPartials.All.Select(p => p.Key).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await db.PromptPartials
            .AsNoTracking()
            .Where(p => seedKeys.Contains(p.Key))
            .Select(p => new { p.Key, p.Version })
            .ToArrayAsync(cancellationToken);

        var existingSet = new HashSet<(string Key, int Version)>(
            existing.Select(row => (row.Key, row.Version)));

        var inserts = new List<PromptPartialEntity>();
        foreach (var partial in SystemPromptPartials.All)
        {
            if (existingSet.Contains((partial.Key, partial.Version)))
            {
                continue;
            }

            inserts.Add(new PromptPartialEntity
            {
                Key = partial.Key,
                Version = partial.Version,
                Body = partial.Body,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = null,
                IsSystemManaged = true,
            });
        }

        if (inserts.Count == 0)
        {
            return;
        }

        db.PromptPartials.AddRange(inserts);
        await db.SaveChangesAsync(cancellationToken);
    }
}
