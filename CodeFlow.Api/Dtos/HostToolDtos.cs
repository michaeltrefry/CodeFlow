using System.Text.Json.Nodes;

namespace CodeFlow.Api.Dtos;

public sealed record HostToolResponse(
    string Name,
    string Description,
    JsonNode? Parameters,
    bool IsMutating);
