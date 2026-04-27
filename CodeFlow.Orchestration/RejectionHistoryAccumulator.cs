using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Persistence;

namespace CodeFlow.Orchestration;

/// <summary>
/// P3: pure helper that appends a single round's loop-decision artifact to the running
/// rejection-history accumulator and trims oldest entries to fit within
/// <see cref="RejectionHistoryConfig.MaxBytes"/>.
///
/// The output shape depends on <see cref="RejectionHistoryConfig.Format"/>:
/// <list type="bullet">
/// <item><description><see cref="RejectionHistoryFormat.Markdown"/> — concatenates
/// <c>## Round N\n{body}</c> blocks separated by a blank line. Trimming drops
/// whole round blocks from the front.</description></item>
/// <item><description><see cref="RejectionHistoryFormat.Json"/> — a JSON array of
/// <c>{ "round": N, "body": "..." }</c> objects. Trimming drops the front of the array.
/// </description></item>
/// </list>
///
/// The accumulator is idempotent for the same (round, body) — if the existing accumulator
/// already ends with the round being appended (same round number), the new entry replaces
/// the old one rather than stacking. This guards against saga re-delivery duplicating rounds
/// in the history.
/// </summary>
public static class RejectionHistoryAccumulator
{
    public static string Append(
        string? existing,
        int round,
        string roundBody,
        RejectionHistoryConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(round);

        var body = roundBody ?? string.Empty;
        var maxBytes = config.MaxBytes > 0 ? config.MaxBytes : 32_768;

        return config.Format switch
        {
            RejectionHistoryFormat.Markdown => AppendMarkdown(existing, round, body, maxBytes),
            RejectionHistoryFormat.Json => AppendJson(existing, round, body, maxBytes),
            _ => AppendMarkdown(existing, round, body, maxBytes),
        };
    }

    private static string AppendMarkdown(string? existing, int round, string body, int maxBytes)
    {
        var blocks = ParseMarkdownBlocks(existing).ToList();

        // Idempotency: a re-delivered completion for the same round shouldn't double-record.
        if (blocks.Count > 0 && blocks[^1].Round == round)
        {
            blocks[^1] = new MarkdownBlock(round, body);
        }
        else
        {
            blocks.Add(new MarkdownBlock(round, body));
        }

        return TrimMarkdown(blocks, maxBytes);
    }

    private static string TrimMarkdown(List<MarkdownBlock> blocks, int maxBytes)
    {
        while (blocks.Count > 1 && MeasureMarkdown(blocks) > maxBytes)
        {
            blocks.RemoveAt(0);
        }

        if (blocks.Count == 1 && MeasureMarkdown(blocks) > maxBytes)
        {
            // Even a single block exceeds the budget — truncate the body itself rather than
            // dropping the only round we have. Keep the header intact so the trace still
            // attributes the truncated text to a round.
            var header = $"## Round {blocks[0].Round.ToString(CultureInfo.InvariantCulture)}\n";
            var headerBytes = System.Text.Encoding.UTF8.GetByteCount(header);
            var bodyBudget = Math.Max(0, maxBytes - headerBytes);
            blocks[0] = new MarkdownBlock(blocks[0].Round, TruncateBytes(blocks[0].Body, bodyBudget));
        }

        return string.Join("\n\n", blocks.Select(RenderMarkdown));
    }

    private static int MeasureMarkdown(IReadOnlyList<MarkdownBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return 0;
        }

        var separators = (blocks.Count - 1) * 2; // "\n\n" between blocks
        var content = blocks.Sum(b => System.Text.Encoding.UTF8.GetByteCount(RenderMarkdown(b)));
        return content + separators;
    }

    private static string RenderMarkdown(MarkdownBlock block) =>
        $"## Round {block.Round.ToString(CultureInfo.InvariantCulture)}\n{block.Body}";

    private static IEnumerable<MarkdownBlock> ParseMarkdownBlocks(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            yield break;
        }

        // Split on the round-block boundary. The first segment may be empty if the value
        // starts with `## Round`; downstream filter drops empty segments.
        const string boundary = "\n\n## Round ";
        var firstHeader = existing.StartsWith("## Round ", StringComparison.Ordinal)
            ? existing
            : "\n\n" + existing.TrimStart('\n');

        var segments = firstHeader.Split(boundary);
        foreach (var segment in segments)
        {
            var normalized = segment;
            if (normalized.StartsWith("## Round ", StringComparison.Ordinal))
            {
                normalized = normalized["## Round ".Length..];
            }

            var newlineIndex = normalized.IndexOf('\n');
            if (newlineIndex < 0)
            {
                continue;
            }

            var roundSegment = normalized[..newlineIndex];
            if (!int.TryParse(roundSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var round))
            {
                continue;
            }

            yield return new MarkdownBlock(round, normalized[(newlineIndex + 1)..]);
        }
    }

    private static string AppendJson(string? existing, int round, string body, int maxBytes)
    {
        var array = ParseJsonArray(existing);

        if (array.Count > 0
            && array[^1] is JsonObject lastObj
            && lastObj["round"]?.GetValue<int>() == round)
        {
            lastObj["body"] = body;
        }
        else
        {
            array.Add(new JsonObject
            {
                ["round"] = round,
                ["body"] = body,
            });
        }

        return TrimJson(array, maxBytes);
    }

    private static string TrimJson(JsonArray array, int maxBytes)
    {
        while (array.Count > 1 && System.Text.Encoding.UTF8.GetByteCount(array.ToJsonString()) > maxBytes)
        {
            array.RemoveAt(0);
        }
        return array.ToJsonString();
    }

    private static JsonArray ParseJsonArray(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return new JsonArray();
        }

        try
        {
            var node = JsonNode.Parse(existing);
            if (node is JsonArray array)
            {
                // Detach from the existing node so we can mutate without surprises.
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(item is null ? null : JsonNode.Parse(item.ToJsonString()));
                }
                return copy;
            }
        }
        catch (JsonException)
        {
            // Corrupt accumulator — start fresh rather than throw.
        }

        return new JsonArray();
    }

    private static string TruncateBytes(string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        // Trim the byte buffer to the budget, then walk back to the previous valid UTF-8
        // boundary so we never split a multi-byte sequence.
        var trimmedLength = maxBytes;
        while (trimmedLength > 0 && (bytes[trimmedLength] & 0b1100_0000) == 0b1000_0000)
        {
            trimmedLength--;
        }

        return System.Text.Encoding.UTF8.GetString(bytes, 0, trimmedLength);
    }

    private readonly record struct MarkdownBlock(int Round, string Body);

    /// <summary>
    /// Reserved workflow-bag key under the <see cref="ProtectedVariables.ReservedNamespaces"/>
    /// <c>__loop</c> prefix. Stored as a flat key with the literal dot in the name; the
    /// template-variable flattener turns it into <c>workflow.__loop.rejectionHistory</c>
    /// in Scriban scope. The agent consumer additionally copies the value to a top-level
    /// <c>rejectionHistory</c> alias for ergonomic <c>{{ rejectionHistory }}</c> usage.
    /// </summary>
    public const string WorkflowVariableKey = "__loop.rejectionHistory";
}

/// <summary>
/// Stable feature ids emitted by built-in pattern-to-feature features (P1-P5) for
/// observability — surfaced via activity tags today, ready for an
/// <c>IAuthoringTelemetry.FeatureUsed</c> sink in the future.
/// </summary>
public static class BuiltInFeatureIds
{
    public const string RejectionHistory = "rejection-history";
    public const string LastRoundReminder = "last-round-reminder";
}
