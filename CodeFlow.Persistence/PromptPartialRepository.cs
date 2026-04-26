using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

/// <summary>
/// Read/write access to prompt partials. Partials are immutable per (Key, Version); to "edit" a
/// partial, callers append a new version. The repository never mutates an existing row.
/// </summary>
public interface IPromptPartialRepository
{
    /// <summary>
    /// Returns the partial pinned at the given version, or throws
    /// <see cref="PromptPartialNotFoundException"/> if it doesn't exist.
    /// </summary>
    Task<PromptPartialEntity> GetAsync(string key, int version, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the partial pinned at the given version, or null if not found. Used by the renderer
    /// path so a missing pin can surface a structured error to the author rather than an exception.
    /// </summary>
    Task<PromptPartialEntity?> TryGetAsync(string key, int version, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the highest version recorded for the given key, or null when no partial exists.
    /// </summary>
    Task<int?> GetLatestVersionAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Append a new version with the supplied body. The next version number is computed from the
    /// max existing version (latest + 1, starting at 1). Returns the version assigned.
    /// </summary>
    Task<int> CreateNewVersionAsync(
        string key,
        string body,
        string? createdBy,
        bool isSystemManaged,
        CancellationToken cancellationToken);

    /// <summary>
    /// Bulk-resolve a set of partial pins to their bodies. Returned dictionary is keyed by the
    /// partial key (so the renderer can look up the body by include name) — the version is encoded
    /// implicitly by which version was loaded. Missing pins are absent from the result; callers
    /// decide how to surface the gap.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveBodiesAsync(
        IReadOnlyCollection<(string Key, int Version)> pins,
        CancellationToken cancellationToken);
}

public sealed class PromptPartialRepository : IPromptPartialRepository
{
    private readonly CodeFlowDbContext db;

    public PromptPartialRepository(CodeFlowDbContext db)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<PromptPartialEntity> GetAsync(string key, int version, CancellationToken cancellationToken)
    {
        var entity = await TryGetAsync(key, version, cancellationToken);
        return entity ?? throw new PromptPartialNotFoundException(key, version);
    }

    public Task<PromptPartialEntity?> TryGetAsync(string key, int version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return db.Set<PromptPartialEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == key && p.Version == version, cancellationToken);
    }

    public async Task<int?> GetLatestVersionAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var max = await db.Set<PromptPartialEntity>()
            .AsNoTracking()
            .Where(p => p.Key == key)
            .Select(p => (int?)p.Version)
            .MaxAsync(cancellationToken);
        return max;
    }

    public async Task<int> CreateNewVersionAsync(
        string key,
        string body,
        string? createdBy,
        bool isSystemManaged,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var latest = await GetLatestVersionAsync(key, cancellationToken);
        var nextVersion = (latest ?? 0) + 1;

        db.Set<PromptPartialEntity>().Add(new PromptPartialEntity
        {
            Key = key,
            Version = nextVersion,
            Body = body,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy,
            IsSystemManaged = isSystemManaged,
        });
        await db.SaveChangesAsync(cancellationToken);
        return nextVersion;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveBodiesAsync(
        IReadOnlyCollection<(string Key, int Version)> pins,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // Pull all candidates by key in a single round-trip; filter to exact (Key, Version)
        // matches in memory. EF Core can't translate tuple-set predicates to SQL across
        // providers cleanly, and there are typically only a handful of pins per agent.
        var keys = pins.Select(p => p.Key).Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.Set<PromptPartialEntity>()
            .AsNoTracking()
            .Where(p => keys.Contains(p.Key))
            .Select(p => new { p.Key, p.Version, p.Body })
            .ToArrayAsync(cancellationToken);

        var pinSet = pins.ToHashSet();
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (pinSet.Contains((row.Key, row.Version)))
            {
                resolved[row.Key] = row.Body;
            }
        }
        return resolved;
    }
}
