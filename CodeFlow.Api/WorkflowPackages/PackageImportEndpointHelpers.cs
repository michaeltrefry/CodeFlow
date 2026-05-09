using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.WorkflowPackages;

/// <summary>
/// Shared helpers + on-the-wire DTOs for the workflow- and agent-package import endpoints.
/// Both sides convert the same flattened <see cref="WorkflowPackageImportResolutionRequest"/>
/// shape, run the same drift gate, and convert the same exception types into ProblemDetails —
/// extracting keeps a third package importer (or a refactor of the existing two) consistent.
/// </summary>
public static class PackageImportEndpointHelpers
{
    public static IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? ConvertResolutions(
        IReadOnlyList<WorkflowPackageImportResolutionRequest>? wire,
        out Dictionary<WorkflowPackageImportResolutionKey, int> expectedMaxVersions)
    {
        expectedMaxVersions = new();
        if (wire is null || wire.Count == 0)
        {
            return null;
        }

        var domain = new Dictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>(wire.Count);
        foreach (var entry in wire)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new WorkflowPackageResolutionException(
                    "Each resolution must include a Key. The dictionary key (Kind, Key, SourceVersion) must be unique across the resolutions list.");
            }
            var target = new WorkflowPackageImportResolutionKey(entry.Kind, entry.Key, entry.SourceVersion);
            if (domain.ContainsKey(target))
            {
                throw new WorkflowPackageResolutionException(
                    $"Duplicate resolution target: {entry.Kind} '{entry.Key}'"
                    + (entry.SourceVersion is int v ? $" v{v}" : string.Empty)
                    + ". Each (kind, key, sourceVersion) tuple may appear at most once.");
            }
            domain[target] = new WorkflowPackageImportResolution(target, entry.Mode, entry.NewKey);

            if (entry.ExpectedExistingMaxVersion is int expected)
            {
                expectedMaxVersions[target] = expected;
            }
        }
        return domain;
    }

    /// <summary>
    /// sc-395: re-read the live max version for each resolved (Agent or Workflow) entity that
    /// carried an <c>expectedExistingMaxVersion</c> from the client's preview. Returns the
    /// per-entity drift list (empty when nothing has moved). Caller decides whether to gate
    /// the apply on a non-empty list (current behavior: 409 unless acknowledgeDrift=true).
    /// </summary>
    public static async Task<IReadOnlyList<DriftEntry>> DetectResolutionDriftAsync(
        CodeFlowDbContext dbContext,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, int> expectedMaxVersions,
        CancellationToken cancellationToken)
    {
        if (expectedMaxVersions.Count == 0)
        {
            return Array.Empty<DriftEntry>();
        }

        var moved = new List<DriftEntry>();
        foreach (var (target, expected) in expectedMaxVersions)
        {
            int? currentMax;
            switch (target.Kind)
            {
                case WorkflowPackageImportResourceKind.Agent:
                    currentMax = await dbContext.Agents
                        .Where(agent => agent.Key == target.Key)
                        .Select(agent => (int?)agent.Version)
                        .MaxAsync(cancellationToken);
                    break;
                case WorkflowPackageImportResourceKind.Workflow:
                    currentMax = await dbContext.Workflows
                        .Where(workflow => workflow.Key == target.Key)
                        .Select(workflow => (int?)workflow.Version)
                        .MaxAsync(cancellationToken);
                    break;
                default:
                    // Drift-ack only applies to versioned kinds; unversioned resolutions
                    // (Skill / Role / McpServer / AgentRoleAssignment) ignore expectedMax.
                    continue;
            }

            if (currentMax != expected)
            {
                moved.Add(new DriftEntry(target.Kind, target.Key, target.SourceVersion, expected, currentMax));
            }
        }
        return moved;
    }

    public static DriftConflictResponse BuildDriftResponse(IReadOnlyList<DriftEntry> moved)
    {
        var summary = string.Join(", ", moved
            .Select(entry => $"{entry.Kind} '{entry.Key}' moved from v{entry.ExpectedExistingMaxVersion} to v{(entry.CurrentExistingMaxVersion?.ToString() ?? "<none>")}")
            .Take(5));
        var more = moved.Count > 5 ? $" (+{moved.Count - 5} more)" : string.Empty;
        return new DriftConflictResponse(
            Error: "Library has moved between preview and apply: " + summary + more
                + ". Re-run the preview to see the current state, or set `acknowledgeDrift: true` to apply against the new max versions.",
            MovedEntities: moved);
    }

    public static IResult ImportValidationProblem(WorkflowPackageResolutionException exception)
    {
        if (exception.ValidationErrors.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["package"] = new[] { exception.Message }
            });
        }

        var problems = new Dictionary<string, string[]>
        {
            ["package"] = new[]
            {
                "Workflow package import failed validation. Fix the listed workflow errors and retry."
            }
        };

        foreach (var group in exception.ValidationErrors.GroupBy(error => error.WorkflowKey, StringComparer.Ordinal))
        {
            problems[$"workflows.{group.Key}"] = group
                .Select(FormatValidationError)
                .ToArray();
        }

        return Results.ValidationProblem(problems);
    }

    /// <summary>
    /// Convert an unexpected exception that escaped the structured-validation paths into a
    /// ProblemDetails the chat-panel can render. Without this any non-WorkflowPackageResolutionException
    /// would either bubble to ASP.NET's default exception handler (500 / generic 400 with no body)
    /// or be swallowed by middleware — either way the chip shows "did not return validation
    /// details" and the user has no path forward.
    /// </summary>
    public static IResult UnhandledImportProblem(Exception exception)
    {
        var detail = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {exception.Message}";
        return Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["package"] = new[]
                {
                    "Package import failed with an unexpected error. "
                    + "This usually means the package shape was rejected at apply time on a rule the "
                    + "preview validator did not catch (a typed-deserialization failure, for example). "
                    + $"Details: {detail}",
                },
            },
            title: "Package import failed.");
    }

    private static string FormatValidationError(WorkflowPackageValidationError error)
    {
        if (error.RuleIds is not { Count: > 0 })
        {
            return error.Message;
        }

        return $"{error.Message} (rules: {string.Join(", ", error.RuleIds)})";
    }
}

/// <summary>sc-395: per-conflict resolution carried over the wire. Mirrors the domain
/// <see cref="WorkflowPackageImportResolution"/> shape but flattens the Target record into
/// individual fields (the System.Text.Json default serializer doesn't round-trip nested
/// records as dictionary keys cleanly, and a flat shape is friendlier to TypeScript). The
/// optional <see cref="ExpectedExistingMaxVersion"/> is the value the client saw in the
/// preview's <c>existingMaxVersion</c> when it picked this resolution; the apply endpoint
/// re-reads the live max and rejects with 409 if they differ unless the client sets
/// <c>acknowledgeDrift: true</c>. Mirrors the <c>POST /api/agents/{key}/publish</c>
/// drift-ack shape from in-place agent edit.</summary>
public sealed record WorkflowPackageImportResolutionRequest(
    WorkflowPackageImportResourceKind Kind,
    string Key,
    int? SourceVersion,
    WorkflowPackageImportResolutionMode Mode,
    string? NewKey = null,
    int? ExpectedExistingMaxVersion = null);

/// <summary>sc-395: 409 body returned when one or more resolved entities have moved between
/// preview and apply. Each <see cref="DriftEntry"/> tells the client which entity moved and
/// where it is now so the imports page can re-preview, surface the moved row, and let the
/// user re-confirm with <c>acknowledgeDrift: true</c>.</summary>
public sealed record DriftConflictResponse(
    string Error,
    IReadOnlyList<DriftEntry> MovedEntities);

public sealed record DriftEntry(
    WorkflowPackageImportResourceKind Kind,
    string Key,
    int? SourceVersion,
    int? ExpectedExistingMaxVersion,
    int? CurrentExistingMaxVersion);
