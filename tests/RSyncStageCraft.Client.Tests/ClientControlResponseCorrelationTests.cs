#nullable enable

using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Scenes;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class ClientControlResponseCorrelationTests
{
    [Fact]
    public void EventsRequireExactValidCursorAndLimitMetadataBeforeAdmission()
    {
        var fixture = new Fixture();
        var id = Guid.NewGuid();
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest,
            fixture.Registry.Register(id, ClientControlRequestKind.Events, null).Status);
        foreach (var metadata in new ClientObjectEventRequestMetadata[]
        {
            new(new(-1), 1), new(new(0), 0), new(new(0), ProtocolLimits.MaximumAuthoritativeObjectEvents + 1),
        })
            Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest,
                fixture.Registry.Register(id, ClientControlRequestKind.Events, null, metadata).Status);
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest,
            fixture.Registry.Register(id, ClientControlRequestKind.Snapshot, null, new(new(0), 1)).Status);
        Assert.Equal(new ClientControlRequestSnapshot(0, 0, false), fixture.Registry.Snapshot);
        var receipt = fixture.Registry.Register(id, ClientControlRequestKind.Events, null, new(new(5), 2)).Receipt!;
        Assert.Equal(new ClientObjectEventRequestMetadata(new(5), 2), receipt.EventRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactSceneResultCompletesOnlyOnceAndReturnsCallerOwnedCommandData(bool read)
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(read ? ClientControlRequestKind.ReadScene : ClientControlRequestKind.SaveScene);
        object payload = read
            ? new SceneReadCompletion(receipt.RequestId, receipt.SceneId!.Value, SceneReadCompletionStatus.Loaded,
                ControlResponseContractTests.Document(receipt.SceneId.Value))
            : new SceneSaveCompletion(receipt.RequestId, receipt.SceneId!.Value, SceneSaveCompletionStatus.Failed);
        var type = read ? ObjectProtocolMessageType.SceneReadCompleted : ObjectProtocolMessageType.SceneSaveCompleted;
        var message = fixture.Message(receipt, type, payload);

        var result = fixture.Receive(receipt, message);

        Assert.True(result.Completed);
        Assert.Same(receipt, result.Receipt);
        Assert.Equal(payload.GetType(), result.CommandResult!.GetType());
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), fixture.Registry.Snapshot);
        Assert.Equal(ClientControlResponseStatus.InactiveRequest, fixture.Receive(receipt, message).Status);
        Assert.Equal(ClientControlRequestRegistrationStatus.DuplicateRequest,
            fixture.Registry.Register(receipt.RequestId, receipt.Kind, receipt.SceneId).Status);
    }

    [Theory]
    [InlineData(ClientControlRequestKind.CreateObject)]
    [InlineData(ClientControlRequestKind.UpdateObject)]
    [InlineData(ClientControlRequestKind.DeleteObject)]
    [InlineData(ClientControlRequestKind.Snapshot)]
    [InlineData(ClientControlRequestKind.Events)]
    [InlineData(ClientControlRequestKind.SaveScene)]
    [InlineData(ClientControlRequestKind.ReadScene)]
    public void GenericRejectionCompletesOnlyExactUnstartedRequest(ClientControlRequestKind kind)
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(kind);
        var rejected = new ObjectRequestRejected(receipt.RequestId, ProtocolRejectReason.NotAuthorized, null);
        var result = fixture.Receive(receipt, fixture.Message(receipt, ObjectProtocolMessageType.Rejected, rejected));
        Assert.True(result.Completed);
        Assert.Equal(rejected, result.CommandResult);
        Assert.Equal(0, fixture.Registry.Snapshot.PendingRequests);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("scene")]
    [InlineData("request-id")]
    [InlineData("session-id")]
    [InlineData("session-generation")]
    [InlineData("epoch")]
    [InlineData("protocol")]
    [InlineData("payload-id")]
    [InlineData("payload-family")]
    public void WrongReplyCannotConsumeSceneOwnership(string mutation)
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(ClientControlRequestKind.ReadScene);
        var read = new SceneReadCompletion(receipt.RequestId, receipt.SceneId!.Value, SceneReadCompletionStatus.NotFound, null);
        var valid = fixture.Message(receipt, ObjectProtocolMessageType.SceneReadCompleted, read);
        var otherId = Guid.NewGuid();
        var changed = mutation switch
        {
            "kind" => fixture.Message(receipt, ObjectProtocolMessageType.SceneSaveCompleted,
                new SceneSaveCompletion(receipt.RequestId, receipt.SceneId.Value, SceneSaveCompletionStatus.Saved)),
            "scene" => valid with { Payload = read with { SceneId = new(Guid.NewGuid()) } },
            "request-id" => valid with
            {
                Envelope = valid.Envelope with { MessageId = otherId },
                Payload = read with { RequestId = otherId }
            },
            "session-id" => valid with { Envelope = valid.Envelope with { SessionId = 99 } },
            "session-generation" => valid with { Envelope = valid.Envelope with { SessionGeneration = 99 } },
            "epoch" => valid with { Envelope = valid.Envelope with { ConnectionEpoch = 99 } },
            "protocol" => valid with { Envelope = valid.Envelope with { ProtocolVersion = 1 } },
            "payload-id" => valid with { Payload = read with { RequestId = Guid.NewGuid() } },
            "payload-family" => valid with
            {
                Payload = new SceneSaveCompletion(receipt.RequestId,
                receipt.SceneId.Value, SceneSaveCompletionStatus.Saved)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var refused = fixture.Receive(receipt, changed);
        Assert.False(refused.Completed);
        Assert.Null(refused.CommandResult);
        Assert.Null(refused.Receipt);
        Assert.Equal(new ClientControlRequestSnapshot(1, 1, false), fixture.Registry.Snapshot);
        Assert.True(fixture.Receive(receipt, valid).Completed);
    }

    [Fact]
    public void NumericBindingAndForgedReceiptCannotManufactureOwnership()
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(ClientControlRequestKind.ReadScene);
        var message = fixture.SceneRead(receipt);
        var numericAlias = Fixture.Binding();
        Assert.Equal(fixture.BindingCapability.Remote, numericAlias.Remote);
        Assert.False(fixture.Registry.ReceiveCandidate(numericAlias, receipt, message).Completed);
        var forged = new ClientControlRequestReceipt(receipt.RequestId, receipt.Kind, receipt.SceneId,
            fixture.BindingCapability, receipt.Generation, receipt.EventRequest);
        Assert.False(fixture.Receive(forged, message).Completed);
        Assert.True(fixture.Registry.Owns(fixture.BindingCapability, receipt));
        Assert.True(fixture.Receive(receipt, message).Completed);
    }

    [Fact]
    public void CatchupRecordsOnlyBoundedContiguousEvidenceBeforeCompletion()
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(ClientControlRequestKind.Events, after: 5, maximum: 3);
        for (var sequence = 6L; sequence <= 8; sequence++)
        {
            var result = fixture.Receive(receipt, fixture.Event(receipt, sequence, (int)(sequence - 6)));
            Assert.Equal(ClientControlResponseStatus.EventObserved, result.Status);
            Assert.Same(receipt, result.Receipt);
            Assert.Null(result.CommandResult);
            Assert.Equal(1, fixture.Registry.Snapshot.PendingRequests);
        }
        var completion = fixture.Completion(receipt, 8, 8, 3, false);
        var done = fixture.Receive(receipt, completion);
        Assert.True(done.Completed);
        Assert.Equal(3, Assert.IsType<ObjectEventBatchCompletion>(done.CommandResult).EventCount);
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), fixture.Registry.Snapshot);
        Assert.Equal(ClientControlResponseStatus.InactiveRequest, fixture.Receive(receipt, completion).Status);
    }

    [Theory]
    [InlineData(ObjectEventBatchCompletionStatus.UpToDate, 5, 5)]
    [InlineData(ObjectEventBatchCompletionStatus.SnapshotRequired, 5, 8)]
    [InlineData(ObjectEventBatchCompletionStatus.InvalidCursor, 5, 3)]
    public void ExactZeroEventOutcomeCompletesWithoutCursorMovement(ObjectEventBatchCompletionStatus status,
        long after, long current)
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(ClientControlRequestKind.Events, after);
        var marker = new ObjectEventBatchCompletion(receipt.RequestId, status, new(after), new(current), new(after), 0, false);
        Assert.True(fixture.Receive(receipt, fixture.Message(receipt, ObjectProtocolMessageType.ObjectEventBatchCompleted, marker)).Completed);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("duplicate")]
    [InlineData("limit")]
    [InlineData("missing-events")]
    [InlineData("wrong-after")]
    [InlineData("invalid-marker")]
    [InlineData("partial-rejection")]
    [InlineData("wrong-family")]
    public void BrokenBatchIsStickyAndCannotBeHiddenByLaterMarker(string mutation)
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(ClientControlRequestKind.Events, after: 5, maximum: mutation == "limit" ? 1 : 2);
        ObjectControlMessage bad;
        if (mutation == "missing-events") bad = fixture.Completion(receipt, 6, 6, 1, false);
        else
        {
            Assert.Equal(ClientControlResponseStatus.EventObserved,
                fixture.Receive(receipt, fixture.Event(receipt, 6)).Status);
            bad = mutation switch
            {
                "gap" => fixture.Event(receipt, 8),
                "duplicate" => fixture.Event(receipt, 6),
                "limit" => fixture.Event(receipt, 7),
                "wrong-after" => fixture.Completion(receipt, 6, 6, 1, false) with
                {
                    Payload = new ObjectEventBatchCompletion(receipt.RequestId, ObjectEventBatchCompletionStatus.Advanced,
                        new(4), new(6), new(6), 2, false)
                },
                "invalid-marker" => fixture.Completion(receipt, 6, 6, 1, false) with
                {
                    Payload = new ObjectEventBatchCompletion(receipt.RequestId, ObjectEventBatchCompletionStatus.Advanced,
                        new(5), new(6), new(6), 99, false)
                },
                "partial-rejection" => fixture.Message(receipt, ObjectProtocolMessageType.Rejected,
                    new ObjectRequestRejected(receipt.RequestId, ProtocolRejectReason.SyncNotReady, null)),
                "wrong-family" => fixture.Message(receipt, ObjectProtocolMessageType.SceneSaveCompleted,
                    new SceneSaveCompletion(receipt.RequestId, new(Guid.NewGuid()), SceneSaveCompletionStatus.Saved)),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
        }
        Assert.False(fixture.Receive(receipt, bad).Completed);
        Assert.Equal(ClientControlResponseStatus.BatchMismatch,
            fixture.Receive(receipt, fixture.Completion(receipt, 6, 6, 1, false)).Status);
        Assert.Equal(new ClientControlRequestSnapshot(1, 1, false), fixture.Registry.Snapshot);
        Assert.True(fixture.Registry.Abandon(receipt));
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), fixture.Registry.Snapshot);
    }

    [Fact]
    public void SequenceCannotWrapAndForeignEnvelopeCannotPoisonBatch()
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(ClientControlRequestKind.Events, after: long.MaxValue, maximum: 1);
        var wrong = fixture.Event(receipt, 0);
        var foreign = wrong with { Envelope = wrong.Envelope with { ConnectionEpoch = 99 } };
        Assert.Equal(ClientControlResponseStatus.EnvelopeMismatch, fixture.Receive(receipt, foreign).Status);
        Assert.Equal(ClientControlResponseStatus.EventSequenceMismatch, fixture.Receive(receipt, wrong).Status);
        var marker = new ObjectEventBatchCompletion(receipt.RequestId, ObjectEventBatchCompletionStatus.UpToDate,
            new(long.MaxValue), new(long.MaxValue), new(long.MaxValue), 0, false);
        Assert.Equal(ClientControlResponseStatus.BatchMismatch, fixture.Receive(receipt,
            fixture.Message(receipt, ObjectProtocolMessageType.ObjectEventBatchCompleted, marker)).Status);
    }

    [Fact]
    public void MarkerCannotCompleteAnotherRequestWithSameCursor()
    {
        var fixture = new Fixture();
        var first = fixture.Register(ClientControlRequestKind.Events, after: 5, maximum: 2);
        var second = fixture.Register(ClientControlRequestKind.Events, after: 5, maximum: 2);
        Assert.Equal(ClientControlResponseStatus.EventObserved, fixture.Receive(first, fixture.Event(first, 6)).Status);
        Assert.Equal(ClientControlResponseStatus.EnvelopeMismatch,
            fixture.Receive(second, fixture.Completion(first, 6, 6, 1, false)).Status);
        Assert.True(fixture.Receive(first, fixture.Completion(first, 6, 6, 1, false)).Completed);
        var empty = new ObjectEventBatchCompletion(second.RequestId, ObjectEventBatchCompletionStatus.UpToDate,
            new(5), new(5), new(5), 0, false);
        Assert.True(fixture.Receive(second, fixture.Message(second, ObjectProtocolMessageType.ObjectEventBatchCompleted, empty)).Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetirementOrAbandonmentDrainsPartialEvidenceAndFencesReplacement(bool retire)
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(ClientControlRequestKind.Events, after: 5);
        Assert.Equal(ClientControlResponseStatus.EventObserved, fixture.Receive(receipt, fixture.Event(receipt, 6)).Status);
        if (retire) fixture.Registry.Retire();
        else Assert.True(fixture.Registry.Abandon(receipt));
        Assert.False(fixture.Receive(receipt, fixture.Completion(receipt, 6, 6, 1, false)).Completed);
        var replacement = new Fixture();
        var fresh = replacement.Registry.Register(receipt.RequestId, receipt.Kind, null, receipt.EventRequest).Receipt!;
        Assert.False(replacement.Receive(receipt, replacement.Completion(fresh, 6, 6, 1, false)).Completed);
        Assert.Equal(ClientControlResponseStatus.EventObserved, replacement.Receive(fresh, replacement.Event(fresh, 6)).Status);
        Assert.True(replacement.Receive(fresh, replacement.Completion(fresh, 6, 6, 1, false)).Completed);
    }

    [Fact]
    public async Task ConcurrentResponsesCompleteExactlyOneReceipt()
    {
        var fixture = new Fixture();
        var receipt = fixture.Register(ClientControlRequestKind.ReadScene);
        var message = fixture.SceneRead(receipt);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => fixture.Receive(receipt, message))));
        Assert.Single(results, r => r.Completed);
        Assert.All(results.Where(r => !r.Completed), r => Assert.Equal(ClientControlResponseStatus.InactiveRequest, r.Status));
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), fixture.Registry.Snapshot);
    }

    internal sealed class Fixture
    {
        internal ReceiverControlBinding BindingCapability { get; } = Binding();
        internal ClientControlRequestRegistry Registry { get; }
        internal Fixture() => Registry = new(BindingCapability, new(8, 16));
        internal ClientControlRequestReceipt Register(ClientControlRequestKind kind, long after = 0, int maximum = 2)
        {
            var scene = kind is ClientControlRequestKind.SaveScene or ClientControlRequestKind.ReadScene
                ? new RscSceneId(Guid.NewGuid()) : (RscSceneId?)null;
            var metadata = kind == ClientControlRequestKind.Events
                ? new ClientObjectEventRequestMetadata(new(after), maximum) : (ClientObjectEventRequestMetadata?)null;
            ClientObjectMutationRequestMetadata? mutation = kind switch
            {
                ClientControlRequestKind.CreateObject => new(RscObjectKind.Furniture, null, null, null),
                ClientControlRequestKind.UpdateObject => new(RscObjectKind.Furniture, null, new(Guid.NewGuid()), new(1)),
                ClientControlRequestKind.DeleteObject => new(null, null, new(Guid.NewGuid()), new(1)),
                _ => null,
            };
            var registered = Registry.Register(Guid.NewGuid(), kind, scene, metadata, mutation);
            Assert.Equal(ClientControlRequestRegistrationStatus.Registered, registered.Status);
            return registered.Receipt!;
        }
        internal ClientControlResponseResult Receive(ClientControlRequestReceipt receipt, ObjectControlMessage message) =>
            Registry.ReceiveCandidate(BindingCapability, receipt, message);
        internal ObjectControlMessage SceneRead(ClientControlRequestReceipt receipt) =>
            Message(receipt, ObjectProtocolMessageType.SceneReadCompleted, new SceneReadCompletion(receipt.RequestId,
                receipt.SceneId!.Value, SceneReadCompletionStatus.NotFound, null));
        internal ObjectControlMessage Message(ClientControlRequestReceipt receipt, ObjectProtocolMessageType type, object payload)
        {
            var remote = BindingCapability.Remote;
            return new(new(ProtocolConstants.CurrentVersion, type, receipt.RequestId,
                remote.SessionId, remote.SessionGeneration, remote.ConnectionEpoch), payload);
        }
        internal ObjectControlMessage Event(ClientControlRequestReceipt receipt, long sequence, int kind = 0)
        {
            var state = ControlResponseContractTests.Furniture(new(Guid.NewGuid()), Guid.NewGuid());
            return kind switch
            {
                0 => Message(receipt, ObjectProtocolMessageType.ObjectCreated, new ObjectCreatedMessage(new(sequence), state)),
                1 => Message(receipt, ObjectProtocolMessageType.ObjectUpdated, new ObjectUpdatedMessage(new(sequence), state)),
                _ => Message(receipt, ObjectProtocolMessageType.ObjectDeleted,
                    new ObjectDeletedMessage(new(sequence), new(state.ObjectId, state.Kind, new(2)))),
            };
        }
        internal ObjectControlMessage Completion(ClientControlRequestReceipt receipt, long current, long through, int count, bool more) =>
            Message(receipt, ObjectProtocolMessageType.ObjectEventBatchCompleted, new ObjectEventBatchCompletion(receipt.RequestId,
                ObjectEventBatchCompletionStatus.Advanced, receipt.EventRequest!.Value.After, new(current), new(through), count, more));
        internal static ReceiverControlBinding Binding() => new(new(1, 2, 3), new(1, 2, 7), new SyncConnectionLease(3));
    }
}
