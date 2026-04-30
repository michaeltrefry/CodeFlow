using System.Text.Json.Nodes;
using CodeFlow.Orchestration.Replay;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Replay;

/// <summary>
/// sc-275 — verifies the lineage hasher is deterministic over canonicalized inputs:
/// edit order doesn't matter, mock dictionary order doesn't matter, pinned-agent order
/// doesn't matter, but any input change moves the hash.
/// </summary>
public sealed class ReplayLineageHasherTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ReplayLineageMock>> EmptyMocks =
        new Dictionary<string, IReadOnlyList<ReplayLineageMock>>();
    private static readonly IReadOnlyDictionary<string, int> EmptyPinned =
        new Dictionary<string, int>();

    [Fact]
    public void IdenticalInputs_ProduceIdenticalHash()
    {
        var inputs = new ReplayLineageInputs(
            Edits: new[]
            {
                new ReplayLineageEdit("agent-a", 1, "Completed", "out", null),
                new ReplayLineageEdit("agent-b", 2, "Failed", null, JsonNode.Parse("""{"k":"v"}""")),
            },
            AdditionalMocks: EmptyMocks,
            WorkflowVersionOverride: 3,
            PinnedAgentVersions: new Dictionary<string, int> { ["agent-a"] = 1, ["agent-b"] = 2 },
            Force: false);

        ReplayLineageHasher.ComputeContentHash(inputs).Should()
            .Be(ReplayLineageHasher.ComputeContentHash(inputs));
    }

    [Fact]
    public void EditOrder_DoesNotChangeHash()
    {
        var first = new[]
        {
            new ReplayLineageEdit("agent-a", 1, "Completed", "out", null),
            new ReplayLineageEdit("agent-b", 2, "Failed", null, null),
        };
        var second = new[]
        {
            new ReplayLineageEdit("agent-b", 2, "Failed", null, null),
            new ReplayLineageEdit("agent-a", 1, "Completed", "out", null),
        };

        var hashFirst = ReplayLineageHasher.ComputeContentHash(MakeInputs(first));
        var hashSecond = ReplayLineageHasher.ComputeContentHash(MakeInputs(second));

        hashSecond.Should().Be(hashFirst);
    }

    [Fact]
    public void PinnedAgentOrder_DoesNotChangeHash()
    {
        var pinnedA = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        var pinnedB = new Dictionary<string, int> { ["b"] = 2, ["a"] = 1 };

        ReplayLineageHasher.ComputeContentHash(MakeInputs(pinnedAgentVersions: pinnedA))
            .Should()
            .Be(ReplayLineageHasher.ComputeContentHash(MakeInputs(pinnedAgentVersions: pinnedB)));
    }

    [Fact]
    public void EditChange_ChangesHash()
    {
        var hashOriginal = ReplayLineageHasher.ComputeContentHash(MakeInputs(new[]
        {
            new ReplayLineageEdit("agent-a", 1, "Completed", "first", null),
        }));
        var hashChanged = ReplayLineageHasher.ComputeContentHash(MakeInputs(new[]
        {
            new ReplayLineageEdit("agent-a", 1, "Completed", "second", null),
        }));

        hashChanged.Should().NotBe(hashOriginal);
    }

    [Fact]
    public void PinnedVersionChange_ChangesHash()
    {
        var hashV1 = ReplayLineageHasher.ComputeContentHash(MakeInputs(
            pinnedAgentVersions: new Dictionary<string, int> { ["a"] = 1 }));
        var hashV2 = ReplayLineageHasher.ComputeContentHash(MakeInputs(
            pinnedAgentVersions: new Dictionary<string, int> { ["a"] = 2 }));

        hashV2.Should().NotBe(hashV1);
    }

    [Fact]
    public void HashFormat_IsLowercaseHex64Chars()
    {
        var hash = ReplayLineageHasher.ComputeContentHash(MakeInputs());
        hash.Should().HaveLength(64);
        hash.Should().Be(hash.ToLowerInvariant());
    }

    [Fact]
    public void LineageId_IsStableAcrossRuns()
    {
        var traceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var hash = ReplayLineageHasher.ComputeContentHash(MakeInputs());

        var first = ReplayLineageHasher.ComputeLineageId(traceId, hash);
        var second = ReplayLineageHasher.ComputeLineageId(traceId, hash);

        second.Should().Be(first);
        first.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void LineageId_DiffersForDifferentParents()
    {
        var hash = ReplayLineageHasher.ComputeContentHash(MakeInputs());
        var traceA = Guid.NewGuid();
        var traceB = Guid.NewGuid();

        ReplayLineageHasher.ComputeLineageId(traceA, hash).Should()
            .NotBe(ReplayLineageHasher.ComputeLineageId(traceB, hash));
    }

    private static ReplayLineageInputs MakeInputs(
        IReadOnlyList<ReplayLineageEdit>? edits = null,
        IReadOnlyDictionary<string, IReadOnlyList<ReplayLineageMock>>? additionalMocks = null,
        int? workflowVersionOverride = null,
        IReadOnlyDictionary<string, int>? pinnedAgentVersions = null,
        bool force = false) =>
        new(
            Edits: edits ?? Array.Empty<ReplayLineageEdit>(),
            AdditionalMocks: additionalMocks ?? EmptyMocks,
            WorkflowVersionOverride: workflowVersionOverride,
            PinnedAgentVersions: pinnedAgentVersions ?? EmptyPinned,
            Force: force);
}
