using CodeFlow.Api.Assistant.Tools;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// CE-3 / N=1: the carrier tracker maintains an at-most-one full-payload invariant on
/// workflow-package transcripts. Each new <see cref="WorkflowPackageCarrierTracker.Replace"/>
/// call demotes the prior carrier (regardless of provider or direction) before registering the
/// new one. These tests verify the tracker's call ordering — they do NOT verify integration
/// with the Anthropic / OpenAI message paths (covered indirectly by the assistant integration
/// tests).
/// </summary>
public sealed class WorkflowPackageCarrierTrackerTests
{
    [Fact]
    public void Replace_FirstCall_DoesNotInvokeAnyDemote()
    {
        var tracker = new WorkflowPackageCarrierTracker();
        var calls = new List<string>();

        tracker.Replace(() => calls.Add("first"));

        // Nothing demoted because nothing was registered before.
        calls.Should().BeEmpty();
    }

    [Fact]
    public void Replace_SecondCall_DemotesFirstCarrier()
    {
        var tracker = new WorkflowPackageCarrierTracker();
        var calls = new List<string>();

        tracker.Replace(() => calls.Add("first-demoted"));
        tracker.Replace(() => calls.Add("second-demoted"));

        // Registering "second" demotes "first". "second" stays full.
        calls.Should().Equal("first-demoted");
    }

    [Fact]
    public void Replace_ThirdCall_DemotesSecondCarrierNotFirst()
    {
        var tracker = new WorkflowPackageCarrierTracker();
        var calls = new List<string>();

        tracker.Replace(() => calls.Add("first-demoted"));
        tracker.Replace(() => calls.Add("second-demoted"));
        tracker.Replace(() => calls.Add("third-demoted"));

        // First was already demoted on the second Replace. Third demotes second.
        calls.Should().Equal("first-demoted", "second-demoted");
    }

    [Fact]
    public void Replace_NullDemote_Throws()
    {
        var tracker = new WorkflowPackageCarrierTracker();

        var act = () => tracker.Replace(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
