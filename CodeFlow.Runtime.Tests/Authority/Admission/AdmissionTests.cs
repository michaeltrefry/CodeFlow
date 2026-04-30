using CodeFlow.Runtime.Authority.Admission;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Authority.Admission;

/// <summary>
/// sc-272 PR1 — covers the foundation <see cref="Admission{T}"/> shape so the contract
/// stays stable as more boundary types land. Per-boundary validators have their own test
/// fixtures.
/// </summary>
public sealed class AdmissionTests
{
    [Fact]
    public void Accept_ProducesAcceptedCarryingTheValue()
    {
        var sample = new SampleAdmitted("ok");

        var admission = Admission<SampleAdmitted>.Accept(sample);

        admission.IsAccepted.Should().BeTrue();
        admission.Should().BeOfType<Accepted<SampleAdmitted>>()
            .Which.Value.Should().BeSameAs(sample);
    }

    [Fact]
    public void Reject_ProducesRejectedCarryingTheReason()
    {
        var rejection = new Rejection(
            Code: "test-code",
            Reason: "test reason",
            Axis: "test-axis");

        var admission = Admission<SampleAdmitted>.Reject(rejection);

        admission.IsAccepted.Should().BeFalse();
        admission.Should().BeOfType<Rejected<SampleAdmitted>>()
            .Which.Reason.Should().BeSameAs(rejection);
    }

    [Fact]
    public void Accept_WithNullValue_Throws()
    {
        var act = () => Admission<SampleAdmitted>.Accept(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Reject_WithNullRejection_Throws()
    {
        var act = () => Admission<SampleAdmitted>.Reject(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private sealed record SampleAdmitted(string Marker);
}
