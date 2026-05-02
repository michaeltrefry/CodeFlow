using System.Text.Json;
using CodeFlow.Api.Assistant.Skills;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Catalog tool — lets the model discover which assistant skills are available without paying the
/// cost of loading any of their bodies. Returns one row per skill with key/name/description.
/// </summary>
/// <remarks>
/// AS-1 ships the loading machinery; AS-3/4/5 migrate the actual content into skills. Until those
/// later slices land, this tool returns an empty list — a no-op handshake the base prompt can
/// still describe without lying.
/// </remarks>
public sealed class ListAssistantSkillsTool(IAssistantSkillProvider skills) : IAssistantTool
{
    public string Name => "list_assistant_skills";

    public string Description =>
        "List the assistant skills you can load on demand. Each skill bundles a slice of the " +
        "platform's curated knowledge (workflow authoring, runtime vocabulary, trace diagnosis, " +
        "etc.) that you only pay for when the conversation actually needs it. Returns a row per " +
        "skill with `key`, `name`, `description`, and `trigger` (a short hint about when to " +
        "load the skill). Call `load_assistant_skill({ key })` to pull a skill's body into the " +
        "transcript before answering domain-specific questions. No arguments.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {},
        ""additionalProperties"": false
    }");

    public Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var catalog = skills.List();
        var payload = new
        {
            count = catalog.Count,
            skills = catalog
                .Select(s => new
                {
                    key = s.Key,
                    name = s.Name,
                    description = s.Description,
                    trigger = s.Trigger,
                })
                .ToArray(),
        };

        return Task.FromResult(new AssistantToolResult(
            JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions)));
    }
}

/// <summary>
/// Pull one skill's body into the transcript. Once loaded the body lives in conversation history
/// for the rest of the session — the model can reference it without re-loading.
/// </summary>
public sealed class LoadAssistantSkillTool(IAssistantSkillProvider skills) : IAssistantTool
{
    public string Name => "load_assistant_skill";

    public string Description =>
        "Load one assistant skill's body into the conversation. Returns `{ key, body }` where " +
        "`body` is markdown that becomes part of the transcript and stays available for the rest " +
        "of the session — load a skill once, reference it freely after. Pass the `key` returned " +
        "by `list_assistant_skills`. Use this when the user asks a question that needs a " +
        "domain-specific procedure (workflow authoring, trace diagnosis, replay-with-edit, etc.) " +
        "and the matching skill is not already loaded.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""key"": { ""type"": ""string"", ""description"": ""Skill key from list_assistant_skills (e.g. 'workflow-authoring').'"" }
        },
        ""required"": [""key""],
        ""additionalProperties"": false
    }");

    public Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var key = AssistantToolJson.ReadOptionalString(arguments, "key");
        if (key is null)
        {
            return Task.FromResult(Error("Provide a `key` (string)."));
        }

        var skill = skills.Get(key);
        if (skill is null)
        {
            var available = skills.List();
            var hint = available.Count == 0
                ? "No skills are currently registered."
                : $"Available keys: {string.Join(", ", available.Select(s => s.Key))}.";

            return Task.FromResult(Error($"No assistant skill with key '{key}'. {hint}"));
        }

        var payload = new
        {
            key = skill.Key,
            body = skill.Body,
        };

        return Task.FromResult(new AssistantToolResult(
            JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions)));
    }

    private static AssistantToolResult Error(string message)
    {
        return new AssistantToolResult(
            JsonSerializer.Serialize(new { error = message }, AssistantToolJson.SerializerOptions),
            IsError: true);
    }
}
