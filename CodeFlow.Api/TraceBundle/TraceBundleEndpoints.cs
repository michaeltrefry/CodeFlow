using Microsoft.AspNetCore.Http;

namespace CodeFlow.Api.TraceBundle;

/// <summary>
/// sc-271: <c>GET /api/traces/{id}/bundle</c> — returns a portable trace evidence bundle
/// as <c>application/zip</c>. Buffers to memory before streaming so the response carries
/// a content length and the request scope's <c>DbContext</c> + artifact reads can complete
/// before the response body starts flushing.
/// </summary>
public static class TraceBundleEndpoints
{
    public static async Task<IResult> GetTraceBundleAsync(
        Guid id,
        TraceEvidenceBundleBuilder builder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);

        using var buffer = new MemoryStream();
        var found = await builder.WriteBundleAsync(id, buffer, cancellationToken);
        if (!found)
        {
            return Results.NotFound();
        }

        return Results.File(
            fileContents: buffer.ToArray(),
            contentType: "application/zip",
            fileDownloadName: $"trace-{id:N}.zip");
    }
}
