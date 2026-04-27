using CodeFlow.Api.WorkflowTemplates;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class WorkflowTemplatesEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public WorkflowTemplatesEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Get_ListsRegisteredTemplates()
    {
        // S3 framework boots with the EmptyWorkflow stub registered. Confirm the listing
        // surface exposes it with the metadata the editor renders in the picker.
        using var client = factory.CreateClient();

        var templates = (await client.GetFromJsonAsync<IReadOnlyList<TemplateSummaryDto>>(
            "/api/workflow-templates"))!;

        templates.Should().NotBeEmpty();
        templates.Should().Contain(t => t.Id == WorkflowTemplateRegistry.EmptyWorkflowId);
        var empty = templates.Single(t => t.Id == WorkflowTemplateRegistry.EmptyWorkflowId);
        empty.Name.Should().NotBeNullOrEmpty();
        empty.Description.Should().NotBeNullOrEmpty();
        empty.Category.Should().Be("Empty");
    }

    [Fact]
    public async Task Get_UnknownTemplate_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/workflow-templates/ghost-template");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Materialize_ValidPrefix_CreatesAgentAndWorkflow()
    {
        // S3 acceptance: selecting a stub template materializes the entities and the response
        // gives the editor everything it needs to navigate to the new workflow.
        using var client = factory.CreateClient();
        var prefix = $"tmpl-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            $"/api/workflow-templates/{WorkflowTemplateRegistry.EmptyWorkflowId}/materialize",
            new { namePrefix = prefix });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<MaterializeResponseDto>())!;
        body.EntryWorkflowKey.Should().Be(prefix);
        body.EntryWorkflowVersion.Should().Be(1);
        body.CreatedEntities.Should().HaveCount(2);

        // Location header points at the new workflow.
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().EndWith($"/api/workflows/{prefix}/1");
    }

    [Fact]
    public async Task Materialize_BlankPrefix_Returns400()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/workflow-templates/{WorkflowTemplateRegistry.EmptyWorkflowId}/materialize",
            new { namePrefix = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Materialize_PrefixAlreadyTaken_Returns409()
    {
        // Materializing twice with the same prefix collides on the agent's unique key — the
        // endpoint surfaces the conflict instead of bubbling a 500.
        using var client = factory.CreateClient();
        var prefix = $"collide-{Guid.NewGuid():N}";

        var first = await client.PostAsJsonAsync(
            $"/api/workflow-templates/{WorkflowTemplateRegistry.EmptyWorkflowId}/materialize",
            new { namePrefix = prefix });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync(
            $"/api/workflow-templates/{WorkflowTemplateRegistry.EmptyWorkflowId}/materialize",
            new { namePrefix = prefix });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Materialize_UnknownTemplate_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/workflow-templates/ghost-template/materialize",
            new { namePrefix = "demo" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record TemplateSummaryDto(
        string Id,
        string Name,
        string Description,
        string Category);

    private sealed record MaterializeResponseDto(
        string EntryWorkflowKey,
        int EntryWorkflowVersion,
        IReadOnlyList<MaterializedEntityResponseDto> CreatedEntities);

    private sealed record MaterializedEntityResponseDto(
        string Kind,
        string Key,
        int Version);
}
