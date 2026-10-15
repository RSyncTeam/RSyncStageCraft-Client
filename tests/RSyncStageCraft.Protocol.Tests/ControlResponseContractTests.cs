#nullable enable

using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Scenes;

namespace RSyncStageCraft.Tests;

public sealed class ControlResponseContractTests
{
    [Theory]
    [InlineData(ObjectEventBatchCompletionStatus.Advanced, 3, 6, 5, 2, true)]
    [InlineData(ObjectEventBatchCompletionStatus.Advanced, 3, 5, 5, 2, false)]
    [InlineData(ObjectEventBatchCompletionStatus.UpToDate, 0, 0, 0, 0, false)]
    [InlineData(ObjectEventBatchCompletionStatus.SnapshotRequired, 0, 8, 0, 0, false)]
    [InlineData(ObjectEventBatchCompletionStatus.InvalidCursor, 8, 4, 8, 0, false)]
    public void CompletionHasExactBoundedStatusShape(ObjectEventBatchCompletionStatus status,
        long after, long current, long through, int count, bool more)
    {
        Assert.True(new ObjectEventBatchCompletion(Guid.NewGuid(), status,
            new(after), new(current), new(through), count, more).IsValid);
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("unknown-status")]
    [InlineData("negative-after")]
    [InlineData("negative-watermark")]
    [InlineData("negative-through")]
    [InlineData("negative-count")]
    [InlineData("oversized-count")]
    [InlineData("count-mismatch")]
    [InlineData("empty-advanced")]
    [InlineData("past-watermark")]
    [InlineData("missing-more")]
    [InlineData("spurious-more")]
    [InlineData("backwards")]
    [InlineData("overflow")]
    [InlineData("up-to-date-behind")]
    [InlineData("snapshot-ahead")]
    [InlineData("invalid-cursor-behind")]
    [InlineData("non-advanced-events")]
    [InlineData("empty-through-mismatch")]
    public void ContradictoryCompletionCannotInventSequenceMovement(string mutation)
    {
        var valid = new ObjectEventBatchCompletion(Guid.NewGuid(), ObjectEventBatchCompletionStatus.Advanced,
            new(3), new(6), new(5), 2, true);
        var changed = mutation switch
        {
            "empty-id" => valid with { RequestId = Guid.Empty },
            "unknown-status" => valid with { Status = (ObjectEventBatchCompletionStatus)99 },
            "negative-after" => valid with { RequestedAfter = new(-1) },
            "negative-watermark" => valid with { CurrentWatermark = new(-1) },
            "negative-through" => valid with { DeliveredThrough = new(-1) },
            "negative-count" => valid with { EventCount = -1 },
            "oversized-count" => valid with { EventCount = ProtocolLimits.MaximumAuthoritativeObjectEvents + 1 },
            "count-mismatch" => valid with { EventCount = 1 },
            "empty-advanced" => valid with { EventCount = 0, DeliveredThrough = new(3) },
            "past-watermark" => valid with { CurrentWatermark = new(4) },
            "missing-more" => valid with { HasMore = false },
            "spurious-more" => valid with { CurrentWatermark = new(5) },
            "backwards" => valid with { DeliveredThrough = new(2) },
            "overflow" => valid with { RequestedAfter = new(long.MaxValue), DeliveredThrough = new(0) },
            "up-to-date-behind" => Empty(ObjectEventBatchCompletionStatus.UpToDate, 3, 4),
            "snapshot-ahead" => Empty(ObjectEventBatchCompletionStatus.SnapshotRequired, 5, 4),
            "invalid-cursor-behind" => Empty(ObjectEventBatchCompletionStatus.InvalidCursor, 3, 4),
            "non-advanced-events" => valid with { Status = ObjectEventBatchCompletionStatus.SnapshotRequired },
            "empty-through-mismatch" => Empty(ObjectEventBatchCompletionStatus.UpToDate, 4, 4)
                with
            { DeliveredThrough = new(3) },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.False(changed.IsValid);
    }

    [Fact]
    public void SceneReadCopiesAndCanonicalizesDocumentWithoutChangingSource()
    {
        var scene = new RscSceneId(Guid.NewGuid());
        var first = Furniture(scene, Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var second = Furniture(scene, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var source = new[] { SceneObjectRecord.FromState(first), SceneObjectRecord.FromState(second) };
        var response = new SceneReadCompletion(Guid.NewGuid(), scene, SceneReadCompletionStatus.Loaded,
            Document(scene, source));

        Assert.True(response.TryNormalize(out var normalized));
        source[0] = SceneObjectRecord.FromState(Furniture(scene, Guid.NewGuid()));

        Assert.NotSame(response.Document!.Objects, normalized.Document!.Objects);
        Assert.Equal(second.ObjectId, normalized.Document.Objects[0].State!.ObjectId);
        Assert.Equal(first.ObjectId, normalized.Document.Objects[1].State!.ObjectId);
        Assert.NotEqual(normalized.Document.Objects[1], source[0]);
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("invalid-scene")]
    [InlineData("unknown-status")]
    [InlineData("missing-document")]
    [InlineData("wrong-document-scene")]
    [InlineData("wrong-object-scene")]
    [InlineData("failed-with-document")]
    [InlineData("duplicate-object")]
    [InlineData("object-tombstone-alias")]
    [InlineData("oversized-document")]
    [InlineData("non-utc-time")]
    public void SceneReadRequiresOneExactValidBoundedDocument(string mutation)
    {
        var scene = new RscSceneId(Guid.NewGuid());
        var item = Furniture(scene, Guid.NewGuid());
        var document = Document(scene, [SceneObjectRecord.FromState(item)]);
        var valid = new SceneReadCompletion(Guid.NewGuid(), scene, SceneReadCompletionStatus.Loaded, document);
        var changed = mutation switch
        {
            "empty-id" => valid with { RequestId = Guid.Empty },
            "invalid-scene" => valid with { SceneId = default },
            "unknown-status" => valid with { Status = (SceneReadCompletionStatus)99 },
            "missing-document" => valid with { Document = null },
            "wrong-document-scene" => valid with { SceneId = new(Guid.NewGuid()) },
            "wrong-object-scene" => valid with
            {
                Document = document with
                {
                    Objects =
                [SceneObjectRecord.FromState(item with { SceneId = new(Guid.NewGuid()) })]
                }
            },
            "failed-with-document" => valid with { Status = SceneReadCompletionStatus.Failed },
            "duplicate-object" => valid with
            {
                Document = document with
                {
                    Objects =
                [SceneObjectRecord.FromState(item), SceneObjectRecord.FromState(item)]
                }
            },
            "object-tombstone-alias" => valid with
            {
                Document = document with
                {
                    Tombstones =
                [new(item.ObjectId, item.Kind, new(2))]
                }
            },
            "oversized-document" => valid with
            {
                Document = document with
                {
                    Objects = Enumerable.Repeat(
                SceneObjectRecord.FromState(item), ProtocolLimits.MaximumObjectsPerSnapshot + 1).ToArray()
                }
            },
            "non-utc-time" => valid with { Document = document with { SavedAtUtc = document.SavedAtUtc.ToOffset(TimeSpan.FromHours(1)) } },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.False(changed.TryNormalize(out var normalized));
        Assert.Null(normalized.Document);
    }

    [Fact]
    public void FailureResponsesHaveNoDocumentOrDiagnosticTextField()
    {
        foreach (var status in Enum.GetValues<SceneReadCompletionStatus>().Where(s => s != SceneReadCompletionStatus.Loaded))
            Assert.True(new SceneReadCompletion(Guid.NewGuid(), new(Guid.NewGuid()), status, null).IsValid);
        Assert.DoesNotContain(typeof(SceneSaveCompletion).GetProperties(), p => p.Name is "Error" or "Path");
        Assert.DoesNotContain(typeof(SceneReadCompletion).GetProperties(), p => p.Name is "Error" or "Path");
    }

    internal static SceneDocument Document(RscSceneId scene, IReadOnlyList<SceneObjectRecord>? objects = null) =>
        new(SceneFormat.CurrentVersion, scene, 1, new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
            objects ?? [], []);

    internal static FurnitureState Furniture(RscSceneId scene, Guid id) =>
        new(new(id), new(1), new("owner"), scene, "furniture:indoor:6000", new(1, 2, 3), new(0, 0, 0, 1));

    private static ObjectEventBatchCompletion Empty(ObjectEventBatchCompletionStatus status, long after, long current) =>
        new(Guid.NewGuid(), status, new(after), new(current), new(after), 0, false);
}
