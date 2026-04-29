using Microsoft.Extensions.Caching.Memory;

namespace CodeFlow.Persistence;

/// <summary>
/// Process-wide bounded cache for immutable, version-pinned entities (workflows, agent configs).
/// Extracted from <see cref="WorkflowRepository"/> + <see cref="AgentConfigRepository"/> per
/// F-006 in the 2026-04-28 backend review — both repositories had identical
/// <see cref="MemoryCache"/> wiring and a hand-rolled <c>ClearCacheForTests</c> hatch. Future
/// version-pinned repositories can lean on this helper instead of copying the pattern.
/// </summary>
/// <typeparam name="TKey">Key type. Repositories typically use a typed record like
/// <c>WorkflowCacheKey</c> so the call site is hard to misuse.</typeparam>
/// <typeparam name="TValue">Cached value type — the immutable entity domain object.</typeparam>
/// <remarks>
/// Backed by a private <see cref="MemoryCache"/> with a fixed size limit and sliding expiration
/// so long-lived processes don't retain every version ever touched. Each entry counts as size 1.
/// </remarks>
internal sealed class VersionedEntityCache<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly MemoryCache cache;
    private readonly MemoryCacheEntryOptions entryOptions;

    public VersionedEntityCache(int sizeLimit, TimeSpan slidingExpiration)
    {
        if (sizeLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeLimit), sizeLimit, "must be positive");
        }
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = sizeLimit });
        entryOptions = new MemoryCacheEntryOptions()
            .SetSize(1)
            .SetSlidingExpiration(slidingExpiration);
    }

    public TValue? Get(TKey cacheKey)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        return cache.TryGetValue<TValue>(cacheKey, out var existing) ? existing : null;
    }

    public void Set(TKey cacheKey, TValue value)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(value);
        cache.Set(cacheKey, value, entryOptions);
    }

    /// <summary>
    /// Test-only escape hatch: drop every cached entry. Required because the cache instance is
    /// typically held in a static field on the repository and can leak entries between tests
    /// that reuse keys against fresh in-memory databases. Production code never calls this.
    /// </summary>
    public void Clear() => cache.Clear();
}
