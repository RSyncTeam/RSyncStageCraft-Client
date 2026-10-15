using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class LocalObjectOwnershipLedgerTests
{
    [Fact]
    public void CapacityAndSessionOwnershipAreBoundedAndExact()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 1);
        var oldSession = Session(1);
        var newSession = Session(2);
        var objectId = ObjectId();

        Assert.True(ledger.TryReserve(Token(oldSession, objectId, 1), Furniture("furniture:indoor:6000")).Accepted);
        Assert.Equal(
            LocalObjectReservationStatus.CapacityExceeded,
            ledger.TryReserve(Token(oldSession, ObjectId(), 1), Furniture("furniture:indoor:6002")).Status);
        Assert.Equal(
            LocalObjectReservationStatus.SessionConflict,
            ledger.TryReserve(Token(newSession, objectId, 1), Furniture("furniture:indoor:6000")).Status);
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void CompatibleGenerationRetainsExactHandleAndRequiresUpdate()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 1);
        var session = Session(1);
        var objectId = ObjectId();
        var first = Token(session, objectId, 1);
        var second = Token(session, objectId, 2);
        var identity = Furniture("furniture:indoor:6000");
        var handle = Handle(objectId, RscObjectKind.Furniture, 1);

        Assert.True(ledger.TryReserve(first, identity).Accepted);
        Assert.True(ledger.TryRecordSpawn(first, identity, handle));
        Assert.True(ledger.TryReserve(second, identity).Accepted);

        Assert.False(ledger.TryGet(first, out _));
        Assert.True(ledger.TryGet(second, out var retained));
        Assert.Equal(handle, retained.Handle);
        Assert.Equal(LocalObjectReconcileAction.Update, retained.Action);
        Assert.Equal(1, retained.NextSpawnGeneration);
    }

    [Fact]
    public void AppliedMarkerRequiresExactDesiredGenerationAndHandle()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 1);
        var session = Session(1);
        var objectId = ObjectId();
        var first = Token(session, objectId, 1);
        var second = Token(session, objectId, 2);
        var identity = Furniture("furniture:indoor:6000");
        var handle = Handle(objectId, RscObjectKind.Furniture, 1);
        Assert.True(ledger.TryReserve(first, identity).Accepted);
        Assert.True(ledger.TryRecordSpawn(first, identity, handle));

        Assert.True(ledger.TryMarkApplied(first, handle));
        Assert.True(ledger.IsApplied(first));
        Assert.False(ledger.TryMarkApplied(
            first,
            handle with { OwnershipId = Guid.NewGuid() }));
        Assert.True(ledger.TryReserve(second, identity).Accepted);
        Assert.False(ledger.IsApplied(first));
        Assert.False(ledger.IsApplied(second));
        Assert.True(ledger.TryMarkApplied(second, handle));
        Assert.True(ledger.IsApplied(second));
    }

    [Fact]
    public void IdentityReplacementReleasesBeforeAdvancingSpawnGeneration()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 1);
        var session = Session(1);
        var objectId = ObjectId();
        var first = Token(session, objectId, 1);
        var second = Token(session, objectId, 2);
        var oldIdentity = Furniture("furniture:indoor:6000");
        var newIdentity = Furniture("furniture:indoor:6002");
        var oldHandle = Handle(objectId, RscObjectKind.Furniture, 1);

        Assert.True(ledger.TryReserve(first, oldIdentity).Accepted);
        Assert.True(ledger.TryRecordSpawn(first, oldIdentity, oldHandle));
        Assert.True(ledger.TryReserve(second, newIdentity).Accepted);
        Assert.True(ledger.TryGet(second, out var replacement));
        Assert.Equal(LocalObjectReconcileAction.Replace, replacement.Action);

        Assert.False(ledger.TryBeginRelease(first, out _));
        Assert.True(ledger.TryBeginRelease(second, out var claimed));
        Assert.Equal(oldHandle, claimed);
        Assert.False(ledger.TryReserve(Token(session, objectId, 3), newIdentity).Accepted);
        Assert.True(ledger.TryCompleteRelease(second, claimed));

        Assert.True(ledger.TryGet(second, out var spawn));
        Assert.Equal(LocalObjectReconcileAction.Spawn, spawn.Action);
        Assert.Equal(2, spawn.NextSpawnGeneration);
        Assert.False(ledger.TryRecordSpawn(first, oldIdentity, oldHandle));
        Assert.True(ledger.TryRecordSpawn(
            second,
            newIdentity,
            Handle(objectId, RscObjectKind.Furniture, 2)));
    }

    [Fact]
    public void ActorSpawnIdentityIsTheBNpcBaseRow()
    {
        var actor = new ActorState(ObjectId(), new ObjectRevision(1), new RscUserId("owner"), null, 4242,
            new Vector3State(1, 2, 3), 0, 1, ActorControlState.Default);
        var identity = LocalObjectSpawnIdentity.FromState(actor);

        Assert.True(identity.IsValid);
        Assert.Equal(LocalObjectSpawnIdentity.ForActor(4242), identity);
        Assert.NotEqual(identity, LocalObjectSpawnIdentity.ForActor(4243));
        Assert.False(new LocalObjectSpawnIdentity(RscObjectKind.Actor, identity.Value).IsValid);
        Assert.False(new LocalObjectSpawnIdentity(
            RscObjectKind.Furniture,
            "furniture:indoor:6000",
            BNpcBaseId: 4242).IsValid);
    }

    [Fact]
    public void LifecycleReceiptsExposeNoVersionSpecificPointerOrObjectIndex()
    {
        var propertyNames = typeof(LocalObjectInstanceHandle)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("Pointer", propertyNames);
        Assert.DoesNotContain("Address", propertyNames);
        Assert.DoesNotContain("ObjectIndex", propertyNames);
        Assert.DoesNotContain("NativeHandle", propertyNames);
    }

    [Fact]
    public void SpawnRequestRejectsIdentityThatDiffersFromDesiredState()
    {
        var objectId = ObjectId();
        var token = Token(Session(1), objectId, 1);
        var desired = new FurnitureState(
            objectId,
            new ObjectRevision(1),
            new RscUserId("creator"),
            null,
            "furniture:indoor:6000",
            new Vector3State(1, 2, 3),
            new RotationState(0, 0, 0, 1));

        var request = new LocalObjectSpawnRequest(
            token,
            Furniture("furniture:indoor:6002"),
            SpawnGeneration: 1,
            desired);

        Assert.False(request.IsValid);
    }

    [Fact]
    public void SameGenerationCannotChangeIdentityOrObjectKind()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 1);
        var token = Token(Session(1), ObjectId(), 1);

        Assert.True(ledger.TryReserve(token, Furniture("furniture:indoor:6000")).Accepted);
        Assert.Equal(
            LocalObjectReservationStatus.GenerationConflict,
            ledger.TryReserve(token, Furniture("furniture:indoor:6002")).Status);
        Assert.Equal(
            LocalObjectReservationStatus.ObjectKindConflict,
            ledger.TryReserve(
                token with { Generation = new ApplyGeneration(2) },
                new LocalObjectSpawnIdentity(RscObjectKind.Light, "point")).Status);
    }

    [Fact]
    public void OneOwnershipReceiptCannotBeRecordedForTwoObjects()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 2);
        var session = Session(1);
        var firstId = ObjectId();
        var secondId = ObjectId();
        var identity = Furniture("furniture:indoor:6000");
        var ownershipId = Guid.NewGuid();
        var first = Token(session, firstId, 1);
        var second = Token(session, secondId, 1);

        Assert.True(ledger.TryReserve(first, identity).Accepted);
        Assert.True(ledger.TryReserve(second, identity).Accepted);
        Assert.True(ledger.TryRecordSpawn(
            first,
            identity,
            new LocalObjectInstanceHandle(
                ownershipId,
                firstId,
                RscObjectKind.Furniture,
                1)));
        Assert.False(ledger.TryRecordSpawn(
            second,
            identity,
            new LocalObjectInstanceHandle(
                ownershipId,
                secondId,
                RscObjectKind.Furniture,
                1)));
    }

    [Fact]
    public void CompensationIsBoundedPerObjectAndMustReleaseBeforePrimary()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 1);
        var token = Token(Session(1), ObjectId(), 1);
        var identity = Furniture("furniture:indoor:6000");
        var primary = Handle(token.ObjectId, RscObjectKind.Furniture, 1);
        var compensation = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            ObjectId(),
            RscObjectKind.Light,
            99);
        Assert.True(ledger.TryReserve(token, identity).Accepted);
        Assert.True(ledger.TryRecordSpawn(token, identity, primary));

        Assert.True(ledger.TryRecordCompensation(token, compensation));
        Assert.False(ledger.TryRecordCompensation(
            token,
            compensation with { OwnershipId = Guid.NewGuid() }));
        Assert.Equal(1, ledger.OwnedCount);
        Assert.Equal(1, ledger.CompensationCount);
        Assert.False(ledger.TryBeginRelease(token, out _));
        Assert.True(ledger.TryBeginCompensationRelease(token, out var claimed));
        Assert.Equal(compensation, claimed);
        Assert.True(ledger.TryCompleteCompensationRelease(token, claimed));
        Assert.Equal(0, ledger.CompensationCount);
        Assert.True(ledger.TryBeginRelease(token, out var primaryClaimed));
        Assert.Equal(primary, primaryClaimed);
    }

    [Fact]
    public void SpawnMutationClaimFencesRemovalAndGenerationReplacement()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 1);
        var session = Session(1);
        var objectId = ObjectId();
        var token = Token(session, objectId, 1);
        var identity = Furniture("furniture:indoor:6000");
        Assert.True(ledger.TryReserve(token, identity).Accepted);

        Assert.True(ledger.TryBeginSpawnMutation(token, identity, 1, out var claimed));
        Assert.Equal(token, claimed.Token);
        Assert.False(ledger.TryGet(token, out _));
        Assert.False(ledger.TryRemoveEmpty(token));
        Assert.False(ledger.TryRecordSpawn(
            token,
            identity,
            Handle(objectId, RscObjectKind.Furniture, 1)));
        Assert.Equal(
            LocalObjectReservationStatus.MutationInProgress,
            ledger.TryReserve(Token(session, objectId, 2), identity).Status);

        Assert.True(ledger.TryEndMutation(token));
        Assert.True(ledger.TryGet(token, out var available));
        Assert.Equal(LocalObjectReconcileAction.Spawn, available.Action);
    }

    [Fact]
    public void SpawnMutationTransitionsAtomicallyIntoExactCompensationRelease()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 1);
        var token = Token(Session(1), ObjectId(), 1);
        var identity = Furniture("furniture:indoor:6000");
        var compensation = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            ObjectId(),
            RscObjectKind.Light,
            99);
        Assert.True(ledger.TryReserve(token, identity).Accepted);
        Assert.True(ledger.TryBeginSpawnMutation(token, identity, 1, out _));
        Assert.True(ledger.TryRecordCompensationMutation(token, compensation));

        Assert.False(ledger.TryBeginCompensationRelease(token, out _));
        Assert.True(ledger.TryTransitionMutationToCompensationRelease(
            token,
            out var claimed));
        Assert.Equal(compensation, claimed);
        Assert.False(ledger.TryEndMutation(token));
        Assert.True(ledger.TryCompleteCompensationRelease(token, claimed));
        Assert.True(ledger.TryRemoveEmpty(token));
    }

    [Fact]
    public void StaleExpectedCompensationCannotClaimReplacementReceipt()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 1);
        var token = Token(Session(1), ObjectId(), 1);
        var identity = Furniture("furniture:indoor:6000");
        var first = Handle(ObjectId(), RscObjectKind.Light, 1);
        var replacement = Handle(ObjectId(), RscObjectKind.Light, 2);
        Assert.True(ledger.TryReserve(token, identity).Accepted);
        Assert.True(ledger.TryRecordCompensation(token, first));
        Assert.True(ledger.TryBeginCompensationRelease(token, first, out var claimed));
        Assert.Equal(first, claimed);
        Assert.True(ledger.TryCompleteCompensationRelease(token, claimed));
        Assert.True(ledger.TryRecordCompensation(token, replacement));

        Assert.False(ledger.TryBeginCompensationRelease(token, first, out _));
        Assert.True(ledger.TryBeginCompensationRelease(token, replacement, out claimed));
        Assert.Equal(replacement, claimed);
        Assert.True(ledger.TryEndRelease(token));
    }

    [Fact]
    public void SessionBatchIsBoundedFairAndDoesNotTouchNewSession()
    {
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 3);
        var oldSession = Session(1);
        var newSession = Session(2);
        foreach (var session in new[] { oldSession, oldSession, newSession })
        {
            var id = ObjectId();
            Assert.True(ledger.TryReserve(
                Token(session, id, 1),
                Furniture("furniture:indoor:6000")).Accepted);
        }

        var first = Assert.Single(ledger.GetSessionBatch(oldSession, 1));
        var second = Assert.Single(ledger.GetSessionBatch(oldSession, 1));

        Assert.NotEqual(first.Token.ObjectId, second.Token.ObjectId);
        Assert.Equal(2, ledger.GetSessionEntryCount(oldSession));
        Assert.Equal(1, ledger.GetSessionEntryCount(newSession));
    }

    [Fact]
    public void ConcurrentAdmissionCannotExceedCapacity()
    {
        const int maximum = 16;
        var ledger = new LocalObjectOwnershipLedger(maximum);
        var session = Session(1);
        var accepted = 0;

        Parallel.For(0, 128, _ =>
        {
            var result = ledger.TryReserve(
                Token(session, ObjectId(), 1),
                Furniture("furniture:indoor:6000"));
            if (result.Accepted)
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(maximum, accepted);
        Assert.Equal(maximum, ledger.Count);
    }

    [Fact]
    public async Task ConcurrentDuplicateReceiptCanBelongToOnlyOneObject()
    {
        var ledger = new LocalObjectOwnershipLedger(2);
        var session = Session(1);
        var firstId = ObjectId();
        var secondId = ObjectId();
        var first = Token(session, firstId, 1);
        var second = Token(session, secondId, 1);
        var identity = Furniture("furniture:indoor:6000");
        var ownershipId = Guid.NewGuid();
        Assert.True(ledger.TryReserve(first, identity).Accepted);
        Assert.True(ledger.TryReserve(second, identity).Accepted);
        using var start = new ManualResetEventSlim(false);

        var attempts = new[]
        {
            Task.Run(() =>
            {
                start.Wait();
                return ledger.TryRecordSpawn(
                    first,
                    identity,
                    new LocalObjectInstanceHandle(
                        ownershipId,
                        firstId,
                        RscObjectKind.Furniture,
                        1));
            }),
            Task.Run(() =>
            {
                start.Wait();
                return ledger.TryRecordSpawn(
                    second,
                    identity,
                    new LocalObjectInstanceHandle(
                        ownershipId,
                        secondId,
                        RscObjectKind.Furniture,
                        1));
            }),
        };
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
    }

    private static ReceiverSessionKey Session(long epoch) => new(epoch, 1, epoch);

    private static RscObjectId ObjectId() => new(Guid.NewGuid());

    private static LocalObjectApplyToken Token(
        ReceiverSessionKey session,
        RscObjectId objectId,
        long generation) => new(session, objectId, new ApplyGeneration(generation));

    private static LocalObjectSpawnIdentity Furniture(string identity) =>
        new(RscObjectKind.Furniture, identity);

    private static LocalObjectInstanceHandle Handle(
        RscObjectId objectId,
        RscObjectKind kind,
        long generation) => new(Guid.NewGuid(), objectId, kind, generation);
}
