using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class AgentsEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public AgentsEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task PostThenPut_CreatesNewVersion()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/agents", new
        {
            key = "reviewer-v1",
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Review the draft." }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var update = await client.PutAsJsonAsync("/api/agents/reviewer-v1", new
        {
            config = new { provider = "openai", model = "gpt-5.4", systemPrompt = "Review more carefully." }
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var versions = await client.GetFromJsonAsync<IReadOnlyList<VersionDto>>("/api/agents/reviewer-v1/versions");
        versions.Should().NotBeNull();
        versions!.Should().HaveCount(2);
        versions.Select(v => v.Version).Should().BeEquivalentTo(new[] { 2, 1 }, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Post_InvalidConfig_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/agents", new
        {
            key = "bad-agent",
            config = new { provider = "banana", model = "x" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_UnknownKey_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/agents/never-created", new
        {
            config = new { provider = "openai", model = "gpt-5" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retire_HidesFromListButKeepsVersionsAccessible()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/agents", new
        {
            key = "retire-me",
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Retire me." }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var retire = await client.PostAsync("/api/agents/retire-me/retire", content: null);
        retire.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<IReadOnlyList<SummaryDto>>("/api/agents");
        list.Should().NotBeNull();
        list!.Select(s => s.Key).Should().NotContain("retire-me");

        var version = await client.GetFromJsonAsync<VersionDetailDto>("/api/agents/retire-me/1");
        version.Should().NotBeNull();
        version!.IsRetired.Should().BeTrue();

        var versions = await client.GetFromJsonAsync<IReadOnlyList<VersionDto>>("/api/agents/retire-me/versions");
        versions.Should().NotBeNull();
        versions!.Should().HaveCount(1);

        var update = await client.PutAsJsonAsync("/api/agents/retire-me", new
        {
            config = new { provider = "openai", model = "gpt-5" }
        });
        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Retire_UnknownKey_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/agents/never-existed/retire", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RenderPreview_Hitl_ShouldRenderTemplateWithFieldValuesAndContext()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "[{{ decision }}] {{ input.feedback }} // ctx={{ context.headline }}",
            mode = "hitl",
            decision = "Approved",
            outputPortName = "Approved",
            fieldValues = new Dictionary<string, object>
            {
                ["feedback"] = "shipped"
            },
            context = new Dictionary<string, object>
            {
                ["headline"] = "Ready to go"
            }
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PreviewResponse>();
        payload!.Rendered.Should().Be("[Approved] shipped // ctx=Ready to go");
    }

    [Fact]
    public async Task RenderPreview_Llm_ShouldExposeOutputAsStructuredJson()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "{{ decision }}: {{ output.headline }} -> {{ outputPortName }}",
            mode = "llm",
            decision = "Approved",
            outputPortName = "Approved",
            output = """{"headline":"lede"}"""
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PreviewResponse>();
        payload!.Rendered.Should().Be("Approved: lede -> Approved");
    }

    [Fact]
    public async Task RenderPreview_MalformedTemplate_Returns422()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "{{ if unterminated",
            decision = "Approved",
            outputPortName = "Approved"
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadFromJsonAsync<PreviewErrorResponse>();
        payload!.Error.Should().Contain("syntax");
    }

    [Fact]
    public async Task RenderPreview_EmptyTemplate_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "",
            decision = "Approved",
            outputPortName = "Approved"
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record VersionDto(string Key, int Version, DateTime CreatedAtUtc, string? CreatedBy);

    private sealed record SummaryDto(string Key, int LatestVersion, bool IsRetired);

    private sealed record VersionDetailDto(string Key, int Version, bool IsRetired);

    private sealed record PreviewResponse(string Rendered);

    private sealed record PreviewErrorResponse(string Error);
}
