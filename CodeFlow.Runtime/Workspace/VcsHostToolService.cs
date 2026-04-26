using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Host-tool dispatch for the <c>vcs.*</c> verbs. Resolves an <see cref="IVcsProvider"/> via
/// <see cref="IVcsProviderFactory"/> on every call so the configured Git host (mode + token) is
/// read fresh and not cached. Translates <see cref="VcsException"/>s into structured tool-error
/// payloads so agents see the failure taxonomy without needing to parse exception text.
/// </summary>
public sealed class VcsHostToolService
{
    private readonly IVcsProviderFactory factory;

    public VcsHostToolService(IVcsProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.factory = factory;
    }

    public async Task<ToolResult> OpenPullRequestAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var owner = GetRequiredString(toolCall.Arguments, "owner");
        var name = GetRequiredString(toolCall.Arguments, "name");
        var head = GetRequiredString(toolCall.Arguments, "head");
        var baseRef = GetRequiredString(toolCall.Arguments, "base");
        var title = GetRequiredString(toolCall.Arguments, "title");
        var body = GetOptionalString(toolCall.Arguments, "body") ?? string.Empty;

        try
        {
            var provider = await factory.CreateAsync(cancellationToken);
            var pr = await provider.OpenPullRequestAsync(
                owner, name, head, baseRef, title, body, cancellationToken);

            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["url"] = pr.Url,
                    ["number"] = pr.Number,
                }.ToJsonString());
        }
        catch (Exception ex) when (ex is VcsException or GitHostNotConfiguredException)
        {
            return BuildError(toolCall.Id, ex);
        }
    }

    public async Task<ToolResult> GetRepoMetadataAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var owner = GetRequiredString(toolCall.Arguments, "owner");
        var name = GetRequiredString(toolCall.Arguments, "name");

        try
        {
            var provider = await factory.CreateAsync(cancellationToken);
            var meta = await provider.GetRepoMetadataAsync(owner, name, cancellationToken);

            return new ToolResult(
                toolCall.Id,
                new JsonObject
                {
                    ["defaultBranch"] = meta.DefaultBranch,
                    ["cloneUrl"] = meta.CloneUrl,
                    ["visibility"] = meta.Visibility.ToString(),
                }.ToJsonString());
        }
        catch (Exception ex) when (ex is VcsException or GitHostNotConfiguredException)
        {
            return BuildError(toolCall.Id, ex);
        }
    }

    private static ToolResult BuildError(string callId, Exception ex)
    {
        var kind = ex switch
        {
            VcsRepoNotFoundException => "repo_not_found",
            VcsUnauthorizedException => "unauthorized",
            VcsRateLimitedException => "rate_limited",
            VcsConflictException => "conflict",
            GitHostNotConfiguredException => "not_configured",
            _ => "vcs_error",
        };

        var payload = new JsonObject
        {
            ["error"] = kind,
            ["message"] = ex.Message,
        };
        return new ToolResult(callId, payload.ToJsonString(), IsError: true);
    }

    private static string GetRequiredString(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is JsonValue value && value.TryGetValue<string>(out var str)
            && !string.IsNullOrWhiteSpace(str))
        {
            return str;
        }

        throw new InvalidOperationException(
            $"vcs tool requires a non-empty string '{propertyName}' argument.");
    }

    private static string? GetOptionalString(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is JsonValue value && value.TryGetValue<string>(out var str))
        {
            return str;
        }

        return null;
    }
}
