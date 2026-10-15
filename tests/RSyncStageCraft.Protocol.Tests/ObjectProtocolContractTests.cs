using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

public sealed class ObjectProtocolContractTests
{
    [Fact]
    public void FurnitureAndLightShapesPreserveSettledTransformRules()
    {
        Assert.DoesNotContain(
            typeof(FurnitureState).GetProperties(),
            property => property.Name == "Scale");
        Assert.DoesNotContain(
            typeof(FurnitureTransformPatch).GetProperties(),
            property => property.Name == "Scale");
        Assert.DoesNotContain(
            typeof(FurnitureCreatePayload).GetProperties(),
            property => property.Name == "Scale");
        Assert.DoesNotContain(
            typeof(LightState).GetProperties(),
            property => property.Name == "Rotation");
        Assert.DoesNotContain(
            typeof(LightPointPatch).GetProperties(),
            property => property.Name == "Rotation");
        Assert.DoesNotContain(
            typeof(LightCreatePayload).GetProperties(),
            property => property.Name == "Rotation");
    }

    [Fact]
    public void EmptyAndCrossAppearancePatchesAreRejected()
    {
        Assert.False(new ActorTransformPatch(null, null, null).IsValid);
        Assert.False(new FurnitureTransformPatch(null, null).IsValid);
        Assert.False(new LightPointPatch(null, null, null, null).IsValid);
        // Protocol 6: the appearance patch is gone; setting and clearing the
        // same control together is the contradictory actor patch now.
        Assert.False(new ActorControlPatch(null, new Vector3State(1, 2, 3), true, null, false).IsValid);
        Assert.False(new ActorControlPatch(null, null, false, 7, true).IsValid);
    }

    [Fact]
    public void SnapshotRequiresUniqueLiveAndTombstonedObjectIds()
    {
        var actor = new ActorState(new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), new RscUserId("owner"),
            TestLocations.NewScene(), 4242, new Vector3State(1, 2, 3), 0, 1, ActorControlState.Default)
        {
            Location = TestLocations.Default,
        };
        var duplicateTombstone = new ObjectTombstone(
            actor.ObjectId,
            RscObjectKind.Actor,
            new ObjectRevision(2));

        var snapshot = new ObjectSnapshotMessage(
            new ServerSequence(10),
            new AuthoritativeObjectState[] { actor },
            new[] { duplicateTombstone });

        Assert.False(snapshot.IsValid);
    }

    [Fact]
    public void UpdateRequestCarriesStableRequestIdentityAndBaseRevision()
    {
        var request = new UpdateObjectRequest(
            Guid.NewGuid(),
            new RscObjectId(Guid.NewGuid()),
            new ObjectRevision(3),
            new FurnitureTransformPatch(
                new Vector3State(1, 2, 3),
                null));

        Assert.True(request.IsValid);
        Assert.Equal(RscObjectKind.Furniture, request.Patch.Kind);
    }

    [Fact]
    public void CreateRequestCarriesIdempotencyIdentityButNoServerOwnedIdentity()
    {
        var request = new CreateObjectRequest(
            Guid.NewGuid(),
            new FurnitureCreatePayload(
                TestLocations.NewScene(),
                "furniture:indoor:6000",
                new Vector3State(1, 2, 3),
                new RotationState(0, 0, 0, 1),
                TestLocations.Default));

        Assert.True(request.IsValid);
        Assert.DoesNotContain(
            request.Payload.GetType().GetProperties(),
            property => property.Name is "ObjectId" or "Revision" or "OwnerId");
    }

    [Fact]
    public void SessionEnvelopeRejectsProtocolOrSessionMismatchShapes()
    {
        var valid = new SessionMessageEnvelope(
            ProtocolConstants.CurrentVersion,
            ObjectProtocolMessageType.UpdateObject,
            Guid.NewGuid(),
            SessionId: 7,
            SessionGeneration: 3,
            ConnectionEpoch: 11);
        var wrongProtocol = valid with { ProtocolVersion = ProtocolConstants.CurrentVersion + 1 };
        var missingSession = valid with { SessionId = 0 };
        var missingConnection = valid with { ConnectionEpoch = 0 };

        Assert.True(valid.IsValid);
        Assert.False(wrongProtocol.IsValid);
        Assert.False(missingSession.IsValid);
        Assert.False(missingConnection.IsValid);
    }

    [Fact]
    public void AuthoritativeStreamRequestsAreIdentityBoundAndBounded()
    {
        var snapshot = new ObjectSnapshotRequest(Guid.NewGuid());
        var maximum = new ObjectEventBatchRequest(
            Guid.NewGuid(),
            new ServerSequence(0),
            ProtocolLimits.MaximumAuthoritativeObjectEvents);

        Assert.True(snapshot.IsValid);
        Assert.True(maximum.IsValid);
        Assert.False(new ObjectSnapshotRequest(Guid.Empty).IsValid);
        Assert.False((maximum with { RequestId = Guid.Empty }).IsValid);
        Assert.False((maximum with { After = new ServerSequence(-1) }).IsValid);
        Assert.False((maximum with { MaximumEvents = 0 }).IsValid);
        Assert.False((maximum with
        {
            MaximumEvents = ProtocolLimits.MaximumAuthoritativeObjectEvents + 1,
        }).IsValid);
    }

    [Fact]
    public void SceneCommandsCarryNoCallerOwnedPathIdentityOrTime()
    {
        var save = new SaveSceneRequest(
            Guid.NewGuid(),
            new RscSceneId(Guid.NewGuid()),
            new ServerSequence(4));
        var read = new ReadSceneRequest(Guid.NewGuid(), save.SceneId);

        Assert.True(save.IsValid);
        Assert.True(read.IsValid);
        Assert.False((save with { RequestId = Guid.Empty }).IsValid);
        Assert.False((save with { SceneId = default }).IsValid);
        Assert.False((save with { ExpectedSequence = new ServerSequence(-1) }).IsValid);
        Assert.False((read with { RequestId = Guid.Empty }).IsValid);
        Assert.False((read with { SceneId = default }).IsValid);
        Assert.DoesNotContain(
            typeof(SaveSceneRequest).GetProperties()
                .Concat(typeof(ReadSceneRequest).GetProperties()),
            property => property.Name.Contains("Path", StringComparison.Ordinal) ||
                property.Name.Contains("Owner", StringComparison.Ordinal) ||
                property.Name.Contains("Time", StringComparison.Ordinal));
    }
}
