using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class GitCredentialEnvTests
{
    [Fact]
    public void Build_NullOrEmptyRoot_ReturnsEmptyDictionary()
    {
        GitCredentialEnv.Build(null, Guid.NewGuid()).Should().BeEmpty();
        GitCredentialEnv.Build("", Guid.NewGuid()).Should().BeEmpty();
        GitCredentialEnv.Build("   ", Guid.NewGuid()).Should().BeEmpty();
    }

    [Fact]
    public void Build_SetsAdHocConfigForCredentialStoreHelper_PointedAtPerTraceFile()
    {
        var traceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var root = "/var/lib/codeflow/git-creds";

        var env = GitCredentialEnv.Build(root, traceId);

        // Three entries: empty `credential.helper` to reset any inherited helper chain
        // (osxkeychain on macOS, libsecret/wincred elsewhere), then our store helper, then
        // useHttpPath. Order matters — the reset must come before the store entry.
        env.Should().ContainKey("GIT_CONFIG_COUNT").WhoseValue.Should().Be("3");
        env.Should().ContainKey("GIT_CONFIG_KEY_0").WhoseValue.Should().Be("credential.helper");
        env.Should().ContainKey("GIT_CONFIG_VALUE_0")
            .WhoseValue.Should().BeEmpty("an empty helper string clears the inherited helper list");
        env.Should().ContainKey("GIT_CONFIG_KEY_1").WhoseValue.Should().Be("credential.helper");
        env.Should().ContainKey("GIT_CONFIG_VALUE_1")
            .WhoseValue.Should().Be("store --file=\"/var/lib/codeflow/git-creds/11111111222233334444555555555555\"");
        env.Should().ContainKey("GIT_CONFIG_KEY_2").WhoseValue.Should().Be("credential.useHttpPath");
        env.Should().ContainKey("GIT_CONFIG_VALUE_2").WhoseValue.Should().Be("true");
    }

    [Fact]
    public void Build_PathPointsAt_GitCredentialFile_BuildPath_ForTheSameTraceId()
    {
        var traceId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "creds-test");

        var env = GitCredentialEnv.Build(root, traceId);

        var expected = $"store --file=\"{GitCredentialFile.BuildPath(root, traceId)}\"";
        env["GIT_CONFIG_VALUE_1"].Should().Be(expected,
            "the env builder and the file writer must agree on the per-trace path or git can't find the cred file");
    }

    [Fact]
    public void Build_DifferentTraceIds_ProduceDifferentHelperPaths()
    {
        var root = "/var/lib/codeflow/git-creds";
        var trace1 = Guid.NewGuid();
        var trace2 = Guid.NewGuid();

        var env1 = GitCredentialEnv.Build(root, trace1);
        var env2 = GitCredentialEnv.Build(root, trace2);

        env1["GIT_CONFIG_VALUE_1"].Should().NotBe(env2["GIT_CONFIG_VALUE_1"],
            "concurrent traces in the same worker must each see their own cred file path");
    }
}
