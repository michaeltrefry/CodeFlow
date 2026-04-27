namespace CodeFlow.Persistence;

/// <summary>
/// P3: per-ReviewLoop opt-in for built-in rejection-history accumulation. When
/// <see cref="Enabled"/> is true, the saga writes the loop-decision artifact at the moment
/// each rejection round closes into the reserved workflow variable
/// <c>__loop.rejectionHistory</c>, retaining at most <see cref="MaxBytes"/> bytes
/// (oldest rounds dropped first) and emitting in the configured <see cref="Format"/>.
///
/// New ReviewLoops created from the editor scaffolds (S4) opt in by default; existing
/// ReviewLoops loaded from rows pre-dating P3 carry a NULL config and behave exactly as
/// they did before — the migration adds the column nullable and does not back-fill.
/// </summary>
public sealed record RejectionHistoryConfig(
    bool Enabled,
    int MaxBytes = 32_768,
    RejectionHistoryFormat Format = RejectionHistoryFormat.Markdown);

public enum RejectionHistoryFormat
{
    Markdown,
    Json,
}
