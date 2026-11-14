using System.Numerics;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Location;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Furniture;

/// <summary>
/// Builds furniture creates, moves and deletes and sends them to the server.
/// Nothing is drawn from here: a change is drawn when it comes back through
/// the event stream, exactly like another player's change. Creates go into
/// the scene of the player's current place and carry its complete location.
/// </summary>
public sealed class FurnitureEditor
{
    private readonly Func<object, CancellationToken, Task<ServerCommandResult>> submit;

    public FurnitureEditor(Func<object, CancellationToken, Task<ServerCommandResult>> submit)
    {
        ArgumentNullException.ThrowIfNull(submit);
        this.submit = submit;
    }

    public Task<ServerCommandResult> PlaceAsync(
        SceneLocation? here,
        string identity,
        Vector3 position,
        float yawRadians,
        CancellationToken cancellationToken = default)
    {
        if (here is not { IsValid: true } location || LocationScenes.SceneFor(location) is not { } scene)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Your location is not known."));
        if (!FurnitureIdentity.IsCanonical(identity))
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Not a furniture item."));
        var payload = new FurnitureCreatePayload(scene, identity, ToState(position), Yaw(yawRadians), location);
        var request = new CreateObjectRequest(Guid.NewGuid(), payload);
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    /// <summary>Moves by <paramref name="offset"/> and turns by <paramref name="yawRadians"/> around the vertical axis.</summary>
    public Task<ServerCommandResult> NudgeAsync(
        FurnitureState furniture,
        Vector3 offset,
        float yawRadians,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(furniture);
        Vector3State? position = offset == Vector3.Zero
            ? null
            : ToState(FurnitureLocalObjectHost.ToVector(furniture.Position) + offset);
        RotationState? rotation = yawRadians == 0
            ? null
            : ToState(Quaternion.Normalize(
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians) *
                FurnitureLocalObjectHost.ToQuaternion(furniture.Rotation)));
        if (position is null && rotation is null)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Nothing to change."));
        var request = new UpdateObjectRequest(
            Guid.NewGuid(),
            furniture.ObjectId,
            furniture.Revision,
            new FurnitureTransformPatch(position, rotation));
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    /// <summary>
    /// Sets the transform, based on exactly the revision the change was made
    /// from (a newer revision on the server means a conflict). Only changed
    /// parts are sent; nothing changed sends nothing. Invalid values are
    /// refused here. Furniture has no scale.
    /// </summary>
    public Task<ServerCommandResult> SetTransformAsync(
        FurnitureState basis,
        Vector3 position,
        Quaternion rotation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        if (!FurnitureTransforms.IsValidPosition(position) ||
            !FurnitureTransforms.TryNormalize(rotation, out var normalized))
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Invalid transform."));
        var basisPosition = FurnitureLocalObjectHost.ToVector(basis.Position);
        var basisRotation = FurnitureLocalObjectHost.ToQuaternion(basis.Rotation);
        Vector3State? newPosition = Vector3.Distance(basisPosition, position) < FurnitureTransforms.PositionEpsilon
            ? null
            : ToState(position);
        RotationState? newRotation = FurnitureTransforms.AngleBetween(basisRotation, normalized) < FurnitureTransforms.AngleEpsilon
            ? null
            : ToState(normalized);
        if (newPosition is null && newRotation is null)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Nothing to change."));
        var request = new UpdateObjectRequest(
            Guid.NewGuid(),
            basis.ObjectId,
            basis.Revision,
            new FurnitureTransformPatch(newPosition, newRotation));
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    /// <summary>
    /// The end of a gizmo drag for either kind: furniture takes position and
    /// rotation (<see cref="SetTransformAsync"/>); a point light takes only
    /// the position.
    /// </summary>
    public Task<ServerCommandResult> SetPlacementAsync(
        AuthoritativeObjectState basis,
        Vector3 position,
        Quaternion rotation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        return basis switch
        {
            FurnitureState furniture => SetTransformAsync(furniture, position, rotation, cancellationToken),
            LightState light when FurnitureTransforms.IsValidPosition(position) =>
                SetLightAsync(light, new LightPointPatch(ToState(position), null, null, null), cancellationToken),
            ActorState actor when FurnitureTransforms.IsValidPosition(position) &&
                FurnitureTransforms.TryNormalize(rotation, out var turned) =>
                SetActorTransformAsync(
                    actor,
                    new ActorTransformPatch(ToState(position), ActorLocalObjectHost.YawOf(turned), null),
                    cancellationToken),
            _ => Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Invalid transform.")),
        };
    }

    /// <summary>
    /// Places a new point light (protocol 5) at <paramref name="position"/>
    /// in the scene of the player's current place, with the default colour,
    /// brightness and range.
    /// </summary>
    public Task<ServerCommandResult> PlaceLightAsync(
        SceneLocation? here,
        Vector3 position,
        CancellationToken cancellationToken = default)
    {
        if (here is not { IsValid: true } location || LocationScenes.SceneFor(location) is not { } scene)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Your location is not known."));
        if (!FurnitureTransforms.IsValidPosition(position))
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Invalid position."));
        var payload = new LightCreatePayload(scene, LightIdentity.Point, ToState(position), LightLimits.UnitScale, location);
        var request = new CreateObjectRequest(Guid.NewGuid(), payload);
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    /// <summary>
    /// One atomic light update (protocol 5), based on exactly the shown
    /// revision. Fields equal to the shown state are left out; nothing
    /// changed sends nothing. Out-of-bounds values are refused here.
    /// </summary>
    public Task<ServerCommandResult> SetLightAsync(
        LightState basis,
        LightPointPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(patch);
        var minimal = new LightPointPatch(
            patch.Position is { } position && position != basis.Position ? position : null,
            patch.Color is { } color && color != basis.Color ? color : null,
            patch.Intensity is { } intensity && intensity != basis.Intensity ? intensity : null,
            patch.Range is { } range && range != basis.Range ? range : null);
        if (minimal.Position is null && minimal.Color is null && minimal.Intensity is null && minimal.Range is null)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Nothing to change."));
        if (!minimal.IsValid)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "A light value is out of range."));
        var request = new UpdateObjectRequest(Guid.NewGuid(), basis.ObjectId, basis.Revision, minimal);
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    /// <summary>
    /// Places a new actor (protocol 6) in the scene of the player's current
    /// place, at <paramref name="position"/> facing <paramref name="yawRadians"/>,
    /// at scale 1 with default controls. <paramref name="bnpcBaseId"/> is the
    /// only identity sent.
    /// </summary>
    public Task<ServerCommandResult> PlaceActorAsync(
        SceneLocation? here,
        uint bnpcBaseId,
        Vector3 position,
        float yawRadians,
        CancellationToken cancellationToken = default)
    {
        if (here is not { IsValid: true } location || LocationScenes.SceneFor(location) is not { } scene)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Your location is not known."));
        if (bnpcBaseId == 0)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Not a creature."));
        var state = ToState(position);
        if (!ActorLimits.IsValidPosition(state))
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Invalid position."));
        var payload = new ActorCreatePayload(
            scene, bnpcBaseId, state, ActorLimits.NormalizeYaw(yawRadians), ActorLimits.DefaultScale, location);
        var request = new CreateObjectRequest(Guid.NewGuid(), payload);
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    /// <summary>
    /// One actor transform update (protocol 6), based on exactly the shown
    /// revision. Fields equal to the shown state are left out; nothing changed
    /// sends nothing. Out-of-bounds values are refused here.
    /// </summary>
    public Task<ServerCommandResult> SetActorTransformAsync(
        ActorState basis,
        ActorTransformPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(patch);
        var yaw = patch.Yaw is { } requested ? ActorLimits.NormalizeYaw(requested) : (float?)null;
        var minimal = new ActorTransformPatch(
            patch.Position is { } position &&
                Vector3.Distance(FurnitureLocalObjectHost.ToVector(position), FurnitureLocalObjectHost.ToVector(basis.Position)) >=
                FurnitureTransforms.PositionEpsilon
                ? position
                : null,
            yaw is { } turned && MathF.Abs(ActorLimits.NormalizeYaw(turned - basis.Yaw)) >= FurnitureTransforms.AngleEpsilon
                ? turned
                : null,
            patch.Scale is { } scale && scale != basis.Scale ? scale : null);
        if (minimal.Position is null && minimal.Yaw is null && minimal.Scale is null)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Nothing to change."));
        if ((minimal.Position is { } newPosition && !ActorLimits.IsValidPosition(newPosition)) ||
            (minimal.Yaw is { } newYaw && !ActorLimits.IsValidYaw(newYaw)) ||
            (minimal.Scale is { } newScale && !ActorLimits.IsValidScale(newScale)))
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "An actor value is out of range."));
        var request = new UpdateObjectRequest(Guid.NewGuid(), basis.ObjectId, basis.Revision, minimal);
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    /// <summary>
    /// One actor control update, based on exactly the shown revision: an
    /// allowlisted loop or its clear. A timeline row the server's allowlist
    /// does not list for this creature is refused by the server; the UI offers
    /// only allowlisted loops. Nothing changed sends nothing. RSC has no
    /// freeze and no look-at: a patch carrying
    /// either reserved field is refused here and never sent.
    /// </summary>
    public Task<ServerCommandResult> SetActorControlsAsync(
        ActorState basis,
        ActorControlPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(patch);
        // The reserved look-at field: no local code path may set or clear it.
        if (patch.LookAt is not null || patch.ClearLookAt)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Look-at is not supported."));
        // The reserved freeze field: no local code path may set or clear it.
        if (patch.Frozen is not null)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Freeze is not supported."));
        var minimal = new ActorControlPatch(
            null,
            null,
            false,
            patch.TimelineId,
            patch.ClearTimeline && basis.Controls.TimelineId is not null);
        if (minimal.TimelineId is null && !minimal.ClearTimeline)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Nothing to change."));
        if (!minimal.IsValid)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "An actor control is out of range."));
        var request = new UpdateObjectRequest(Guid.NewGuid(), basis.ObjectId, basis.Revision, minimal);
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

#if RSC_DEVELOPMENT
    /// <summary>
    /// HV-6, Debug only (a Release build never sends a human create): places
    /// a shared human actor (protocol 7) from one of the caller's own,
    /// group-published, format-3 bundles, in the scene of the player's
    /// current place. The bundle id is the only appearance identity sent; the
    /// server checks the bundle.
    /// </summary>
    public Task<ServerCommandResult> PlaceHumanAsync(
        SceneLocation? here,
        string bundleId,
        Vector3 position,
        float yawRadians,
        CancellationToken cancellationToken = default)
    {
        if (here is not { IsValid: true } location || LocationScenes.SceneFor(location) is not { } scene)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Your location is not known."));
        if (!HumanActorLimits.IsValidBundleId(bundleId))
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Not a bundle id."));
        var state = ToState(position);
        if (!ActorLimits.IsValidPosition(state))
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Invalid position."));
        var payload = new HumanActorCreatePayload(scene, bundleId, state, ActorLimits.NormalizeYaw(yawRadians), location);
        var request = new CreateObjectRequest(Guid.NewGuid(), payload);
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    /// <summary>
    /// HV-6, Debug only: one shared human transform update (position and yaw
    /// only), based on exactly the shown revision. Nothing changed sends nothing.
    /// </summary>
    public Task<ServerCommandResult> SetHumanTransformAsync(
        HumanActorState basis,
        Vector3 position,
        float yawRadians,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        var target = ToState(position);
        var yaw = ActorLimits.NormalizeYaw(yawRadians);
        var minimal = new HumanTransformPatch(
            Vector3.Distance(position, FurnitureLocalObjectHost.ToVector(basis.Position)) >= FurnitureTransforms.PositionEpsilon ? target : null,
            MathF.Abs(ActorLimits.NormalizeYaw(yaw - basis.Yaw)) >= FurnitureTransforms.AngleEpsilon ? yaw : null);
        if (minimal.Position is null && minimal.Yaw is null)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Nothing to change."));
        if (!minimal.IsValid)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "A human actor value is out of range."));
        var request = new UpdateObjectRequest(Guid.NewGuid(), basis.ObjectId, basis.Revision, minimal);
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }
#endif

    /// <summary>
    /// Sets the stain (0 = Default), based on exactly the shown revision.
    /// Selecting the stain the item already has sends nothing.
    /// </summary>
    public Task<ServerCommandResult> SetStainAsync(
        FurnitureState basis,
        byte stain,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        if (basis.Stain == stain)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Nothing to change."));
        var request = new UpdateObjectRequest(
            Guid.NewGuid(),
            basis.ObjectId,
            basis.Revision,
            new FurnitureStainPatch(stain));
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    public Task<ServerCommandResult> DeleteAsync(
        AuthoritativeObjectState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var request = new DeleteObjectRequest(Guid.NewGuid(), state.ObjectId, state.Revision);
        return request.IsValid
            ? submit(request, cancellationToken)
            : Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
    }

    /// <summary>A rotation of <paramref name="radians"/> around the vertical axis (the game's facing).</summary>
    public static RotationState Yaw(float radians) =>
        ToState(Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.IsFinite(radians) ? radians : 0));

    /// <summary>The facing of a rotation around the vertical axis, in degrees (0-360).</summary>
    public static float YawDegrees(RotationState rotation)
    {
        var q = FurnitureLocalObjectHost.ToQuaternion(rotation);
        var radians = MathF.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.Y * q.Y + q.X * q.X));
        var degrees = radians * 180 / MathF.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static Vector3State ToState(Vector3 value) => new(value.X, value.Y, value.Z);

    private static RotationState ToState(Quaternion value) => new(value.X, value.Y, value.Z, value.W);
}
