using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Translates the assistant tool registry to OpenAI's <see cref="ChatTool"/> shape. Each tool
/// becomes a function tool whose parameters block is the tool's JSON Schema serialized to
/// <see cref="BinaryData"/>.
/// </summary>
internal static class OpenAiToolMapper
{
    public static IReadOnlyList<ChatTool> Map(IEnumerable<IAssistantTool> tools)
    {
        return tools.Select(MapOne).ToArray();
    }

    private static ChatTool MapOne(IAssistantTool tool)
    {
        var schemaJson = tool.InputSchema.GetRawText();
        var binary = BinaryData.FromBytes(Encoding.UTF8.GetBytes(schemaJson));
        return ChatTool.CreateFunctionTool(
            tool.Name,
            tool.Description,
            binary,
            functionSchemaIsStrict: null);
    }
}
