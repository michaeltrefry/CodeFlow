using FluentAssertions;

namespace CodeFlow.Architecture.Tests;

/// <summary>
/// Static authority-boundary tests. Each test asserts a single architectural invariant by
/// scanning source files for forbidden substrings. Inspired by Protostar's
/// no-fs.contract.test.ts / no-net.contract.test.ts / no-merge.contract.test.ts pattern
/// (sc-276 / epic 268).
///
/// When a test fires, prefer fixing the root cause over weakening the rule. If a violation
/// is intentional, document the exception in code near the call site, then narrow the rule
/// (e.g., add an excluded path fragment) rather than removing the test.
/// </summary>
public sealed class AuthorityBoundaryTests
{
    [Fact]
    public void DeliveryCode_ShouldNotContainMergeOperations()
    {
        // Borrowed verbatim from Protostar's no-merge.contract.test.ts. CodeFlow's VCS host
        // tools open PRs — they MUST NOT merge. Any future delivery surface inherits this
        // rule. If a workflow needs auto-merge it requires explicit out-of-band approval and
        // a deliberate carve-out documented in code.
        var forbiddenMergeApiCalls = new[]
        {
            "pulls.Merge",
            "pulls.merge",
            "pullRequests.Merge",
            "pullRequests.merge",
            "EnableAutoMerge",
            "enableAutoMerge",
            "merge_method",
            "MergeMethod",
            "pulls.UpdateBranch",
            "pulls.updateBranch",
            "gh pr merge"
        };

        var matches = SourceScanner.Scan(
            projectRelativeRoot: "CodeFlow.Runtime/Workspace",
            forbiddenPatterns: forbiddenMergeApiCalls);

        matches.Should().BeEmpty(
            "VCS host tooling must open pull requests, never merge them. " +
            "Found: " + Render(matches));
    }

    [Fact]
    public void ReplayExtraction_ShouldNotMutateWorkspace()
    {
        // Replay extraction reads recorded decisions and synthesizes mock bundles for
        // dry-run executors. It must not touch the live workspace; otherwise replay can
        // accidentally apply patches or run commands that the original trace already
        // committed/executed.
        var forbiddenMutationApiCalls = new[]
        {
            "WorkspaceHostToolService",
            "ApplyPatchAsync",
            "RunCommandAsync",
            "File.WriteAllText",
            "File.WriteAllBytes",
            "File.WriteAllLines",
            "File.AppendAllText",
            "File.AppendAllLines",
            "File.Delete",
            "File.Move",
            "File.Copy",
            "Directory.CreateDirectory",
            "Directory.Delete",
            "Directory.Move"
        };

        var matches = SourceScanner.Scan(
            projectRelativeRoot: "CodeFlow.Orchestration/Replay",
            forbiddenPatterns: forbiddenMutationApiCalls);

        matches.Should().BeEmpty(
            "Replay extraction must remain read-only. " +
            "If you need to land changes during replay, do it through a saga/host tool path, " +
            "not directly. Found: " + Render(matches));
    }

    [Fact]
    public void WorkflowValidation_AndDataflow_ShouldNotPerformNetworkOrFilesystemMutation()
    {
        // Validation and dataflow analysis are pure-ish: they parse, lint, and trace
        // workflow definitions. They must not mutate disk or hit the network. Any persistence
        // or network call belongs in a service called from validation, not embedded inside it.
        var forbiddenIo = new[]
        {
            "HttpClient",
            "WebClient",
            "File.WriteAllText",
            "File.WriteAllBytes",
            "File.WriteAllLines",
            "File.AppendAllText",
            "File.AppendAllLines",
            "File.Delete",
            "File.Move",
            "Directory.CreateDirectory",
            "Directory.Delete",
            "Directory.Move",
            "Process.Start",
            "ProcessStartInfo"
        };

        var validationMatches = SourceScanner.Scan(
            projectRelativeRoot: "CodeFlow.Api/Validation",
            forbiddenPatterns: forbiddenIo);

        var dataflowMatches = SourceScanner.Scan(
            projectRelativeRoot: "CodeFlow.Orchestration/Scripting",
            forbiddenPatterns: forbiddenIo);

        validationMatches.Should().BeEmpty(
            "Workflow validation must remain side-effect-free. Found: " + Render(validationMatches));
        dataflowMatches.Should().BeEmpty(
            "Workflow dataflow analysis must remain side-effect-free. Found: " + Render(dataflowMatches));
    }

    [Fact]
    public void Orchestration_ShouldNotImportProviderSdks()
    {
        // Provider SDKs (Anthropic, OpenAI) live behind IAgentInvoker / runtime adapters.
        // Orchestration must not directly couple to a vendor SDK; that defeats provider
        // swapping and the assistant/runtime split.
        //
        // CodeFlow.Api may import these directly because the homepage assistant binds to
        // the SDKs explicitly (per LLM-streaming convention). This rule scopes to
        // CodeFlow.Orchestration only.
        var forbiddenSdkImports = new[]
        {
            "using OpenAI;",
            "using OpenAI.",
            "using Anthropic;",
            "using Anthropic."
        };

        var matches = SourceScanner.Scan(
            projectRelativeRoot: "CodeFlow.Orchestration",
            forbiddenPatterns: forbiddenSdkImports);

        matches.Should().BeEmpty(
            "CodeFlow.Orchestration must invoke agents through IAgentInvoker, not provider SDKs. " +
            "Found: " + Render(matches));
    }

    [Fact]
    public void Contracts_ShouldNotDependOnHigherLayers()
    {
        // CodeFlow.Contracts is the bottom of the dependency stack. It must not import API,
        // Orchestration, Persistence, or Host types. Project references already enforce this
        // at build time; this test catches it earlier and gives a better error than a
        // missing-reference compile failure when someone hand-edits a using statement.
        var forbiddenUsings = new[]
        {
            "using CodeFlow.Api",
            "using CodeFlow.Persistence",
            "using CodeFlow.Orchestration",
            "using CodeFlow.Host"
        };

        var matches = SourceScanner.Scan(
            projectRelativeRoot: "CodeFlow.Contracts",
            forbiddenPatterns: forbiddenUsings);

        matches.Should().BeEmpty(
            "CodeFlow.Contracts must not depend on higher layers. Found: " + Render(matches));
    }

    private static string Render(IReadOnlyList<SourceMatch> matches)
    {
        if (matches.Count == 0)
        {
            return "(no matches)";
        }

        return Environment.NewLine + string.Join(Environment.NewLine, matches.Select(m => "  " + m));
    }
}
