using System.Text.Json.Serialization;

namespace CodeFlow.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageRole>))]
public enum ChatMessageRole
{
    System,
    User,
    Assistant,
    Tool
}
