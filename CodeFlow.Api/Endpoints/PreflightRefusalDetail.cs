using System.Text.Json.Nodes;
using CodeFlow.Runtime.Authority.Preflight;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// Builds the <c>DetailJson</c> blob for a <see cref="CodeFlow.Runtime.Authority.RefusalEvent"/>
/// at <see cref="CodeFlow.Runtime.Authority.RefusalStages.Preflight"/>. Shared between sc-274's
/// replay-edit gate (phase 1) and assistant-chat gate (phase 2) so both producers persist the
/// same shape — governance queries can rely on a stable schema regardless of which entry point
/// refused.
/// </summary>
internal static class PreflightRefusalDetail
{
    /// <summary>
    /// Serializes the assessment into the canonical preflight refusal detail shape:
    /// <c>{ mode, score, threshold, dimensions[], missingFields[], clarificationQuestions[] }</c>.
    /// </summary>
    public static JsonObject Build(IntentClarityAssessment assessment) =>
        new()
        {
            ["mode"] = assessment.Mode.ToString(),
            ["score"] = assessment.OverallScore,
            ["threshold"] = assessment.Threshold,
            ["dimensions"] = new JsonArray(assessment.Dimensions
                .Select(d => (JsonNode)new JsonObject
                {
                    ["dimension"] = d.Dimension,
                    ["score"] = d.Score,
                    ["reason"] = d.Reason,
                }).ToArray()),
            ["missingFields"] = new JsonArray(
                assessment.MissingFields.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["clarificationQuestions"] = new JsonArray(
                assessment.ClarificationQuestions.Select(q => (JsonNode)JsonValue.Create(q)!).ToArray()),
        };

    /// <summary>
    /// The lowest-scoring dimension name (first one if multiple tied), or null when there are
    /// no dimensions. Used as the <c>Axis</c> field on the refusal event so governance can
    /// slice "preflight refusals where goal was the blocker" without parsing the detail blob.
    /// </summary>
    public static string? LowestDimensionAxis(IntentClarityAssessment assessment) =>
        assessment.Dimensions.OrderBy(d => d.Score).FirstOrDefault()?.Dimension;
}
