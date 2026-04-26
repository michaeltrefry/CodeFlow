using System.Globalization;
using System.Text;

namespace CodeFlow.Runtime;

/// <summary>
/// Canonical implementation of the feature-branch naming convention used across the code-aware
/// workflow stack (setup agent, PR-publishing agent, and any author-defined templates). Exposed
/// to Scriban templates as the <c>branch_name(prd_title, trace_id)</c> filter via
/// <see cref="ScribanTemplateRenderer"/>; callable directly from C# for the same reason.
///
/// Output shape: <c>&lt;slug&gt;-&lt;8hex&gt;</c>. The slug normalises arbitrary PRD titles to a
/// stable, ASCII, lowercase, dash-joined token capped at <see cref="MaxSlugLength"/> characters
/// and truncated at a word boundary. The 8-hex suffix is the leading hex of the top-level
/// traceId (with hyphens stripped) and guarantees uniqueness even when slugs collide.
/// </summary>
public static class BranchNameHelper
{
    public const int MaxSlugLength = 40;
    public const int TraceIdPrefixLength = 8;
    private const string EmptySlugFallback = "branch";
    private const string EmptyTraceIdFallback = "00000000";

    public static string BranchName(string? prdTitle, string? traceId)
    {
        var slug = Slugify(prdTitle);
        var prefix = TraceIdPrefix(traceId);
        return $"{slug}-{prefix}";
    }

    public static string Slugify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return EmptySlugFallback;
        }

        // NFD-decompose so combining marks (e.g. accents) can be dropped, leaving the ASCII
        // base letters intact. Anything still non-ASCII after that is replaced by a separator.
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (ch is >= '0' and <= '9' or >= 'a' and <= 'z')
            {
                builder.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                builder.Append((char)(ch + 32));
            }
            else
            {
                builder.Append('-');
            }
        }

        var collapsed = CollapseDashes(builder.ToString()).Trim('-');
        if (collapsed.Length == 0)
        {
            return EmptySlugFallback;
        }

        if (collapsed.Length <= MaxSlugLength)
        {
            return collapsed;
        }

        // Truncate at the last word boundary within the cap so the slug doesn't end mid-word.
        // Fall back to a hard cut if there's no boundary in the prefix.
        var capped = collapsed[..MaxSlugLength];
        var lastDash = capped.LastIndexOf('-');
        var truncated = lastDash > 0 ? capped[..lastDash] : capped;
        return truncated.TrimEnd('-');
    }

    public static string TraceIdPrefix(string? traceId)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return EmptyTraceIdFallback;
        }

        var stripped = new StringBuilder(traceId.Length);
        foreach (var ch in traceId)
        {
            if (ch is >= '0' and <= '9' or >= 'a' and <= 'f')
            {
                stripped.Append(ch);
            }
            else if (ch is >= 'A' and <= 'F')
            {
                stripped.Append((char)(ch + 32));
            }
            // Drop everything else (hyphens, braces, etc.)
        }

        if (stripped.Length == 0)
        {
            return EmptyTraceIdFallback;
        }

        return stripped.Length >= TraceIdPrefixLength
            ? stripped.ToString(0, TraceIdPrefixLength)
            : stripped.ToString().PadRight(TraceIdPrefixLength, '0');
    }

    private static string CollapseDashes(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasDash = false;
        foreach (var ch in value)
        {
            if (ch == '-')
            {
                if (!previousWasDash)
                {
                    builder.Append('-');
                }
                previousWasDash = true;
            }
            else
            {
                builder.Append(ch);
                previousWasDash = false;
            }
        }
        return builder.ToString();
    }
}
