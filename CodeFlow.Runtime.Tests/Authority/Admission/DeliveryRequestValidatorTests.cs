using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Authority.Admission;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Authority.Admission;

/// <summary>
/// sc-272 PR2 — exercises <see cref="DeliveryRequestValidator"/> on its own. The
/// validator-then-execute flow inside <see cref="VcsHostToolService"/> is covered by
/// the existing <c>VcsHostToolServiceTests</c> (one fixture updated to assert the new
/// refusal shape); this file covers each rejection axis + the minted
/// <see cref="AuthorizedDeliveryRequest"/> contract directly.
/// </summary>
public sealed class DeliveryRequestValidatorTests
{
    [Fact]
    public void Validate_HappyPath_TraceContextMatch_AdmitsWithIdentityKey()
    {
        var url = "https://github.com/foo/bar.git";
        var identityKey = RepoReference.Parse(url).IdentityKey;
        var validator = new DeliveryRequestValidator();

        var admission = validator.Validate(NewRequest(
            owner: "foo",
            name: "bar",
            context: ContextWithRepo("foo", "bar", url, identityKey)));

        var admitted = admission.Should().BeOfType<Accepted<AuthorizedDeliveryRequest>>().Subject.Value;
        admitted.Owner.Should().Be("foo");
        admitted.Name.Should().Be("bar");
        admitted.RepoIdentityKey.Should().Be(identityKey);
        admitted.Body.Should().Be(string.Empty);
    }

    [Fact]
    public void Validate_HappyPath_WorkspaceRepoUrlMatch_AdmitsWithDerivedIdentityKey()
    {
        var validator = new DeliveryRequestValidator();
        var url = "https://github.com/foo/bar.git";

        var admission = validator.Validate(NewRequest(
            owner: "foo",
            name: "bar",
            context: new ToolExecutionContext(
                Workspace: new ToolWorkspaceContext(Guid.NewGuid(), "/tmp/work", RepoUrl: url))));

        var admitted = admission.Should().BeOfType<Accepted<AuthorizedDeliveryRequest>>().Subject.Value;
        admitted.RepoIdentityKey.Should().Be(RepoReference.Parse(url).IdentityKey);
    }

    [Fact]
    public void Validate_RepoNotDeclared_RejectsWithDeliveryRepoNotDeclared()
    {
        var validator = new DeliveryRequestValidator();

        var admission = validator.Validate(NewRequest(
            owner: "foo",
            name: "bar",
            context: ContextWithRepo("other", "repo", "https://github.com/other/repo.git")));

        var rejected = admission.Should().BeOfType<Rejected<AuthorizedDeliveryRequest>>().Subject;
        rejected.Reason.Code.Should().Be("delivery-repo-not-declared");
        rejected.Reason.Axis.Should().Be("delivery");
        rejected.Reason.Path.Should().Be("foo/bar");
    }

    [Fact]
    public void Validate_EnvelopeRepoScopesOnlyRead_RejectsWriteRequest()
    {
        var validator = new DeliveryRequestValidator();
        var url = "https://github.com/foo/bar.git";
        var identityKey = RepoReference.Parse(url).IdentityKey;
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            RepoScopes = new[]
            {
                new RepoScopeGrant(identityKey, "github.com/foo/bar", RepoAccess.Read),
            },
        };

        var admission = validator.Validate(NewRequest(
            owner: "foo",
            name: "bar",
            context: ContextWithRepo("foo", "bar", url, identityKey, envelope)));

        var rejected = admission.Should().BeOfType<Rejected<AuthorizedDeliveryRequest>>().Subject;
        rejected.Reason.Code.Should().Be("envelope-repo-scope");
        rejected.Reason.Axis.Should().Be(BlockedBy.Axes.RepoScopes);
    }

    [Fact]
    public void Validate_EnvelopeRepoScopesGrantsWrite_Admits()
    {
        var validator = new DeliveryRequestValidator();
        var url = "https://github.com/foo/bar.git";
        var identityKey = RepoReference.Parse(url).IdentityKey;
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            RepoScopes = new[]
            {
                new RepoScopeGrant(identityKey, "github.com/foo/bar", RepoAccess.Write),
            },
        };

        var admission = validator.Validate(NewRequest(
            owner: "foo",
            name: "bar",
            context: ContextWithRepo("foo", "bar", url, identityKey, envelope)));

        admission.Should().BeOfType<Accepted<AuthorizedDeliveryRequest>>();
    }

    [Fact]
    public void Validate_EnvelopeDeliveryMismatchOnRepo_Rejects()
    {
        var validator = new DeliveryRequestValidator();
        var url = "https://github.com/foo/bar.git";
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            Delivery = new DeliveryTarget("foo", "bar", "main"),
        };

        var admission = validator.Validate(NewRequest(
            owner: "foo",
            name: "different",
            head: "feat/x",
            baseBranch: "main",
            context: ContextWithRepo("foo", "different", "https://github.com/foo/different.git", envelope: envelope)));

        var rejected = admission.Should().BeOfType<Rejected<AuthorizedDeliveryRequest>>().Subject;
        rejected.Reason.Code.Should().Be("envelope-delivery");
        rejected.Reason.Axis.Should().Be(BlockedBy.Axes.Delivery);
    }

    [Fact]
    public void Validate_EnvelopeDeliveryMismatchOnBranch_Rejects()
    {
        var validator = new DeliveryRequestValidator();
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            Delivery = new DeliveryTarget("foo", "bar", "main"),
        };

        var admission = validator.Validate(NewRequest(
            owner: "foo",
            name: "bar",
            baseBranch: "develop",
            context: ContextWithRepo("foo", "bar", "https://github.com/foo/bar.git", envelope: envelope)));

        admission.Should().BeOfType<Rejected<AuthorizedDeliveryRequest>>()
            .Which.Reason.Code.Should().Be("envelope-delivery");
    }

    [Fact]
    public void Validate_EnvelopeDeliveryMatches_Admits()
    {
        var validator = new DeliveryRequestValidator();
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            Delivery = new DeliveryTarget("foo", "bar", "main"),
        };

        var admission = validator.Validate(NewRequest(
            owner: "foo",
            name: "bar",
            baseBranch: "main",
            context: ContextWithRepo("foo", "bar", "https://github.com/foo/bar.git", envelope: envelope)));

        admission.Should().BeOfType<Accepted<AuthorizedDeliveryRequest>>();
    }

    [Fact]
    public void Validate_EmptyOwner_RejectsWithArgMissing()
    {
        var validator = new DeliveryRequestValidator();

        var admission = validator.Validate(NewRequest(
            owner: "",
            name: "bar",
            context: ContextWithRepo("foo", "bar", "https://github.com/foo/bar.git")));

        admission.Should().BeOfType<Rejected<AuthorizedDeliveryRequest>>()
            .Which.Reason.Code.Should().Be("delivery-arg-missing");
    }

    [Fact]
    public void Validate_ReMint_SecondCallWithSameInputProducesEquivalentAdmittedValue()
    {
        var fixedNow = DateTimeOffset.Parse("2026-04-30T14:00:00Z");
        var validator = new DeliveryRequestValidator(nowProvider: () => fixedNow);
        var url = "https://github.com/foo/bar.git";
        var request = NewRequest(
            owner: "foo",
            name: "bar",
            context: ContextWithRepo("foo", "bar", url));

        var first = validator.Validate(request);
        var second = validator.Validate(request);

        var firstAdmitted = first.Should().BeOfType<Accepted<AuthorizedDeliveryRequest>>().Subject.Value;
        var secondAdmitted = second.Should().BeOfType<Accepted<AuthorizedDeliveryRequest>>().Subject.Value;
        secondAdmitted.Owner.Should().Be(firstAdmitted.Owner);
        secondAdmitted.Name.Should().Be(firstAdmitted.Name);
        secondAdmitted.Head.Should().Be(firstAdmitted.Head);
        secondAdmitted.BaseBranch.Should().Be(firstAdmitted.BaseBranch);
        secondAdmitted.Title.Should().Be(firstAdmitted.Title);
        secondAdmitted.AdmittedAt.Should().Be(firstAdmitted.AdmittedAt);
    }

    private static DeliveryAdmissionRequest NewRequest(
        string owner = "foo",
        string name = "bar",
        string head = "feat/x",
        string baseBranch = "main",
        string title = "Add x",
        string? body = null,
        ToolExecutionContext? context = null) =>
        new(owner, name, head, baseBranch, title, body, context);

    private static ToolExecutionContext ContextWithRepo(
        string owner,
        string name,
        string url,
        string? identityKey = null,
        WorkflowExecutionEnvelope? envelope = null)
    {
        identityKey ??= RepoReference.Parse(url).IdentityKey;
        return new ToolExecutionContext(
            Repositories: new[]
            {
                new ToolRepositoryContext(owner, name, url, identityKey, $"{owner}/{name}"),
            },
            Envelope: envelope);
    }
}
