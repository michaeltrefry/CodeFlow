namespace CodeFlow.Persistence;

public sealed class McpServerNotFoundException : Exception
{
    public McpServerNotFoundException(long id)
        : base($"No MCP server configured with id {id}.")
    {
    }

    public McpServerNotFoundException(string key)
        : base($"No MCP server configured with key '{key}'.")
    {
    }
}
