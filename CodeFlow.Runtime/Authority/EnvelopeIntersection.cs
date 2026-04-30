namespace CodeFlow.Runtime.Authority;

/// <summary>
/// Pure per-axis intersection algorithm for <see cref="WorkflowExecutionEnvelope"/>. Most
/// restrictive tier wins per axis; <see cref="BlockedBy"/> evidence is emitted whenever a
/// tier removed an item that another tier granted (set axes), narrowed a numeric budget,
/// or conflicted with another tier on a value that requires exact agreement (delivery).
///
/// Tiers with <c>null</c> values for an axis are treated as "no opinion" — they neither
/// grant nor deny on that axis. See <see cref="WorkflowExecutionEnvelope"/> for the
/// nullability rationale.
/// </summary>
public static class EnvelopeIntersection
{
    public static EnvelopeResolutionResult Intersect(IReadOnlyList<EnvelopeTier> tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        var blocked = new List<BlockedBy>();

        var resolved = new WorkflowExecutionEnvelope(
            RepoScopes: IntersectSet(
                tiers,
                select: e => e.RepoScopes,
                keyOf: s => $"{s.RepoIdentityKey}|{s.Path}|{s.Access}",
                describe: s => $"{s.RepoIdentityKey}:{s.Path} ({s.Access})",
                axis: BlockedBy.Axes.RepoScopes,
                blocked: blocked),
            ToolGrants: IntersectSet(
                tiers,
                select: e => e.ToolGrants,
                keyOf: g => $"{g.Category}|{g.ToolName}",
                describe: g => $"{g.Category}:{g.ToolName}",
                axis: BlockedBy.Axes.ToolGrants,
                blocked: blocked),
            ExecuteGrants: IntersectSet(
                tiers,
                select: e => e.ExecuteGrants,
                keyOf: g => g.Command,
                describe: g => g.Command,
                axis: BlockedBy.Axes.ExecuteGrants,
                blocked: blocked),
            Network: IntersectNetwork(tiers, blocked),
            Budget: IntersectBudget(tiers, blocked),
            Workspace: IntersectWorkspace(tiers),
            Delivery: IntersectDelivery(tiers, blocked));

        return new EnvelopeResolutionResult(resolved, blocked, tiers);
    }

    // ---- Set axes ----------------------------------------------------------

    private static IReadOnlyList<T>? IntersectSet<T>(
        IReadOnlyList<EnvelopeTier> tiers,
        Func<WorkflowExecutionEnvelope, IReadOnlyList<T>?> select,
        Func<T, string> keyOf,
        Func<T, string> describe,
        string axis,
        List<BlockedBy> blocked)
        where T : class
    {
        var perTier = tiers
            .Where(t => select(t.Envelope) is not null)
            .Select(t => new
            {
                t.Name,
                Items = select(t.Envelope)!.ToDictionary(keyOf, item => item, StringComparer.Ordinal)
            })
            .ToArray();

        if (perTier.Length == 0)
        {
            return null;
        }

        if (perTier.Length == 1)
        {
            return perTier[0].Items.Values.ToArray();
        }

        var commonKeys = new HashSet<string>(perTier[0].Items.Keys, StringComparer.Ordinal);
        for (var i = 1; i < perTier.Length; i++)
        {
            commonKeys.IntersectWith(perTier[i].Items.Keys);
        }

        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in perTier)
        {
            foreach (var key in p.Items.Keys)
            {
                allKeys.Add(key);
            }
        }

        foreach (var key in allKeys)
        {
            if (commonKeys.Contains(key))
            {
                continue;
            }

            var sample = perTier.First(p => p.Items.ContainsKey(key)).Items[key];
            var removingTier = perTier.First(p => !p.Items.ContainsKey(key)).Name;

            blocked.Add(new BlockedBy(
                Tier: removingTier,
                Axis: axis,
                Code: BlockedBy.Codes.TierRemoved,
                Reason: $"Tier '{removingTier}' did not grant '{describe(sample)}'.",
                RequestedValue: describe(sample)));
        }

        return commonKeys
            .Select(key => perTier[0].Items[key])
            .ToArray();
    }

    // ---- Network -----------------------------------------------------------

    private static EnvelopeNetwork? IntersectNetwork(IReadOnlyList<EnvelopeTier> tiers, List<BlockedBy> blocked)
    {
        var opinionated = tiers.Where(t => t.Envelope.Network is not null).ToArray();
        if (opinionated.Length == 0)
        {
            return null;
        }

        var policies = opinionated.Select(t => (Tier: t.Name, Policy: t.Envelope.Network!.Allow)).ToArray();
        var minPolicy = policies.Min(p => p.Policy);
        var maxPolicy = policies.Max(p => p.Policy);

        if (minPolicy != maxPolicy)
        {
            var narrowing = policies.First(p => p.Policy == minPolicy).Tier;
            blocked.Add(new BlockedBy(
                Tier: narrowing,
                Axis: BlockedBy.Axes.Network,
                Code: BlockedBy.Codes.Narrowed,
                Reason: $"Network policy narrowed from '{maxPolicy}' to '{minPolicy}'.",
                RequestedValue: maxPolicy.ToString(),
                AllowedValue: minPolicy.ToString()));
        }

        if (minPolicy != NetworkPolicy.Allowlist)
        {
            return new EnvelopeNetwork(minPolicy);
        }

        // Allowlist: intersect host sets across tiers that have explicit hosts. A tier with
        // null/empty hosts on Allowlist means "any host" and does not narrow.
        var hostSets = opinionated
            .Where(t => t.Envelope.Network!.Allow == NetworkPolicy.Allowlist
                && t.Envelope.Network!.AllowedHosts is { Count: > 0 })
            .Select(t => t.Envelope.Network!.AllowedHosts!.ToHashSet(StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (hostSets.Length == 0)
        {
            return new EnvelopeNetwork(NetworkPolicy.Allowlist);
        }

        var intersected = new HashSet<string>(hostSets[0], StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < hostSets.Length; i++)
        {
            intersected.IntersectWith(hostSets[i]);
        }

        return new EnvelopeNetwork(NetworkPolicy.Allowlist, intersected.ToArray());
    }

    // ---- Budget ------------------------------------------------------------

    private static EnvelopeBudget? IntersectBudget(IReadOnlyList<EnvelopeTier> tiers, List<BlockedBy> blocked)
    {
        var opinionated = tiers.Where(t => t.Envelope.Budget is not null).ToArray();
        if (opinionated.Length == 0)
        {
            return null;
        }

        var maxTokens = MinNullableLong(opinionated, b => b.MaxTokens, "MaxTokens", blocked);
        var timeoutSeconds = MinNullableInt(opinionated, b => b.TimeoutSeconds, "TimeoutSeconds", blocked);
        var maxRepairLoops = MinNullableInt(opinionated, b => b.MaxRepairLoops, "MaxRepairLoops", blocked);

        return new EnvelopeBudget(maxTokens, timeoutSeconds, maxRepairLoops);
    }

    private static long? MinNullableLong(
        IReadOnlyList<EnvelopeTier> opinionated,
        Func<EnvelopeBudget, long?> select,
        string field,
        List<BlockedBy> blocked)
    {
        var values = opinionated
            .Select(t => (Tier: t.Name, Value: select(t.Envelope.Budget!)))
            .Where(pair => pair.Value is not null)
            .ToArray();

        if (values.Length == 0)
        {
            return null;
        }

        var min = values.Min(v => v.Value!.Value);
        var max = values.Max(v => v.Value!.Value);
        if (min < max)
        {
            var narrowing = values.First(v => v.Value == min).Tier;
            blocked.Add(new BlockedBy(
                Tier: narrowing,
                Axis: BlockedBy.Axes.Budget,
                Code: BlockedBy.Codes.Narrowed,
                Reason: $"Budget '{field}' narrowed from {max} to {min}.",
                RequestedValue: max.ToString(),
                AllowedValue: min.ToString()));
        }

        return min;
    }

    private static int? MinNullableInt(
        IReadOnlyList<EnvelopeTier> opinionated,
        Func<EnvelopeBudget, int?> select,
        string field,
        List<BlockedBy> blocked)
    {
        return MinNullableLong(opinionated, b => select(b), field, blocked) is { } v ? checked((int)v) : null;
    }

    // ---- Workspace ---------------------------------------------------------

    private static EnvelopeWorkspace? IntersectWorkspace(IReadOnlyList<EnvelopeTier> tiers)
    {
        var opinionated = tiers.Where(t => t.Envelope.Workspace is not null).ToArray();
        if (opinionated.Length == 0)
        {
            return null;
        }

        var symlink = opinionated.Max(t => t.Envelope.Workspace!.SymlinkPolicy);

        var allowlistTiers = opinionated
            .Where(t => t.Envelope.Workspace!.CommandAllowlist is not null)
            .ToArray();

        IReadOnlyList<string>? allowlist;
        if (allowlistTiers.Length == 0)
        {
            allowlist = null;
        }
        else
        {
            var intersected = new HashSet<string>(
                allowlistTiers[0].Envelope.Workspace!.CommandAllowlist!,
                StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < allowlistTiers.Length; i++)
            {
                intersected.IntersectWith(allowlistTiers[i].Envelope.Workspace!.CommandAllowlist!);
            }
            allowlist = intersected.ToArray();
        }

        var allowDirty = opinionated.All(t => t.Envelope.Workspace!.AllowDirty);

        return new EnvelopeWorkspace(symlink, allowlist, allowDirty);
    }

    // ---- Delivery ----------------------------------------------------------

    private static DeliveryTarget? IntersectDelivery(IReadOnlyList<EnvelopeTier> tiers, List<BlockedBy> blocked)
    {
        var opinionated = tiers.Where(t => t.Envelope.Delivery is not null).ToArray();
        if (opinionated.Length == 0)
        {
            return null;
        }

        var first = opinionated[0].Envelope.Delivery!;
        for (var i = 1; i < opinionated.Length; i++)
        {
            var next = opinionated[i].Envelope.Delivery!;
            if (next != first)
            {
                blocked.Add(new BlockedBy(
                    Tier: opinionated[i].Name,
                    Axis: BlockedBy.Axes.Delivery,
                    Code: BlockedBy.Codes.Conflict,
                    Reason: $"Delivery target conflict between tier '{opinionated[0].Name}' ({first}) and tier '{opinionated[i].Name}' ({next}).",
                    RequestedValue: first.ToString(),
                    AllowedValue: next.ToString()));
                return null;
            }
        }

        return first;
    }
}
