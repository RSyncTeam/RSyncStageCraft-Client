using System.Numerics;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Furniture;

public enum FurnitureGizmoMode
{
    /// <summary>Move along the world X, Y and Z axes.</summary>
    Move,
    /// <summary>Turn around the vertical (Y) axis only.</summary>
    Rotate,
}

public enum FurnitureGizmoPhase
{
    Idle,
    /// <summary>The player is dragging; only the local instance moves.</summary>
    Dragging,
    /// <summary>The one update was sent; waiting for the server's answer.</summary>
    Committing,
}

/// <summary>
/// What the UI shows. <see cref="Selected"/> is only what the player picked;
/// <see cref="Busy"/> is the object a drag or a pending update belongs to,
/// and the preview transform is that object's, never the selection's.
/// </summary>
public sealed record FurnitureGizmoView(
    RscObjectId? Selected,
    FurnitureGizmoMode Mode,
    FurnitureGizmoPhase Phase,
    Vector3? PreviewPosition,
    Quaternion? PreviewRotation,
    string? Message)
{
    /// <summary>The object being dragged or saved, if any.</summary>
    public RscObjectId? Busy { get; init; }

    /// <summary>
    /// Furniture may be edited (typed transform, move to me, delete, place, a
    /// new drag) only while no drag or update is in progress or queued: an
    /// edit based on a revision that is about to change would be stale.
    /// </summary>
    public bool CanEdit => Phase == FurnitureGizmoPhase.Idle && Busy is null;

    /// <summary>Whether the gizmo is drawn on <paramref name="objectId"/>, and with which transform.</summary>
    public bool ShowsGizmoOn(RscObjectId objectId) => Phase switch
    {
        FurnitureGizmoPhase.Idle => Selected == objectId,
        // The drag's own object only; hidden while saving.
        FurnitureGizmoPhase.Dragging => Busy == objectId && Selected == objectId,
        _ => false,
    };
}

/// <summary>
/// Drag-to-edit for one selected, drawn piece of furniture. The UI
/// reports drag input from any thread; <see cref="Pump"/> runs on the game
/// thread and is the only caller of the furniture host's preview, which
/// alone touches the native object. During a drag only that local instance
/// moves and nothing is sent; on release exactly one ordinary update goes to
/// the server, based on the revision the drag started from, and only if
/// something changed.
/// <para>
/// Before release, any interruption (remote change or deletion, location or
/// connection change, host loss, selection or mode change, closing, unload)
/// cancels the preview and restores the latest authoritative state.
/// </para>
/// <para>
/// After release the update is in flight and cannot be called back, so the
/// operation is no longer cancellable: it keeps its preview until the outcome
/// is known. A successful reply is not authority (the event stream is), so it
/// stays Committing until the authoritative state for the object is applied;
/// a rejection restores the latest authoritative state. Selection, mode or
/// window changes meanwhile only hide the gizmo.
/// </para>
/// </summary>
public sealed class FurnitureGizmoController
{
    private readonly ILocalObjectPreviewHost host;
    private readonly FurnitureEditor editor;
    private readonly Func<long> sessionSerial;
    private readonly Func<RscObjectId, AuthoritativeObjectState?> authoritative;
    private readonly object gate = new();

    // UI-side requests, consumed by Pump.
    private AuthoritativeObjectState? pendingBegin;
    private (FurnitureState Basis, Vector3 Position, Quaternion Rotation)? pendingApply;
    private (Vector3 Position, Quaternion Rotation)? pendingMove;
    private bool pendingEnd;
    private string? pendingCancel;

    // Game-thread state (also read by View under the lock).
    private RscObjectId? selected;
    private FurnitureGizmoMode mode;
    private FurnitureGizmoPhase phase;
    private Guid previewId;
    private AuthoritativeObjectState? basis;
    private long basisSession;
    private Vector3? previewPosition;
    private Quaternion? previewRotation;
    private Task<ServerCommandResult>? commit;
    private bool accepted;
    // A dye or light-field update: committed without a preview, ended by the
    // authoritative event (a newer revision), not by the preview status.
    private bool awaitsEvent;
    private string changeNoun = "the change";
    private string? message;

    /// <param name="sessionSerial">
    /// The current signed-in connection's serial, or 0 while not connected. A
    /// change during a drag (drop or reconnect) cancels it.
    /// </param>
    /// <param name="authoritative">
    /// The object's current authoritative (desired) state on this client, or
    /// null when it is gone. A stain update stays saving until this shows a
    /// newer revision than the one it was based on (the event arrived).
    /// </param>
    public FurnitureGizmoController(
        ILocalObjectPreviewHost host,
        FurnitureEditor editor,
        Func<long> sessionSerial,
        Func<RscObjectId, AuthoritativeObjectState?>? authoritative = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(sessionSerial);
        this.host = host;
        this.editor = editor;
        this.sessionSerial = sessionSerial;
        this.authoritative = authoritative ?? (_ => null);
    }

    public FurnitureGizmoView View
    {
        get
        {
            lock (gate)
            {
                return new(selected, mode, phase, previewPosition, previewRotation, message)
                {
                    Busy = basis?.ObjectId ?? pendingBegin?.ObjectId ?? pendingApply?.Basis.ObjectId,
                };
            }
        }
    }

    /// <summary>Selecting another object (or none) cancels a preview in progress.</summary>
    public void Select(RscObjectId? objectId)
    {
        lock (gate)
        {
            if (selected == objectId)
                return;
            selected = objectId;
            RequestCancelUnsafe(null);
        }
    }

    /// <summary>Changing the mode cancels a preview in progress.</summary>
    public void SetMode(FurnitureGizmoMode next)
    {
        lock (gate)
        {
            if (mode == next || !Enum.IsDefined(next))
                return;
            mode = next;
            RequestCancelUnsafe(null);
        }
    }

    /// <summary>The gizmo was grabbed on <paramref name="state"/> (the state the UI shows).</summary>
    public void BeginDrag(AuthoritativeObjectState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (gate)
        {
            if (phase != FurnitureGizmoPhase.Idle || pendingBegin is not null || pendingApply is not null ||
                selected != state.ObjectId)
                return;
            pendingBegin = state;
            pendingMove = null;
            pendingEnd = false;
            pendingCancel = null;
            message = null;
            previewPosition = PositionOf(state);
            previewRotation = RotationOf(state);
        }
    }

    /// <summary>
    /// The gizmo's current transform. Only the part the mode allows is used:
    /// Move keeps the rotation, Rotate keeps the position and takes only the
    /// heading. Invalid values are ignored.
    /// </summary>
    public void Drag(Vector3 position, Quaternion rotation)
    {
        lock (gate)
        {
            var start = basis ?? pendingBegin;
            if (start is null || (phase != FurnitureGizmoPhase.Dragging && pendingBegin is null) || pendingEnd)
                return;
            var startPosition = PositionOf(start);
            var startRotation = RotationOf(start);
            Vector3 nextPosition;
            Quaternion nextRotation;
            // A point light has no rotation: it only ever moves.
            if (mode == FurnitureGizmoMode.Move || start is LightState)
            {
                if (!FurnitureTransforms.IsValidPosition(position))
                    return;
                nextPosition = position;
                nextRotation = startRotation;
            }
            else
            {
                if (!FurnitureTransforms.TryNormalize(rotation, out var normalized))
                    return;
                nextPosition = startPosition;
                nextRotation = FurnitureTransforms.WithYawOf(startRotation, normalized);
            }
            pendingMove = (nextPosition, nextRotation);
            previewPosition = nextPosition;
            previewRotation = nextRotation;
        }
    }

    /// <summary>
    /// The typed transform fields were applied (<see cref="FurnitureTransformDraft"/>).
    /// This takes the same path as a released drag: on the game thread the
    /// drawn instance is set to the new transform, exactly one update is sent
    /// based on <paramref name="basis"/>, and the outcome is handled like a
    /// drag's (wait for the server's event; restore on rejection). Refused
    /// (returns false) while anything else is in progress or queued.
    /// </summary>
    public bool ApplyTransform(FurnitureState basis, Vector3 position, Quaternion rotation)
    {
        ArgumentNullException.ThrowIfNull(basis);
        if (!FurnitureTransforms.IsValidPosition(position) || !FurnitureTransforms.TryNormalize(rotation, out var normalized))
            return false;
        lock (gate)
        {
            if (phase != FurnitureGizmoPhase.Idle || pendingBegin is not null || pendingApply is not null ||
                selected != basis.ObjectId)
                return false;
            pendingApply = (basis, position, normalized);
            pendingCancel = null;
            message = null;
            return true;
        }
    }

    /// <summary>
    /// A dye was picked for the selected item. Sends exactly one stain
    /// update based on <paramref name="basis"/> and then locks all furniture
    /// editing (dye, transform fields, gizmo, move to me, delete) like a
    /// released drag: a successful reply alone keeps it saving until the
    /// authoritative state shows a newer revision; a rejection or a dropped
    /// connection ends it with a message. Nothing is previewed: the item is
    /// redrawn with the new dye when the authoritative event arrives. Picking
    /// the stain the item already has sends nothing. Returns whether an
    /// update was sent.
    /// </summary>
    public bool ApplyStain(FurnitureState basis, byte stain)
    {
        ArgumentNullException.ThrowIfNull(basis);
        lock (gate)
        {
            if (phase != FurnitureGizmoPhase.Idle || pendingBegin is not null || pendingApply is not null ||
                selected != basis.ObjectId || basis.Stain == stain)
                return false;
            message = null;
            phase = FurnitureGizmoPhase.Committing;
            this.basis = basis;
            awaitsEvent = true;
            changeNoun = "the dye";
            accepted = false;
            previewId = Guid.Empty;
            commit = editor.SetStainAsync(basis, stain);
            return true;
        }
    }

    /// <summary>
    /// The light's typed fields (position, colour, brightness, range) were
    /// applied. Exactly like <see cref="ApplyStain"/>: one atomic light
    /// update based on <paramref name="basis"/>, then all editing is locked
    /// until the authoritative state shows a newer revision; a rejection or a
    /// dropped connection ends it with a message. No local preview. A patch
    /// that changes nothing is refused here and sends nothing.
    /// </summary>
    public bool ApplyLight(LightState basis, LightPointPatch patch)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(patch);
        if (!patch.IsValid)
            return false;
        lock (gate)
        {
            if (phase != FurnitureGizmoPhase.Idle || pendingBegin is not null || pendingApply is not null ||
                selected != basis.ObjectId)
                return false;
            message = null;
            phase = FurnitureGizmoPhase.Committing;
            this.basis = basis;
            awaitsEvent = true;
            changeNoun = "the light";
            accepted = false;
            previewId = Guid.Empty;
            commit = editor.SetLightAsync(basis, patch);
            return true;
        }
    }

    /// <summary>
    /// The actor's typed fields (X/Y/Z, facing, scale) were applied.
    /// Exactly like <see cref="ApplyLight"/>: one actor transform update based
    /// on <paramref name="basis"/>, then all editing is locked until the
    /// authoritative state shows a newer revision; a rejection or a dropped
    /// connection ends it with a message. No local preview.
    /// </summary>
    public bool ApplyActor(ActorState basis, ActorTransformPatch patch)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(patch);
        lock (gate)
        {
            if (phase != FurnitureGizmoPhase.Idle || pendingBegin is not null || pendingApply is not null ||
                selected != basis.ObjectId)
                return false;
            message = null;
            phase = FurnitureGizmoPhase.Committing;
            this.basis = basis;
            awaitsEvent = true;
            changeNoun = "the actor";
            accepted = false;
            previewId = Guid.Empty;
            commit = editor.SetActorTransformAsync(basis, patch);
            return true;
        }
    }

    /// <summary>The gizmo was released.</summary>
    public void EndDrag()
    {
        lock (gate)
        {
            if (phase == FurnitureGizmoPhase.Dragging || pendingBegin is not null)
                pendingEnd = true;
        }
    }

    /// <summary>Cancels a preview in progress (window closed, unload, location change, ...).</summary>
    public void Cancel(string? reason = null)
    {
        lock (gate)
            RequestCancelUnsafe(reason);
    }

    /// <summary>Game thread, every frame. Applies the UI's requests to the host and sends the update.</summary>
    public void Pump()
    {
        lock (gate)
        {
            if (pendingCancel is { } reason)
            {
                pendingCancel = null;
                CancelUnsafe(reason.Length == 0 ? null : reason);
            }

            // Only an unreleased drag is cancelled by a connection change; a
            // released update waits for its outcome (a dropped connection
            // answers it as not delivered).
            if (phase == FurnitureGizmoPhase.Dragging && (basisSession == 0 || sessionSerial() != basisSession))
                CancelUnsafe("The connection changed; the move was discarded.");

            if (pendingBegin is { } begin)
            {
                pendingBegin = null;
                StartUnsafe(begin);
            }
            else if (pendingApply is { } apply)
            {
                pendingApply = null;
                ApplyUnsafe(apply.Basis, apply.Position, apply.Rotation);
            }

            switch (phase)
            {
                case FurnitureGizmoPhase.Dragging:
                    PumpDraggingUnsafe();
                    break;
                case FurnitureGizmoPhase.Committing:
                    PumpCommittingUnsafe();
                    break;
            }
        }
    }

    private static Vector3 PositionOf(AuthoritativeObjectState state) => state switch
    {
        FurnitureState furniture => FurnitureLocalObjectHost.ToVector(furniture.Position),
        LightState light => FurnitureLocalObjectHost.ToVector(light.Position),
        ActorState actor => FurnitureLocalObjectHost.ToVector(actor.Position),
        _ => Vector3.Zero,
    };

    private static Quaternion RotationOf(AuthoritativeObjectState state) => state switch
    {
        FurnitureState furniture => FurnitureLocalObjectHost.ToQuaternion(furniture.Rotation),
        ActorState actor => ActorLocalObjectHost.RotationOf(actor),
        _ => Quaternion.Identity,
    };

    private void StartUnsafe(AuthoritativeObjectState begin, string staleMessage = "That item just changed; try again.")
    {
        var session = sessionSerial();
        if (session == 0)
        {
            ResetUnsafe("Not connected.");
            return;
        }
        var started = host.BeginPreview(begin);
        if (!started.Started)
        {
            ResetUnsafe(started.Status switch
            {
                FurniturePreviewBeginStatus.Stale => staleMessage,
                FurniturePreviewBeginStatus.NotDrawn => "That item is not drawn here yet.",
                _ => "The item cannot be moved right now.",
            });
            return;
        }
        previewId = started.Id;
        basis = begin;
        basisSession = session;
        phase = FurnitureGizmoPhase.Dragging;
    }

    private void ApplyUnsafe(FurnitureState start, Vector3 position, Quaternion rotation)
    {
        StartUnsafe(start, "It changed meanwhile; not applied.");
        if (phase != FurnitureGizmoPhase.Dragging)
            return;
        if (!host.MovePreview(previewId, position, rotation))
        {
            if (host.PreviewStatus(previewId) == FurniturePreviewStatus.Faulted)
            {
                ResetUnsafe("The item hit a game error and is being redrawn; not applied.");
                return;
            }
            host.CancelPreview(previewId);
            ResetUnsafe("The item cannot be moved right now.");
            return;
        }
        var end = host.EndPreview(previewId);
        if (!end.Changed || end.Basis is null)
        {
            ResetUnsafe(host.PreviewStatus(previewId) == FurniturePreviewStatus.Faulted
                ? "The item hit a game error and is being redrawn; not applied."
                : null);
            return;
        }
        phase = FurnitureGizmoPhase.Committing;
        commit = editor.SetPlacementAsync(end.Basis, end.Position, end.Rotation);
    }

    private void PumpDraggingUnsafe()
    {
        switch (host.PreviewStatus(previewId))
        {
            case FurniturePreviewStatus.Superseded:
                ResetUnsafe("Someone else changed it; your move was discarded.");
                return;
            case FurniturePreviewStatus.Released:
                ResetUnsafe("The item is no longer here.");
                return;
            case FurniturePreviewStatus.Faulted:
                ResetUnsafe("The item hit a game error and is being redrawn; the move was discarded.");
                return;
            case FurniturePreviewStatus.Active:
                break;
            default:
                ResetUnsafe(null);
                return;
        }

        if (pendingMove is { } move)
        {
            pendingMove = null;
            host.MovePreview(previewId, move.Position, move.Rotation);
        }

        if (!pendingEnd)
            return;
        pendingEnd = false;
        var end = host.EndPreview(previewId);
        if (!end.Changed || end.Basis is null)
        {
            // A click without a move: nothing is sent.
            ResetUnsafe(null);
            return;
        }
        phase = FurnitureGizmoPhase.Committing;
        commit = editor.SetPlacementAsync(end.Basis, end.Position, end.Rotation);
    }

    private void PumpCommittingUnsafe()
    {
        if (awaitsEvent)
        {
            PumpStainUnsafe();
            return;
        }

        var status = host.PreviewStatus(previewId);
        if (!accepted)
        {
            if (commit is not { IsCompleted: true } done)
                return;
            var result = done.IsCompletedSuccessfully
                ? done.Result
                : new ServerCommandResult(ServerCommandStatus.NotConnected);
            if (!result.Completed)
            {
                // Rejected or not delivered: show the latest authoritative
                // state again (a no-op if a newer one already replaced it).
                host.CancelPreview(previewId);
                if (host.PreviewStatus(previewId) == FurniturePreviewStatus.Faulted)
                {
                    ResetUnsafe("The item hit a game error and is being redrawn.");
                    return;
                }
                ResetUnsafe(result.Status switch
                {
                    ServerCommandStatus.Rejected when result.RejectReason == ProtocolRejectReason.StaleRevision =>
                        "Someone else changed it first; restored.",
                    ServerCommandStatus.NotConnected =>
                        "The connection dropped before the server answered; showing the shared state.",
                    _ => $"Not moved. {result.Describe()}",
                });
                return;
            }
            // Accepted, but a reply is not authority: wait for the event.
            accepted = true;
        }

        switch (status)
        {
            case FurniturePreviewStatus.Committing:
                return;
            case FurniturePreviewStatus.Faulted:
                ResetUnsafe("The item hit a game error and is being redrawn.");
                return;
            default:
                // Superseded by the authoritative state (or the object was
                // released here): the shown state is authoritative again.
                ResetUnsafe(null);
                return;
        }
    }

    private void PumpStainUnsafe()
    {
        if (basis is not { } start)
        {
            ResetUnsafe(null);
            return;
        }
        if (!accepted)
        {
            if (commit is not { IsCompleted: true } done)
                return;
            var result = done.IsCompletedSuccessfully
                ? done.Result
                : new ServerCommandResult(ServerCommandStatus.NotConnected);
            if (!result.Completed)
            {
                // Nothing changed on the server; the shown state is still
                // the shared one.
                ResetUnsafe(result.Status switch
                {
                    ServerCommandStatus.Rejected when result.RejectReason == ProtocolRejectReason.StaleRevision =>
                        $"Someone else changed it first; {changeNoun} was not changed.",
                    ServerCommandStatus.NotConnected =>
                        "The connection dropped before the server answered; showing the shared state.",
                    _ => $"Not changed. {result.Describe()}",
                });
                return;
            }
            // Accepted, but a reply is not authority: wait for the event.
            accepted = true;
        }

        var current = authoritative(start.ObjectId);
        if (current is null || current.Revision.Value > start.Revision.Value)
            ResetUnsafe(null);
    }

    private void RequestCancelUnsafe(string? reason)
    {
        pendingBegin = null;
        pendingApply = null;
        pendingMove = null;
        pendingEnd = false;
        switch (phase)
        {
            case FurnitureGizmoPhase.Dragging:
                pendingCancel = reason ?? string.Empty;
                break;
            case FurnitureGizmoPhase.Committing:
                // Released: the update is in flight and its outcome decides.
                break;
            default:
                previewPosition = null;
                previewRotation = null;
                break;
        }
    }

    private void CancelUnsafe(string? reason)
    {
        if (phase != FurnitureGizmoPhase.Dragging)
            return;
        host.CancelPreview(previewId);
        ResetUnsafe(reason);
    }

    private void ResetUnsafe(string? reason)
    {
        phase = FurnitureGizmoPhase.Idle;
        previewId = Guid.Empty;
        basis = null;
        basisSession = 0;
        previewPosition = null;
        previewRotation = null;
        pendingMove = null;
        pendingEnd = false;
        commit = null;
        accepted = false;
        awaitsEvent = false;
        message = reason;
    }
}
