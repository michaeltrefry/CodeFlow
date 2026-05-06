using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence.Tests;

/// <summary>
/// sc-792 (AA-1): repository-level tests for <see cref="AssistantArtifactRepository"/>. The
/// table is metadata-only — bytes live on disk in the per-conversation workspace — so these
/// tests focus on sequence assignment, supersession semantics, snapshot expiry, and message
/// late-binding. Integration-style against MariaDB so the (conversation_id, sequence) unique
/// index and the indexes the rail/list queries depend on run against the production schema.
/// </summary>
[Collection(PersistenceMariaDbCollection.Name)]
public sealed class AssistantArtifactRepositoryTests : IAsyncLifetime
{
    private readonly SharedMariaDbFixture mariaDb;
    private const string DatabaseName = "test_assistantartifactrepositorytests";
    private string? connectionString;

    public AssistantArtifactRepositoryTests(SharedMariaDbFixture mariaDb)
    {
        this.mariaDb = mariaDb;
    }

    public async Task InitializeAsync()
    {
        connectionString = await mariaDb.EnsureDatabaseAsync(DatabaseName);

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDb.DropDatabaseAsync(DatabaseName);
    }

    [Fact]
    public async Task AddAsync_assigns_monotonic_sequence_per_conversation()
    {
        var (conversationId, _) = await SeedConversationAsync();

        await using var context = CreateDbContext();
        var repo = new AssistantArtifactRepository(context);

        var first = await repo.AddAsync(
            conversationId,
            ArtifactEventKind.WorkflowPackageDraft,
            "draft.cf-workflow-package.json",
            "draft.cf-workflow-package.json",
            snapshotId: null,
            summaryJson: null);
        var second = await repo.AddAsync(
            conversationId,
            ArtifactEventKind.WorkflowPackageDraft,
            "draft.cf-workflow-package.json",
            "draft.cf-workflow-package.json",
            snapshotId: null,
            summaryJson: null);

        first.Sequence.Should().Be(1);
        second.Sequence.Should().Be(2);
        first.SupersededByEventId.Should().BeNull(
            "the repository writes events; supersession is the recorder's job.");
    }

    [Fact]
    public async Task ListByConversationAsync_returns_events_ordered_by_sequence()
    {
        var (conversationId, _) = await SeedConversationAsync();

        await using var context = CreateDbContext();
        var repo = new AssistantArtifactRepository(context);

        await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageDraft, "draft.cf-workflow-package.json", "draft.cf-workflow-package.json", null, null);
        await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageSnapshot, "snapshot-1", "snapshot-1.cf-workflow-package.json", Guid.NewGuid(), null);
        await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageDraft, "draft.cf-workflow-package.json", "draft.cf-workflow-package.json", null, null);

        var events = await repo.ListByConversationAsync(conversationId);

        events.Should().HaveCount(3);
        events.Select(e => e.Sequence).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ListByConversationAsync_does_not_leak_other_conversations()
    {
        var (conversationA, _) = await SeedConversationAsync();
        var (conversationB, _) = await SeedConversationAsync();

        await using var context = CreateDbContext();
        var repo = new AssistantArtifactRepository(context);

        await repo.AddAsync(conversationA, ArtifactEventKind.WorkflowPackageDraft, "a-draft", "a-draft", null, null);
        await repo.AddAsync(conversationB, ArtifactEventKind.WorkflowPackageDraft, "b-draft", "b-draft", null, null);

        (await repo.ListByConversationAsync(conversationA)).Should().ContainSingle(e => e.Name == "a-draft");
        (await repo.ListByConversationAsync(conversationB)).Should().ContainSingle(e => e.Name == "b-draft");
    }

    [Fact]
    public async Task MarkActiveSupersededByNameAsync_marks_only_active_same_name_events()
    {
        var (conversationId, _) = await SeedConversationAsync();

        await using var context = CreateDbContext();
        var repo = new AssistantArtifactRepository(context);

        var draft1 = await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageDraft, "draft.cf-workflow-package.json", "draft.cf-workflow-package.json", null, null);
        // A snapshot event with a DIFFERENT name must NOT be superseded by a draft replacement.
        var snapshot = await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageSnapshot, "snapshot-1", "snapshot-1", Guid.NewGuid(), null);
        var draft2 = await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageDraft, "draft.cf-workflow-package.json", "draft.cf-workflow-package.json", null, null);

        var rowsUpdated = await repo.MarkActiveSupersededByNameAsync(
            conversationId,
            "draft.cf-workflow-package.json",
            draft2.Id);

        rowsUpdated.Should().Be(1, "only draft1 was active under that name; draft2 is the superseder.");
        var events = await repo.ListByConversationAsync(conversationId);
        events.Single(e => e.Id == draft1.Id).SupersededByEventId.Should().Be(draft2.Id);
        events.Single(e => e.Id == snapshot.Id).SupersededByEventId.Should().BeNull(
            "snapshot has a different name and must not be touched.");
        events.Single(e => e.Id == draft2.Id).SupersededByEventId.Should().BeNull(
            "the superseder itself is excluded from the supersession sweep.");
    }

    [Fact]
    public async Task MarkActiveSupersededByNameAsync_is_idempotent()
    {
        var (conversationId, _) = await SeedConversationAsync();

        await using var context = CreateDbContext();
        var repo = new AssistantArtifactRepository(context);

        await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageDraft, "draft", "draft", null, null);
        var draft2 = await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageDraft, "draft", "draft", null, null);

        await repo.MarkActiveSupersededByNameAsync(conversationId, "draft", draft2.Id);
        var rowsSecond = await repo.MarkActiveSupersededByNameAsync(conversationId, "draft", draft2.Id);

        rowsSecond.Should().Be(0, "the prior call already marked everything; the second call is a no-op.");
    }

    [Fact]
    public async Task MarkExpiredBySnapshotIdAsync_sets_expired_at_only_when_active()
    {
        var (conversationId, _) = await SeedConversationAsync();

        await using var context = CreateDbContext();
        var repo = new AssistantArtifactRepository(context);

        var snapshotId = Guid.NewGuid();
        var snapshot = await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageSnapshot, "snapshot", "snapshot", snapshotId, null);

        var rowsFirst = await repo.MarkExpiredBySnapshotIdAsync(snapshotId, DateTime.UtcNow);
        rowsFirst.Should().Be(1);
        var afterFirst = await repo.GetAsync(snapshot.Id);
        afterFirst!.ExpiredAtUtc.Should().NotBeNull();

        var rowsSecond = await repo.MarkExpiredBySnapshotIdAsync(snapshotId, DateTime.UtcNow);
        rowsSecond.Should().Be(0, "expired_at is non-null already; the predicate excludes already-expired rows.");
    }

    [Fact]
    public async Task BindMessageAsync_sets_message_id_and_is_idempotent()
    {
        var (conversationId, _) = await SeedConversationAsync();

        await using var context = CreateDbContext();
        var repo = new AssistantArtifactRepository(context);

        var added = await repo.AddAsync(conversationId, ArtifactEventKind.WorkflowPackageDraft, "draft", "draft", null, null);
        added.MessageId.Should().BeNull();

        var messageId = Guid.NewGuid();
        await repo.BindMessageAsync(added.Id, messageId);
        var afterBind = await repo.GetAsync(added.Id);
        afterBind!.MessageId.Should().Be(messageId);

        // Calling BindMessage with the same id again must not throw and must not produce
        // additional writes (verified by re-fetch having the same value).
        await repo.BindMessageAsync(added.Id, messageId);
        (await repo.GetAsync(added.Id))!.MessageId.Should().Be(messageId);
    }

    [Fact]
    public async Task AddAsync_throws_for_missing_conversation()
    {
        await using var context = CreateDbContext();
        var repo = new AssistantArtifactRepository(context);

        var act = async () => await repo.AddAsync(
            Guid.NewGuid(),
            ArtifactEventKind.WorkflowPackageDraft,
            "draft",
            "draft",
            null,
            null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    private async Task<(Guid ConversationId, AssistantConversation Conversation)> SeedConversationAsync()
    {
        await using var context = CreateDbContext();
        var convRepo = new AssistantConversationRepository(context);
        var userId = $"user-{Guid.NewGuid():N}";
        var conversation = await convRepo.GetOrCreateAsync(userId, AssistantConversationScope.Homepage());
        return (conversation.Id, conversation);
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
