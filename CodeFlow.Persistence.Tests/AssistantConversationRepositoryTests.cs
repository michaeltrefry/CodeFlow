using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

/// <summary>
/// Repository-level tests for <see cref="AssistantConversationRepository"/> behaviors that
/// don't ride through the API surface — primarily the HAA-14 <c>ListByUserAsync</c> path that
/// drives the homepage resume-conversation rail.
///
/// Integration-style tests (real MariaDB via Testcontainers) so the EF query against the
/// LEFT JOIN-style projection runs against the same schema production sees.
/// </summary>
public sealed class AssistantConversationRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private string? connectionString;

    public async Task InitializeAsync()
    {
        await mariaDbContainer.StartAsync();
        connectionString = mariaDbContainer.GetConnectionString();

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task ListByUserAsync_returns_user_conversations_ordered_by_updated_desc_with_message_counts_and_previews()
    {
        // Three conversations for the same user, plus one for a different user that must NOT
        // surface. Update the timestamps deliberately so we can assert the ordering.
        var userId = $"user-{Guid.NewGuid():N}";
        var otherUserId = $"user-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AssistantConversationRepository(context);

        var convA = await repo.GetOrCreateAsync(userId, AssistantConversationScope.Homepage());
        var convB = await repo.GetOrCreateAsync(userId, AssistantConversationScope.Entity("trace", "trace-1"));
        var convC = await repo.GetOrCreateAsync(userId, AssistantConversationScope.Entity("workflow", "wf-1"));
        await repo.GetOrCreateAsync(otherUserId, AssistantConversationScope.Homepage());

        // Append messages so the preview + count fields can be validated. Different content per
        // conversation so we can verify the right preview lands on the right row.
        await repo.AppendMessageAsync(convA.Id, AssistantMessageRole.User, "  How do swarm nodes work? ", null, null, null);
        await repo.AppendMessageAsync(convA.Id, AssistantMessageRole.Assistant, "Like this…", "anthropic", "claude", Guid.NewGuid());
        await repo.AppendMessageAsync(convB.Id, AssistantMessageRole.User, "Why did this fail?", null, null, null);
        // C left empty — must still surface with MessageCount = 0 and null preview so the rail
        // renders a meaningful fallback label.

        // Stamp deterministic UpdatedAtUtc values so ordering is unambiguous: B newest, A middle,
        // C oldest. Append-message bumps UpdatedAtUtc, so we override post-hoc.
        await ForceUpdatedAtUtcAsync(convA.Id, DateTime.UtcNow.AddMinutes(-10));
        await ForceUpdatedAtUtcAsync(convB.Id, DateTime.UtcNow.AddMinutes(-2));
        await ForceUpdatedAtUtcAsync(convC.Id, DateTime.UtcNow.AddMinutes(-30));

        var summaries = await repo.ListByUserAsync(userId, limit: 10);

        summaries.Should().HaveCount(3);
        summaries.Select(s => s.Id).Should().BeEquivalentTo(
            new[] { convB.Id, convA.Id, convC.Id },
            opts => opts.WithStrictOrdering());

        // A's preview is the trimmed first user message; whitespace runs collapse to single
        // spaces (the rail treats this as a one-line label).
        var summaryA = summaries.Single(s => s.Id == convA.Id);
        summaryA.FirstUserMessagePreview.Should().Be("How do swarm nodes work?");
        summaryA.MessageCount.Should().Be(2);
        summaryA.SyntheticTraceId.Should().Be(convA.SyntheticTraceId);
        summaryA.ScopeKind.Should().Be(AssistantConversationScopeKind.Homepage);

        var summaryB = summaries.Single(s => s.Id == convB.Id);
        summaryB.FirstUserMessagePreview.Should().Be("Why did this fail?");
        summaryB.MessageCount.Should().Be(1);
        summaryB.ScopeKind.Should().Be(AssistantConversationScopeKind.Entity);
        summaryB.EntityType.Should().Be("trace");
        summaryB.EntityId.Should().Be("trace-1");

        // C — fresh conversation, no messages yet. Rail uses MessageCount = 0 + null preview to
        // suppress empty homepage rows; entity-scoped rows still render with a fallback label.
        var summaryC = summaries.Single(s => s.Id == convC.Id);
        summaryC.MessageCount.Should().Be(0);
        summaryC.FirstUserMessagePreview.Should().BeNull();
    }

    [Fact]
    public async Task ListByUserAsync_truncates_long_previews_to_120_characters()
    {
        var userId = $"user-{Guid.NewGuid():N}";
        var longContent = new string('x', 200);

        await using var context = CreateDbContext();
        var repo = new AssistantConversationRepository(context);

        var conv = await repo.GetOrCreateAsync(userId, AssistantConversationScope.Homepage());
        await repo.AppendMessageAsync(conv.Id, AssistantMessageRole.User, longContent, null, null, null);

        var summaries = await repo.ListByUserAsync(userId, limit: 5);
        summaries.Should().ContainSingle();

        // Cap is 120 chars + ellipsis; exact length is not the contract, just "trimmed and
        // shorter than the original."
        summaries[0].FirstUserMessagePreview!.Length.Should().BeLessThanOrEqualTo(125);
        summaries[0].FirstUserMessagePreview!.Should().EndWith("…");
    }

    [Fact]
    public async Task ListByUserAsync_respects_limit()
    {
        var userId = $"user-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AssistantConversationRepository(context);

        // Five entity-scoped conversations.
        for (var i = 0; i < 5; i++)
        {
            await repo.GetOrCreateAsync(userId, AssistantConversationScope.Entity("trace", $"t-{i}"));
        }

        var summaries = await repo.ListByUserAsync(userId, limit: 2);
        summaries.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListByUserAsync_returns_empty_for_unknown_user()
    {
        await using var context = CreateDbContext();
        var repo = new AssistantConversationRepository(context);

        var summaries = await repo.ListByUserAsync($"nobody-{Guid.NewGuid():N}", limit: 10);
        summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_preserves_old_thread_and_GetOrCreateAsync_returns_newest_for_scope()
    {
        var userId = $"user-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AssistantConversationRepository(context);

        var oldConversation = await repo.GetOrCreateAsync(userId, AssistantConversationScope.Homepage());
        await repo.AppendMessageAsync(oldConversation.Id, AssistantMessageRole.User, "hello", null, null, null);

        var newConversation = await repo.CreateAsync(userId, AssistantConversationScope.Homepage());
        var latest = await repo.GetOrCreateAsync(userId, AssistantConversationScope.Homepage());
        var summaries = await repo.ListByUserAsync(userId, limit: 10);

        newConversation.Id.Should().NotBe(oldConversation.Id);
        latest.Id.Should().Be(newConversation.Id);
        (await repo.GetByIdAsync(oldConversation.Id)).Should().NotBeNull();
        (await repo.ListMessagesAsync(oldConversation.Id)).Should().ContainSingle(m => m.Content == "hello");
        summaries.Select(s => s.Id).Should().Contain(new[] { oldConversation.Id, newConversation.Id });
    }

    [Fact]
    public async Task CreateAsync_preserves_entity_scoped_threads_per_scope_key()
    {
        // Two distinct entity scopes must each track their own latest conversation. Threads
        // created with CreateAsync against scope A must not influence GetOrCreateAsync for
        // scope B.
        var userId = $"user-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AssistantConversationRepository(context);

        var traceId = Guid.NewGuid().ToString();
        var workflowId = Guid.NewGuid().ToString();
        var traceScope = AssistantConversationScope.Entity("trace", traceId);
        var workflowScope = AssistantConversationScope.Entity("workflow", workflowId);

        var traceOriginal = await repo.GetOrCreateAsync(userId, traceScope);
        await repo.AppendMessageAsync(traceOriginal.Id, AssistantMessageRole.User, "trace-1", null, null, null);

        var traceFresh = await repo.CreateAsync(userId, traceScope);
        var workflowOriginal = await repo.GetOrCreateAsync(userId, workflowScope);

        var traceLatest = await repo.GetOrCreateAsync(userId, traceScope);
        var workflowLatest = await repo.GetOrCreateAsync(userId, workflowScope);

        traceFresh.Id.Should().NotBe(traceOriginal.Id);
        traceLatest.Id.Should().Be(traceFresh.Id, "the freshly-created trace thread is the newest for the trace scope");
        workflowLatest.Id.Should().Be(workflowOriginal.Id, "the workflow scope is independent from the trace scope");
        (await repo.GetByIdAsync(traceOriginal.Id)).Should().NotBeNull("the original trace thread must be preserved");
    }

    [Fact]
    public async Task CompactAsync_appends_summary_advances_watermark_and_resets_token_totals()
    {
        var userId = $"user-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AssistantConversationRepository(context);

        var conv = await repo.GetOrCreateAsync(userId, AssistantConversationScope.Homepage());
        await repo.AppendMessageAsync(conv.Id, AssistantMessageRole.User, "first user msg", null, null, null);
        await repo.AppendMessageAsync(conv.Id, AssistantMessageRole.Assistant, "first assistant msg", "anthropic", "claude", Guid.NewGuid());
        await repo.AppendMessageAsync(conv.Id, AssistantMessageRole.User, "second user msg", null, null, null);
        await repo.AddTokenUsageAsync(conv.Id, inputTokensDelta: 800, outputTokensDelta: 200);

        var result = await repo.CompactAsync(conv.Id, "synthesized summary", "anthropic", "claude");

        result.CompactedThroughSequence.Should().Be(3, "the previous max sequence becomes the watermark");
        result.InputTokensTotal.Should().Be(0);
        result.OutputTokensTotal.Should().Be(0);
        result.SummaryMessage.Role.Should().Be(AssistantMessageRole.Summary);
        result.SummaryMessage.Sequence.Should().Be(4, "the summary lands above the watermark so it survives history filtering");
        result.SummaryMessage.Content.Should().Be("synthesized summary");

        // Persisted state matches the result.
        var refreshed = await repo.GetByIdAsync(conv.Id);
        refreshed!.CompactedThroughSequence.Should().Be(3);
        refreshed.InputTokensTotal.Should().Be(0);
        refreshed.OutputTokensTotal.Should().Be(0);

        // Display history retains everything for the UI…
        var allMessages = await repo.ListMessagesAsync(conv.Id);
        allMessages.Should().HaveCount(4);

        // …but the LLM-facing view drops the pre-watermark messages and keeps the summary.
        var llmMessages = await repo.ListMessagesForLlmAsync(conv.Id);
        llmMessages.Should().ContainSingle();
        llmMessages[0].Role.Should().Be(AssistantMessageRole.Summary);
        llmMessages[0].Content.Should().Be("synthesized summary");
    }

    [Fact]
    public async Task ListMessagesForLlmAsync_when_uncompacted_returns_full_history()
    {
        var userId = $"user-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AssistantConversationRepository(context);

        var conv = await repo.GetOrCreateAsync(userId, AssistantConversationScope.Homepage());
        await repo.AppendMessageAsync(conv.Id, AssistantMessageRole.User, "u1", null, null, null);
        await repo.AppendMessageAsync(conv.Id, AssistantMessageRole.Assistant, "a1", "openai", "gpt", Guid.NewGuid());

        var llm = await repo.ListMessagesForLlmAsync(conv.Id);
        llm.Should().HaveCount(2);
        llm.Select(m => m.Role).Should().Equal(new[] { AssistantMessageRole.User, AssistantMessageRole.Assistant });
    }

    private async Task ForceUpdatedAtUtcAsync(Guid conversationId, DateTime updatedAtUtc)
    {
        await using var context = CreateDbContext();
        var entity = await context.AssistantConversations.SingleAsync(c => c.Id == conversationId);
        entity.UpdatedAtUtc = updatedAtUtc;
        await context.SaveChangesAsync();
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
