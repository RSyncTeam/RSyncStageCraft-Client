#nullable enable
using System.Numerics;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

/// <summary>Placing and editing actors from the UI (editor and draft).</summary>
public sealed class ActorEditingTests
{
    [Fact]
    public async Task PlaceSendsOnlyTheBNpcBaseIdAtThePlayersPositionAndFacing()
    {
        var sent = new List<object>();
        var editor = Editor(sent);
        var result = await editor.PlaceActorAsync(TestLocations.Default, 4242, new Vector3(1, 2, 3), 4f);
        Assert.True(result.Completed);
        var create = Assert.IsType<CreateObjectRequest>(Assert.Single(sent));
        var payload = Assert.IsType<ActorCreatePayload>(create.Payload);
        Assert.Equal(4242u, payload.BNpcBaseId);
        Assert.Equal(new Vector3State(1, 2, 3), payload.Position);
        Assert.Equal(ActorLimits.NormalizeYaw(4f), payload.Yaw, 5);
        Assert.Equal(1f, payload.Scale);
        Assert.Equal(TestLocations.Default, payload.Location);

        Assert.Equal(ServerCommandStatus.Invalid, (await editor.PlaceActorAsync(TestLocations.Default, 0, Vector3.Zero, 0)).Status);
        Assert.Equal(ServerCommandStatus.Invalid, (await editor.PlaceActorAsync(null, 1, Vector3.Zero, 0)).Status);
        Assert.Single(sent);
    }

    [Fact]
    public async Task TransformUpdatesSendOnlyChangedValidFields()
    {
        var sent = new List<object>();
        var editor = Editor(sent);
        var actor = Actor();
        Assert.Equal(ServerCommandStatus.Invalid,
            (await editor.SetActorTransformAsync(actor, new ActorTransformPatch(actor.Position, actor.Yaw, actor.Scale))).Status);
        Assert.Equal(ServerCommandStatus.Invalid,
            (await editor.SetActorTransformAsync(actor, new ActorTransformPatch(null, null, 5f))).Status);
        Assert.Empty(sent);

        Assert.True((await editor.SetActorTransformAsync(actor, new ActorTransformPatch(actor.Position, 1f, 2f))).Completed);
        var update = Assert.IsType<UpdateObjectRequest>(Assert.Single(sent));
        var patch = Assert.IsType<ActorTransformPatch>(update.Patch);
        Assert.Null(patch.Position);
        Assert.Equal(1f, patch.Yaw);
        Assert.Equal(2f, patch.Scale);
        Assert.Equal(actor.Revision, update.BaseRevision);
    }

    [Fact]
    public async Task AGizmoDragOnAnActorSendsPositionAndYawOnly()
    {
        var sent = new List<object>();
        var editor = Editor(sent);
        var actor = Actor();
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.5f);
        Assert.True((await editor.SetPlacementAsync(actor, new Vector3(4, 5, 6), rotation)).Completed);
        var patch = Assert.IsType<ActorTransformPatch>(Assert.IsType<UpdateObjectRequest>(Assert.Single(sent)).Patch);
        Assert.Equal(new Vector3State(4, 5, 6), patch.Position);
        Assert.Equal(1.5f, patch.Yaw!.Value, 4);
        Assert.Null(patch.Scale);
    }

    [Fact]
    public void TheDraftRefusesOutOfRangeValuesAndBuildsOnePatchFromItsBasis()
    {
        var draft = new ActorDraft();
        var actor = Actor();
        draft.Sync(actor, locked: false);
        Assert.Equal(FurnitureDraftApplyStatus.NothingChanged, draft.TryApply().Status);

        draft.Edit(new Vector3(1, 2, 3), draft.Facing, 4f);
        Assert.Equal(FurnitureDraftApplyStatus.Invalid, draft.TryApply().Status);

        draft.Edit(new Vector3(1, 2, 3.5f), 90, 2f);
        var ready = draft.TryApply();
        Assert.Equal(FurnitureDraftApplyStatus.Ready, ready.Status);
        Assert.Same(actor, ready.Basis);
        Assert.Equal(new Vector3State(1, 2, 3.5f), ready.Patch!.Position);
        Assert.Equal(MathF.PI / 2, ready.Patch.Yaw!.Value, 4);
        Assert.Equal(2f, ready.Patch.Scale);

        draft.Edit(new Vector3(9, 9, 9), 0, 1);
        draft.Sync(actor with { Revision = new(2) }, locked: false);
        Assert.Equal(FurnitureDraftApplyStatus.Stale, draft.TryApply().Status);
    }

    private static FurnitureEditor Editor(List<object> sent) => new((request, _) =>
    {
        sent.Add(request);
        return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Completed,
            new ObjectMutationCompletion(Guid.NewGuid(), ObjectMutationCompletionKind.Update, false, new(1), null, null)));
    });

    [Fact]
    public void OwnerNamesAreDisplayOnlyAndNeverDecideEditing()
    {
        var actor = new ActorState(new(Guid.NewGuid()), new(1), new("alice"), TestLocations.SharedScene,
            4242, new(1, 2, 3), 0, 1, ActorControlState.Default) { Location = TestLocations.Default };
        var directory = new OwnerDisplayNameDirectory();
        directory.Observe(new ObjectCreatedMessage(new(1), actor, new(new("alice"), "Ada")));
        Assert.Equal("Ada", directory.NameOf(new("alice")));
        Assert.Equal("Ada", ActorOwnership.OwnerLabel(actor, directory.NameOf));
        // Bob's client knows Alice's actor as "Ada"; Bob being "Ada" too changes nothing.
        Assert.False(ActorOwnership.IsEditableBy(actor, new RscUserId("bob")));
        // A later message without a name clears it (the account has none now).
        directory.Observe(new ObjectUpdatedMessage(new(2), actor with { Revision = new(2) }));
        Assert.Null(directory.NameOf(new("alice")));
        Assert.Equal("alice", ActorOwnership.OwnerLabel(actor, directory.NameOf));
        Assert.False(ActorOwnership.IsEditableBy(actor, null));
    }

    private static ActorState Actor() => new(new(Guid.NewGuid()), new(1), new("owner"), TestLocations.SharedScene, 4242,
        new(1, 2, 3), 0.25f, 1f, ActorControlState.Default);
}
