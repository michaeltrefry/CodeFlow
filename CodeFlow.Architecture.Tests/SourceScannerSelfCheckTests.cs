using FluentAssertions;

namespace CodeFlow.Architecture.Tests;

/// <summary>
/// Self-check tests for <see cref="SourceScanner"/>. A green-but-broken scanner is the worst
/// failure mode for these architectural tests — it would silently approve real violations.
/// These tests prove the scanner actually detects matches, skips comments, and skips
/// build output.
/// </summary>
public sealed class SourceScannerSelfCheckTests
{
    [Fact]
    public void Scan_FindsForbiddenStringsInActiveCode()
    {
        // The architectural-rule test files literally contain the forbidden patterns as
        // string literals in their forbidden-pattern arrays. Scanning the test project for
        // those literals must find them — which proves the scanner walks files, splits lines,
        // and substring-matches as expected.
        var matches = SourceScanner.Scan(
            projectRelativeRoot: "CodeFlow.Architecture.Tests",
            forbiddenPatterns: new[] { "pulls.Merge" });

        matches.Should().NotBeEmpty(
            "AuthorityBoundaryTests.cs contains 'pulls.Merge' as a forbidden-pattern string literal; " +
            "scanner must find it. If this fails, every other architectural assertion is silently broken.");
    }

    [Fact]
    public void Scan_SkipsCommentLines()
    {
        // Plant the marker only inside a comment in this file. The scanner must NOT report it.
        // The sentinel literal is split below so the only place the *concatenated* string
        // exists in this file is in this comment line.
        // sentinel-marker-do-not-touch-yyKKbA
        var sentinel = string.Concat("sentinel-marker", "-do-not-touch-yyKKbA");

        var matches = SourceScanner.Scan(
            projectRelativeRoot: "CodeFlow.Architecture.Tests",
            forbiddenPatterns: new[] { sentinel });

        matches.Should().BeEmpty(
            "Sentinel only appears inside a // comment; scanner must skip comment lines.");
    }

    [Fact]
    public void Scan_SkipsBuildOutput()
    {
        // After a build, copies of source files exist under bin/ as compiled artifacts and
        // sometimes embedded debug info. The scanner must skip bin/ and obj/. Verify by
        // scanning for a string that appears in compiled output if anywhere — we just check
        // that an extremely common token isn't double-counted from build folders by scanning
        // a path under the test project that has bin/ and obj/ siblings.
        var matches = SourceScanner.Scan(
            projectRelativeRoot: "CodeFlow.Architecture.Tests",
            forbiddenPatterns: new[] { "namespace CodeFlow.Architecture.Tests" });

        matches.Should().OnlyContain(
            m => !m.RelativePath.Contains("/bin/", StringComparison.Ordinal)
                && !m.RelativePath.Contains("/obj/", StringComparison.Ordinal),
            "scanner must skip bin/ and obj/ artifact directories.");
    }
}
