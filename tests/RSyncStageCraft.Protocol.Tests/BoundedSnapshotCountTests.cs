using System.Collections;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Scenes;

namespace RSyncStageCraft.Tests;

public sealed class BoundedSnapshotCountTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidCombinedCountsRejectBeforeEnumeratingCallerCollections(int reportedCount)
    {
        var protocolObjects = new NonEnumerableReadOnlyList<AuthoritativeObjectState>(
            reportedCount);
        var sceneObjects = new NonEnumerableReadOnlyList<SceneObjectRecord>(reportedCount);
        var tombstones = new ObjectTombstone[1];

        var protocol = new ObjectSnapshotMessage(new ServerSequence(0), protocolObjects, tombstones);
        var scene = new SceneDocument(
            SceneFormat.CurrentVersion,
            new RscSceneId(Guid.NewGuid()),
            SceneGeneration: 1,
            DateTimeOffset.UtcNow,
            sceneObjects,
            tombstones);

        Assert.False(protocol.IsValid);
        Assert.False(scene.IsValid);
        Assert.False(protocolObjects.EnumerationAttempted);
        Assert.False(sceneObjects.EnumerationAttempted);
    }

    [Fact]
    public void ReportedCountCannotHideAdditionalEnumerableElements()
    {
        var actorTombstone = Tombstone(RscObjectKind.Actor);
        var protocolTombstones = new OverlongReadOnlyList<ObjectTombstone>(
            reportedCount: 1,
            [actorTombstone, actorTombstone]);
        var sceneTombstones = new OverlongReadOnlyList<ObjectTombstone>(
            reportedCount: 1,
            [actorTombstone, actorTombstone]);

        var protocol = new ObjectSnapshotMessage(
            new ServerSequence(0),
            Array.Empty<AuthoritativeObjectState>(),
            protocolTombstones);
        var scene = new SceneDocument(
            SceneFormat.CurrentVersion,
            new RscSceneId(Guid.NewGuid()),
            SceneGeneration: 1,
            DateTimeOffset.UtcNow,
            Array.Empty<SceneObjectRecord>(),
            sceneTombstones);

        Assert.False(protocol.IsValid);
        Assert.False(scene.IsValid);
        Assert.Equal(2, protocolTombstones.EnumeratedItems);
        Assert.Equal(2, sceneTombstones.EnumeratedItems);
    }

    [Fact]
    public void SnapshotEncodingUsesTheSingleBoundedNormalizedCopy()
    {
        var source = new SingleEnumerationReadOnlyList<ObjectTombstone>(
            [Tombstone(RscObjectKind.Actor)]);
        var snapshot = new ObjectSnapshotMessage(
            new ServerSequence(0),
            Array.Empty<AuthoritativeObjectState>(),
            source);
        var envelope = new SessionMessageEnvelope(
            ProtocolConstants.CurrentVersion,
            ObjectProtocolMessageType.ObjectSnapshot,
            Guid.NewGuid(),
            SessionId: 1,
            SessionGeneration: 1,
            ConnectionEpoch: 1);
        var codec = new ObjectControlMessageCodec();

        Assert.True(codec.TryEncode(envelope, snapshot, out var bytes, out var error), error.ToString());
        Assert.Equal(1, source.EnumerationCount);
        Assert.True(codec.TryDecode(bytes, out var decoded, out error), error.ToString());
        Assert.IsType<ObjectSnapshotMessage>(decoded!.Payload);
    }

    private static ObjectTombstone Tombstone(RscObjectKind kind) => new(
        new RscObjectId(Guid.NewGuid()),
        kind,
        new ObjectRevision(1));

    private sealed class NonEnumerableReadOnlyList<T>(int count) : IReadOnlyList<T>
    {
        public int Count { get; } = count;
        public bool EnumerationAttempted { get; private set; }
        public T this[int index] => throw new InvalidOperationException(
            "An invalid collection count must reject before indexing.");

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationAttempted = true;
            throw new InvalidOperationException(
                "An invalid collection count must reject before enumeration.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class OverlongReadOnlyList<T>(
        int reportedCount,
        IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int Count { get; } = reportedCount;
        public int EnumeratedItems { get; private set; }
        public T this[int index] => throw new InvalidOperationException(
            "Bounded snapshots must use one normalized enumeration.");

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var item in items)
            {
                EnumeratedItems++;
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SingleEnumerationReadOnlyList<T>(IReadOnlyList<T> items)
        : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public int EnumerationCount { get; private set; }
        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount > 1)
            {
                throw new InvalidOperationException(
                    "The caller collection was enumerated more than once.");
            }

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
