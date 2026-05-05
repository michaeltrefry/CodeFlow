using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;

namespace CodeFlow.Api.Mapping;

internal static class WorkflowFixtureMappings
{
    public static WorkflowFixtureSummaryResponse ToSummaryDto(this WorkflowFixtureEntity fixture) =>
        new(fixture.Id, fixture.WorkflowKey, fixture.FixtureKey, fixture.DisplayName,
            fixture.CreatedAtUtc, fixture.UpdatedAtUtc);

    public static WorkflowFixtureDetailResponse ToDetailDto(this WorkflowFixtureEntity fixture) =>
        new(
            fixture.Id,
            fixture.WorkflowKey,
            fixture.FixtureKey,
            fixture.DisplayName,
            fixture.StartingInput,
            TryParseJsonObject(fixture.MockResponsesJson),
            fixture.CreatedAtUtc,
            fixture.UpdatedAtUtc);

    private static JsonNode TryParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
