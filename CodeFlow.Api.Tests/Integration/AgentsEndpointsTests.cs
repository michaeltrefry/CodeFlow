using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task BulkRetire_HidesSelectedAgents()
    {
        using var client = factory.CreateClient();

        var keyA = $"bulk-retire-a-{Guid.NewGuid():N}";
        var keyB = $"bulk-retire-b-{Guid.NewGuid():N}";

        (await client.PostAsJsonAsync("/api/agents", new
        {
            key = keyA,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "A" }
        })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/agents", new
        {
            key = keyB,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "B" }
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var retire = await client.PostAsJsonAsync("/api/agents/retire", new
        {
            keys = new[] { keyA, keyB, "missing-agent-key" },
        });
        retire.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await retire.Content.ReadFromJsonAsync<BulkRetireDto>())!;
        result.RetiredKeys.Should().BeEquivalentTo(new[] { keyA, keyB });
        result.MissingKeys.Should().Contain("missing-agent-key");

        var list = await client.GetFromJsonAsync<IReadOnlyList<SummaryDto>>("/api/agents");
        list!.Select(a => a.Key).Should().NotContain(new[] { keyA, keyB });
    }

    [Fact]
    public async Task CreateAndGet_ShouldRoundTripNormalizedTags()
    {
        using var client = factory.CreateClient();

        var key = $"tagged-agent-{Guid.NewGuid():N}".ToLowerInvariant()[..40];
        var response = await client.PostAsJsonAsync("/api/agents", new
        {
            key,
            tags = new[] { " Ops ", "ops", "Research" },
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Tag me." }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetFromJsonAsync<IReadOnlyList<TaggedSummaryDto>>("/api/agents");
        var summary = list!.Single(agent => agent.Key == key);
        summary.Tags.Should().Equal("Ops", "Research");

        var detail = await client.GetFromJsonAsync<TaggedVersionDetailDto>($"/api/agents/{key}/1");
        detail!.Tags.Should().Equal("Ops", "Research");

        var versions = await client.GetFromJsonAsync<IReadOnlyList<TaggedVersionDto>>($"/api/agents/{key}/versions");
        versions!.Single().Tags.Should().Equal("Ops", "Research");
    }

    [Fact]
    public async Task List_WithTagFilters_ReturnsAgentsMatchingAllRequestedTags()
    {
        using var client = factory.CreateClient();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var sharedTag = $"shared-{suffix}";
        var narrowTag = $"narrow-{suffix}";
        var matchingKey = $"tag-match-{suffix}";
        var partialKey = $"tag-partial-{suffix}";

        (await client.PostAsJsonAsync("/api/agents", new
        {
            key = matchingKey,
            tags = new[] { sharedTag, narrowTag },
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Match." }
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.PostAsJsonAsync("/api/agents", new
        {
            key = partialKey,
            tags = new[] { sharedTag },
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Partial." }
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var repeated = await client.GetFromJsonAsync<IReadOnlyList<TaggedSummaryDto>>(
            $"/api/agents?tag={sharedTag}&tag={narrowTag}");
        var repeatedKeys = repeated!.Select(agent => agent.Key);
        repeatedKeys.Should().Contain(matchingKey);
        repeatedKeys.Should().NotContain(partialKey);

        var commaSeparated = await client.GetFromJsonAsync<IReadOnlyList<TaggedSummaryDto>>(
            $"/api/agents?tags={sharedTag},{narrowTag}");
        var commaSeparatedKeys = commaSeparated!.Select(agent => agent.Key);
        commaSeparatedKeys.Should().Contain(matchingKey);
        commaSeparatedKeys.Should().NotContain(partialKey);
    }

    [Fact]
    public async Task Put_WithTags_UpdatesLatestVersionTags()
    {
        using var client = factory.CreateClient();

        var key = $"tag-update-{Guid.NewGuid():N}".ToLowerInvariant()[..40];
        (await client.PostAsJsonAsync("/api/agents", new
        {
            key,
            tags = new[] { "old-tag" },
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "v1" }
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var update = await client.PutAsJsonAsync($"/api/agents/{key}", new
        {
            tags = new[] { "new-tag" },
            config = new { provider = "openai", model = "gpt-5.4", systemPrompt = "v2" }
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var latest = await client.GetFromJsonAsync<TaggedVersionDetailDto>($"/api/agents/{key}");
        latest!.Version.Should().Be(2);
        latest.Tags.Should().Equal("new-tag");

        var versions = await client.GetFromJsonAsync<IReadOnlyList<TaggedVersionDto>>($"/api/agents/{key}/versions");
        versions!.Should().ContainSingle(v => v.Version == 1 && v.Tags.SequenceEqual(new[] { "old-tag" }));
        versions.Should().ContainSingle(v => v.Version == 2 && v.Tags.SequenceEqual(new[] { "new-tag" }));
    }

    [Fact]
    public async Task List_ShouldHideWorkflowScopedForks()
    {
        using var client = factory.CreateClient();

        var librarySourceKey = $"library-{Guid.NewGuid():N}";
        var create = await client.PostAsJsonAsync("/api/agents", new
        {
            key = librarySourceKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Visible." }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var forkKey = $"__fork_{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            db.Agents.Add(new AgentConfigEntity
            {
                Key = forkKey,
                Version = 1,
                ConfigJson = """{"provider":"openai","model":"gpt-5","systemPrompt":"Hidden."}""",
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = "tester",
                IsActive = true,
                OwningWorkflowKey = $"wf-{Guid.NewGuid():N}",
                ForkedFromKey = librarySourceKey,
                ForkedFromVersion = 1
            });
            await db.SaveChangesAsync();
        }

        var list = await client.GetFromJsonAsync<IReadOnlyList<SummaryDto>>("/api/agents");
        list.Should().NotBeNull();
        list!.Select(s => s.Key).Should().Contain(librarySourceKey);
        list.Select(s => s.Key).Should().NotContain(forkKey);
    }

    [Fact]
    public async Task Fork_HappyPath_CreatesHiddenScopedAgent()
    {
        using var client = factory.CreateClient();

        var sourceKey = $"fork-src-{Guid.NewGuid():N}";
        var create = await client.PostAsJsonAsync("/api/agents", new
        {
            key = sourceKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Base." }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var workflowKey = $"wf-{Guid.NewGuid():N}";
        var fork = await client.PostAsJsonAsync("/api/agents/fork", new
        {
            sourceKey,
            sourceVersion = 1,
            workflowKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Edited in place." }
        });
        fork.StatusCode.Should().Be(HttpStatusCode.Created);
        var forkPayload = await fork.Content.ReadFromJsonAsync<ForkResponse>();
        forkPayload!.Key.Should().StartWith("__fork_");
        forkPayload.Version.Should().Be(1);
        forkPayload.ForkedFromKey.Should().Be(sourceKey);
        forkPayload.ForkedFromVersion.Should().Be(1);
        forkPayload.OwningWorkflowKey.Should().Be(workflowKey);

        var list = await client.GetFromJsonAsync<IReadOnlyList<SummaryDto>>("/api/agents");
        list.Should().NotBeNull();
        list!.Select(s => s.Key).Should().Contain(sourceKey);
        list.Select(s => s.Key).Should().NotContain(forkPayload.Key);
    }

    [Fact]
    public async Task Put_WorkflowScopedFork_CreatesNextForkVersion()
    {
        using var client = factory.CreateClient();

        var sourceKey = $"fork-update-src-{Guid.NewGuid():N}";
        var create = await client.PostAsJsonAsync("/api/agents", new
        {
            key = sourceKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Base." }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var fork = await CreateForkAsync(client, sourceKey, sourceVersion: 1, forkPrompt: "first fork edit");

        var update = await client.PutAsJsonAsync($"/api/agents/{fork.Key}", new
        {
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "second fork edit" }
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updatePayload = await update.Content.ReadFromJsonAsync<CreateResponse>();
        updatePayload!.Key.Should().Be(fork.Key);
        updatePayload.Version.Should().Be(2);

        var status = await client.GetFromJsonAsync<PublishStatusDto>($"/api/agents/{fork.Key}/publish-status");
        status!.ForkedFromKey.Should().Be(sourceKey);
        status.ForkedFromVersion.Should().Be(1);
        status.IsDrift.Should().BeFalse();

        var versions = await client.GetFromJsonAsync<IReadOnlyList<VersionDto>>($"/api/agents/{fork.Key}/versions");
        versions.Should().NotBeNull();
        versions!.Select(v => v.Version).Should().BeEquivalentTo(new[] { 2, 1 }, options => options.WithStrictOrdering());

        var list = await client.GetFromJsonAsync<IReadOnlyList<SummaryDto>>("/api/agents");
        list!.Select(s => s.Key).Should().NotContain(fork.Key);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var latestFork = await db.Agents
            .AsNoTracking()
            .SingleAsync(agent => agent.Key == fork.Key && agent.Version == 2);
        latestFork.OwningWorkflowKey.Should().Be(fork.OwningWorkflowKey);
        latestFork.ForkedFromKey.Should().Be(sourceKey);
        latestFork.ForkedFromVersion.Should().Be(1);
    }

    [Fact]
    public async Task Fork_UnknownSource_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/agents/fork", new
        {
            sourceKey = $"nope-{Guid.NewGuid():N}",
            sourceVersion = 1,
            workflowKey = "wf-any",
            config = new { provider = "openai", model = "gpt-5" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Fork_RetiredSource_Returns422()
    {
        using var client = factory.CreateClient();

        var sourceKey = $"retired-src-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/agents", new
        {
            key = sourceKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "x" }
        });
        await client.PostAsync($"/api/agents/{sourceKey}/retire", content: null);

        var response = await client.PostAsJsonAsync("/api/agents/fork", new
        {
            sourceKey,
            sourceVersion = 1,
            workflowKey = "wf-any",
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "x" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PublishStatus_DriftReflectsOriginalAdvancement()
    {
        using var client = factory.CreateClient();

        var sourceKey = $"drift-src-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/agents", new
        {
            key = sourceKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "v1" }
        });

        var fork = await CreateForkAsync(client, sourceKey, sourceVersion: 1);

        var beforeStatus = await client.GetFromJsonAsync<PublishStatusDto>($"/api/agents/{fork.Key}/publish-status");
        beforeStatus!.IsDrift.Should().BeFalse();
        beforeStatus.OriginalLatestVersion.Should().Be(1);

        await client.PutAsJsonAsync($"/api/agents/{sourceKey}", new
        {
            config = new { provider = "openai", model = "gpt-5.4", systemPrompt = "v2" }
        });

        var afterStatus = await client.GetFromJsonAsync<PublishStatusDto>($"/api/agents/{fork.Key}/publish-status");
        afterStatus!.IsDrift.Should().BeTrue();
        afterStatus.OriginalLatestVersion.Should().Be(2);
    }

    [Fact]
    public async Task Publish_ToOriginal_NoDrift_CreatesNewVersionOnOriginal()
    {
        using var client = factory.CreateClient();

        var sourceKey = $"pub-src-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/agents", new
        {
            key = sourceKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "v1" }
        });

        var fork = await CreateForkAsync(client, sourceKey, sourceVersion: 1, forkPrompt: "forked");

        var publish = await client.PostAsJsonAsync(
            $"/api/agents/{fork.Key}/publish",
            new { mode = "original" });
        publish.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await publish.Content.ReadFromJsonAsync<PublishResponseDto>();
        response!.PublishedKey.Should().Be(sourceKey);
        response.PublishedVersion.Should().Be(2);

        var versions = await client.GetFromJsonAsync<IReadOnlyList<VersionDto>>($"/api/agents/{sourceKey}/versions");
        versions!.Should().HaveCount(2);
    }

    [Fact]
    public async Task Publish_ToOriginal_WithDrift_RequiresAcknowledgement()
    {
        using var client = factory.CreateClient();

        var sourceKey = $"drift-pub-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/agents", new
        {
            key = sourceKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "v1" }
        });
        var fork = await CreateForkAsync(client, sourceKey, sourceVersion: 1);

        await client.PutAsJsonAsync($"/api/agents/{sourceKey}", new
        {
            config = new { provider = "openai", model = "gpt-5.4", systemPrompt = "upstream v2" }
        });

        var refused = await client.PostAsJsonAsync(
            $"/api/agents/{fork.Key}/publish",
            new { mode = "original" });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var accepted = await client.PostAsJsonAsync(
            $"/api/agents/{fork.Key}/publish",
            new { mode = "original", acknowledgeDrift = true });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await accepted.Content.ReadFromJsonAsync<PublishResponseDto>();
        response!.PublishedVersion.Should().Be(3);
    }

    [Fact]
    public async Task Publish_AsNewAgent_CreatesFreshAgent()
    {
        using var client = factory.CreateClient();

        var sourceKey = $"new-src-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/agents", new
        {
            key = sourceKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "v1" }
        });
        var fork = await CreateForkAsync(client, sourceKey, sourceVersion: 1);

        var newKey = $"published-{Guid.NewGuid():N}".ToLowerInvariant()[..40];
        var publish = await client.PostAsJsonAsync(
            $"/api/agents/{fork.Key}/publish",
            new { mode = "new-agent", newKey });
        publish.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await publish.Content.ReadFromJsonAsync<PublishResponseDto>();
        response!.PublishedKey.Should().Be(newKey);
        response.PublishedVersion.Should().Be(1);

        var list = await client.GetFromJsonAsync<IReadOnlyList<SummaryDto>>("/api/agents");
        list!.Select(s => s.Key).Should().Contain(newKey);
    }

    [Fact]
    public async Task Publish_AsNewAgent_ConflictOnExistingKey_Returns409()
    {
        using var client = factory.CreateClient();

        var sourceKey = $"conflict-src-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/agents", new
        {
            key = sourceKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "x" }
        });
        var fork = await CreateForkAsync(client, sourceKey, sourceVersion: 1);

        var response = await client.PostAsJsonAsync(
            $"/api/agents/{fork.Key}/publish",
            new { mode = "new-agent", newKey = sourceKey });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Publish_NonForkKey_Returns422()
    {
        using var client = factory.CreateClient();

        var key = $"plain-agent-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/agents", new
        {
            key,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "x" }
        });

        var response = await client.PostAsJsonAsync(
            $"/api/agents/{key}/publish",
            new { mode = "original" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    private static async Task<ForkResponse> CreateForkAsync(
        HttpClient client,
        string sourceKey,
        int sourceVersion,
        string? forkPrompt = null)
    {
        var workflowKey = $"wf-{Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/api/agents/fork", new
        {
            sourceKey,
            sourceVersion,
            workflowKey,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = forkPrompt ?? "forked" }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = await response.Content.ReadFromJsonAsync<ForkResponse>();
        payload.Should().NotBeNull();
        return payload!;
    }

    private sealed record ForkResponse(string Key, int Version, string ForkedFromKey, int ForkedFromVersion, string OwningWorkflowKey);

    private sealed record PublishStatusDto(string ForkedFromKey, int ForkedFromVersion, int? OriginalLatestVersion, bool IsDrift);

    private sealed record PublishResponseDto(string PublishedKey, int PublishedVersion, string ForkedFromKey, int ForkedFromVersion);

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

    private sealed record CreateResponse(string Key, int Version);

    private sealed record SummaryDto(string Key, int LatestVersion, bool IsRetired);

    private sealed record VersionDetailDto(string Key, int Version, bool IsRetired);

    private sealed record TaggedSummaryDto(string Key, int LatestVersion, IReadOnlyList<string> Tags, bool IsRetired);

    private sealed record TaggedVersionDto(string Key, int Version, IReadOnlyList<string> Tags, bool IsRetired);

    private sealed record TaggedVersionDetailDto(string Key, int Version, IReadOnlyList<string> Tags, bool IsRetired);

    private sealed record BulkRetireDto(IReadOnlyList<string> RetiredKeys, IReadOnlyList<string> MissingKeys);

    private sealed record PreviewResponse(string Rendered);

    private sealed record PreviewErrorResponse(string Error);

    [Fact]
    public async Task RenderPromptPreview_RendersSystemAndPromptAgainstWorkflowVars()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            systemPrompt = "Reviewer: {{ workflow.task }}",
            promptTemplate = "Plan:\n{{ workflow.currentPlan }}\n\nFeedback: {{ rejectionHistory }}",
            workflow = new Dictionary<string, object>
            {
                ["task"] = "review the impl plan",
                ["currentPlan"] = "draft v1"
            }
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-prompt-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PromptPreviewResponse>();
        payload.Should().NotBeNull();
        payload!.RenderedSystemPrompt.Should().Be("Reviewer: review the impl plan");
        payload.RenderedPromptTemplate.Should().Contain("Plan:\ndraft v1");
        payload.AutoInjections.Should().BeEmpty();
        payload.MissingPartials.Should().BeEmpty();
    }

    [Fact]
    public async Task RenderPromptPreview_TypoInVariableRendersVerbatim()
    {
        using var client = factory.CreateClient();

        // Acceptance: editing a reviewer prompt that references {{ wokflow.foo }} (typo) shows the
        // typo unresolved in the preview before the author runs the workflow.
        var body = new
        {
            promptTemplate = "Hello {{ wokflow.foo }}",
            workflow = new Dictionary<string, object> { ["foo"] = "world" }
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-prompt-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PromptPreviewResponse>();
        payload!.RenderedPromptTemplate.Should().Be("Hello {{ wokflow.foo }}");
    }

    [Fact]
    public async Task RenderPromptPreview_ReviewLoop_AutoInjectsLastRoundReminder()
    {
        using var client = factory.CreateClient();

        // Acceptance: auto-injected last-round-reminder is visibly annotated as [auto-injected]
        // in the preview. The endpoint surfaces it as a structured AutoInjections entry that the
        // UI renders as a labelled block.
        var body = new
        {
            promptTemplate = "Review {{ workflow.draft }}",
            workflow = new Dictionary<string, object> { ["draft"] = "the artifact" },
            reviewRound = 3,
            reviewMaxRounds = 3
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-prompt-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PromptPreviewResponse>();
        payload!.RenderedPromptTemplate.Should().Be("Review the artifact");
        payload.AutoInjections.Should().HaveCount(1);
        var injection = payload.AutoInjections[0];
        injection.Key.Should().Be("@codeflow/last-round-reminder");
        injection.RenderedBody.Should().Contain("FINAL ROUND");
    }

    [Fact]
    public async Task RenderPromptPreview_ReviewLoop_OptOutSkipsAutoInjection()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            promptTemplate = "Review",
            reviewRound = 3,
            reviewMaxRounds = 3,
            optOutLastRoundReminder = true
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-prompt-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PromptPreviewResponse>();
        payload!.AutoInjections.Should().BeEmpty();
    }

    [Fact]
    public async Task RenderPromptPreview_MalformedTemplate_Returns422()
    {
        using var client = factory.CreateClient();

        var body = new { promptTemplate = "{{ if unterminated" };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-prompt-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadFromJsonAsync<PreviewErrorResponse>();
        payload!.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RenderPromptPreview_StockPartialResolves()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            promptTemplate = "{{ include \"@codeflow/last-round-reminder\" }}",
            partialPins = new[] { new { key = "@codeflow/last-round-reminder", version = 1 } },
            reviewRound = 2,
            reviewMaxRounds = 2
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-prompt-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PromptPreviewResponse>();
        payload!.RenderedPromptTemplate.Should().Contain("FINAL ROUND");
        // Explicit include de-dups the auto-injection.
        payload.AutoInjections.Should().BeEmpty();
        payload.MissingPartials.Should().BeEmpty();
    }

    [Fact]
    public async Task RenderPromptPreview_MissingPartialIsSurfacedInErrorResponse()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            promptTemplate = "{{ include \"@codeflow/never-existed\" }}",
            partialPins = new[] { new { key = "@codeflow/never-existed", version = 1 } }
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-prompt-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadFromJsonAsync<PromptPreviewErrorResponseWithMissing>();
        payload!.MissingPartials.Should().HaveCount(1);
        payload.MissingPartials![0].Key.Should().Be("@codeflow/never-existed");
        payload.Error.Should().NotBeNullOrEmpty();
    }

    private sealed record PromptPreviewErrorResponseWithMissing(
        string Error,
        IReadOnlyList<PromptPreviewMissingPartial>? MissingPartials);

    // S2: lock in the rendered behavior of each form preset's templates so a regression in the
    // preset definitions or the renderer surfaces immediately. Templates copied verbatim from
    // codeflow-ui/src/app/pages/agents/form-presets.ts.

    [Fact]
    public async Task FormPreset_Passthrough_RendersInputArtifactUnchanged()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "{{ input }}",
            mode = "hitl",
            decision = "Approved",
            outputPortName = "Approved",
            fieldValues = new Dictionary<string, object> { ["body"] = "upstream artifact" }
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PreviewResponse>();
        // Passthrough renders the form-fields object — the runtime delivers the form's submitted
        // JSON as the artifact downstream when the template is just `{{ input }}`.
        payload!.Rendered.Should().Contain("upstream artifact");
    }

    [Theory]
    [InlineData("reviewer override", "reviewer override")]   // edited path
    [InlineData("", "original v1")]                          // empty edit falls back to workflow var
    public async Task FormPreset_EditThenApprove_PrefersEditOverWorkflowVar(string editedText, string expected)
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = """{{ if (input.editedText ?? "").size > 0 }}{{ input.editedText }}{{ else }}{{ workflow.currentPlan }}{{ end }}""",
            mode = "hitl",
            decision = "Approved",
            outputPortName = "Approved",
            fieldValues = new Dictionary<string, object> { ["editedText"] = editedText },
            workflow = new Dictionary<string, object> { ["currentPlan"] = "original v1" }
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PreviewResponse>();
        payload!.Rendered.Should().Be(expected);
    }

    [Fact]
    public async Task FormPreset_EditThenApprove_MissingFieldFallsBackToWorkflowVar()
    {
        using var client = factory.CreateClient();

        // The reviewer never expanded the textarea; `input.editedText` is absent entirely.
        // The preset's null-coalesce ensures we fall through to the workflow var.
        var body = new
        {
            template = """{{ if (input.editedText ?? "").size > 0 }}{{ input.editedText }}{{ else }}{{ workflow.currentPlan }}{{ end }}""",
            mode = "hitl",
            decision = "Approved",
            outputPortName = "Approved",
            fieldValues = new Dictionary<string, object>(),
            workflow = new Dictionary<string, object> { ["currentPlan"] = "original v1" }
        };

        var response = await client.PostAsJsonAsync("/api/agents/templates/render-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PreviewResponse>();
        payload!.Rendered.Should().Be("original v1");
    }

    private sealed record PromptPreviewResponse(
        string? RenderedSystemPrompt,
        string? RenderedPromptTemplate,
        IReadOnlyList<PromptPreviewAutoInjection> AutoInjections,
        IReadOnlyList<PromptPreviewMissingPartial> MissingPartials);

    private sealed record PromptPreviewAutoInjection(string Key, string RenderedBody, string Reason);

    private sealed record PromptPreviewMissingPartial(string Key, int Version);
}
