using System.Net;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;
using Octokit;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class GitHubErrorMappingTests
{
    [Fact]
    public void Translate_maps_NotFoundException_to_VcsRepoNotFoundException()
    {
        var ex = new NotFoundException("not found", HttpStatusCode.NotFound);

        var result = GitHubErrorMapping.Translate(ex, "acme", "widget");

        result.Should().BeOfType<VcsRepoNotFoundException>()
            .Which.Owner.Should().Be("acme");
    }

    [Fact]
    public void Translate_maps_AuthorizationException_to_VcsUnauthorizedException()
    {
        var ex = new AuthorizationException(new ApiResponseStub(HttpStatusCode.Unauthorized, "bad token"));

        var result = GitHubErrorMapping.Translate(ex, "acme", "widget");

        result.Should().BeOfType<VcsUnauthorizedException>();
    }

    [Fact]
    public void Translate_maps_401_ApiException_to_VcsUnauthorizedException()
    {
        var ex = new ApiException("unauthorized", HttpStatusCode.Unauthorized);

        var result = GitHubErrorMapping.Translate(ex, "acme", "widget");

        result.Should().BeOfType<VcsUnauthorizedException>();
    }

    [Fact]
    public void Translate_maps_403_ApiException_to_VcsUnauthorizedException()
    {
        var ex = new ApiException("forbidden", HttpStatusCode.Forbidden);

        var result = GitHubErrorMapping.Translate(ex, "acme", "widget");

        result.Should().BeOfType<VcsUnauthorizedException>();
    }

    [Fact]
    public void Translate_maps_409_ApiException_to_VcsConflictException()
    {
        var ex = new ApiException("conflict", HttpStatusCode.Conflict);

        var result = GitHubErrorMapping.Translate(ex, "acme", "widget");

        result.Should().BeOfType<VcsConflictException>();
    }

    [Fact]
    public void Translate_maps_ApiValidationException_to_VcsConflictException()
    {
        var ex = new ApiValidationException(new ApiResponseStub(
            HttpStatusCode.UnprocessableEntity,
            "{\"message\":\"Validation Failed\",\"errors\":[{\"message\":\"A pull request already exists for acme:branch.\"}]}"));

        var result = GitHubErrorMapping.Translate(ex, "acme", "widget");

        result.Should().BeOfType<VcsConflictException>();
    }

    [Fact]
    public void Translate_maps_other_exception_to_wrapped_github_api_exception()
    {
        var ex = new InvalidOperationException("boom");

        var result = GitHubErrorMapping.Translate(ex, "acme", "widget");

        result.Should().BeOfType<GitHubErrorMapping.GitHubApiException>()
            .Which.InnerException.Should().BeSameAs(ex);
    }

    private sealed class ApiResponseStub : IResponse
    {
        public ApiResponseStub(HttpStatusCode statusCode, string body)
        {
            StatusCode = statusCode;
            Body = body;
        }

        public object? Body { get; }
        public IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
        public ApiInfo? ApiInfo { get; } = null;
        public HttpStatusCode StatusCode { get; }
        public string? ContentType { get; } = "application/json";
    }
}
