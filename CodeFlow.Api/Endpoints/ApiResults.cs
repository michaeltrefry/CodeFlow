using Microsoft.AspNetCore.Http;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// Canonical helpers for endpoint error responses. Replaces the ad-hoc
/// <c>Results.{BadRequest|NotFound|Conflict|...}(new { error = "..." })</c> pattern that grew
/// across the endpoint surface (F-005 in the 2026-04-28 backend review). Every helper here emits
/// RFC 7807 <c>ProblemDetails</c> via <see cref="Results.Problem"/> so clients (UI, assistant,
/// tests) can share one error-parsing path.
/// </summary>
internal static class ApiResults
{
    /// <summary>
    /// Generic ProblemDetails response with a message in the <c>detail</c> field. Caller picks
    /// the status code; prefer the named helpers below for the common cases.
    /// </summary>
    public static IResult Error(string message, int statusCode) =>
        Results.Problem(detail: message, statusCode: statusCode);

    public static IResult BadRequest(string message) =>
        Error(message, StatusCodes.Status400BadRequest);

    public static IResult NotFound(string message) =>
        Error(message, StatusCodes.Status404NotFound);

    public static IResult Conflict(string message) =>
        Error(message, StatusCodes.Status409Conflict);

    public static IResult UnprocessableEntity(string message) =>
        Error(message, StatusCodes.Status422UnprocessableEntity);
}
