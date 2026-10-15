using System.Numerics;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Furniture;

/// <summary>
/// Client-only scene objects the host draws furniture with. Every call is made
/// on the game thread. An instance is an opaque, nonzero handle the backend
/// returned from <see cref="Create"/>; the host never invents one.
/// </summary>
public interface IFurnitureSceneBackend
{
    /// <summary>Creates one model in client memory only, or returns 0.</summary>
    nint Create(string modelPath);

    /// <summary>The model finished loading; transforms can be applied in full.</summary>
    bool IsLoaded(nint instance);

    /// <summary>
    /// Places the instance. Scale is always one: furniture has no scale.
    /// <paramref name="loaded"/> says whether the model has loaded, so the
    /// backend may finalize the transform. This never makes it visible.
    /// </summary>
    void SetTransform(nint instance, Vector3 position, Quaternion rotation, bool loaded);

    /// <summary>
    /// Makes a loaded, fully applied instance visible. The host calls it once,
    /// only after the transform and (for a dyed item) the stain are applied,
    /// so a dyed item is never seen undyed.
    /// </summary>
    void Show(nint instance);

    /// <summary>
    /// Applies a housing stain colour (the game's own sRGB object-stain
    /// colour) to a loaded instance. Returns false while the model's stain
    /// buffer does not exist yet; the host retries on later polls.
    /// </summary>
    bool TrySetStain(nint instance, FurnitureStainColor color);

    /// <summary>Frees the exact instance.</summary>
    void Destroy(nint instance);
}

/// <summary>
/// One object this client was asked to draw but does not. Reports are per
/// object: the exact report is cleared when that object draws successfully
/// or is no longer wanted (<see cref="FurnitureLocalObjectHost.RetainUnsupported"/>).
/// </summary>
public sealed record FurnitureUnsupportedReport(
    RscObjectId ObjectId,
    string Identity,
    string? Name,
    string Reason,
    int Count,
    LocalObjectReportKind Kind = LocalObjectReportKind.NotSupported);

/// <summary>Why an object is listed as not drawn.</summary>
public enum LocalObjectReportKind
{
    /// <summary>Permanently: this client cannot draw it (excluded from loading progress).</summary>
    NotSupported,
    /// <summary>Creation failed; another attempt is scheduled (still pending in loading progress).</summary>
    Retrying,
    /// <summary>Creation failed and the retries are exhausted (excluded from loading progress).</summary>
    Failed,
}

/// <summary>
/// Furniture host for the local-object port: one client-only model per
/// supported item, owned through an exact receipt. Creating the object is not
/// success on its own: a spawn returns <see cref="ApplyPortStatus.AwaitingPayload"/>
/// until the model has loaded and its transform is applied, and the
/// reconciler then polls <see cref="ApplyOwned"/> within a bounded time.
/// Releasing frees exactly the receipt's instance and is idempotent. A
/// backend fault while applying to an owned instance propagates, so the
/// reconciler releases that exact receipt before any retry.
/// Unsupported items (multipart, unknown, ...) are never spawned; they are
/// reported in <see cref="Unsupported"/>. Lights are not drawn here.
/// <para>
/// Stains: stain 0 (Default) is the plain undyed model, with no stain
/// lookup or call. Any other stain must be in this client's housing palette
/// and the item must be dyeable; otherwise the object is not drawn at all
/// (never drawn undyed, which would differ from the shared appearance) and is
/// reported. A dyed object counts as applied only once its stain is set,
/// within the same bounded load time. The stain is part of the spawn
/// identity, so a stain change replaces the exact owned instance.
/// </para>
/// </summary>
public sealed class FurnitureLocalObjectHost :
    ILocalObjectRuntimePort,
    ILocalObjectRuntimeOwnershipDiagnostics,
    ILocalObjectPreviewHost
{
    public const int MaximumUnsupportedReports = 64;

    private readonly FurnitureSupportResolver resolver;
    private readonly IFurnitureSceneBackend backend;
    private readonly FurnitureStainPalette stains;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Owned> owned = new();
    private readonly List<FurnitureUnsupportedReport> unsupported = new();
    private bool closed;
    // At most one local preview (gizmo drag) at a time; see the Preview region.
    private Preview? preview;
    private (Guid Id, FurniturePreviewStatus Status) lastPreview;

    /// <summary>
    /// Raised (game thread, outside the host's lock) after a native fault
    /// retired an instance this host owned. The receiver still holds that
    /// receipt, so the plugin resets the local-object domain: the receipt is
    /// released (as already gone) and the object respawns as a fresh instance.
    /// </summary>
    public event Action? InstanceRetired;

    public FurnitureLocalObjectHost(
        FurnitureSupportResolver resolver,
        IFurnitureSceneBackend backend,
        FurnitureStainPalette? stains = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(backend);
        this.resolver = resolver;
        this.backend = backend;
        this.stains = stains ?? FurnitureStainPalette.Empty;
    }

    public int OwnedCount
    {
        get
        {
            lock (gate)
                return owned.Count;
        }
    }

    int ILocalObjectRuntimeOwnershipDiagnostics.PendingOwnershipCount => OwnedCount;

    /// <summary>Items this client was asked to draw but cannot, most recent last.</summary>
    public IReadOnlyList<FurnitureUnsupportedReport> Unsupported
    {
        get
        {
            lock (gate)
                return unsupported.ToArray();
        }
    }

    public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(LocalObjectSpawnRequest request)
    {
        if (IsClosed)
            return new(ApplyPortStatus.Unavailable, Error: "The furniture host is shut down.");
        if (!request.IsValid)
            return new(ApplyPortStatus.PermanentFailure, Error: "Invalid spawn request.");
        if (request.DesiredState is not FurnitureState furniture)
        {
            var identity = request.Identity.Value;
            Report(request.Token.ObjectId, identity, null, "Lights are not drawn by the furniture host.");
            return new(ApplyPortStatus.PermanentFailure, Error: "Only furniture is drawn in this version.");
        }

        var support = resolver.Resolve(furniture.FurnitureIdentity);
        if (!support.IsSupported)
        {
            Report(furniture.ObjectId, furniture.FurnitureIdentity, support.Row?.Name, support.Describe());
            return new(
                ApplyPortStatus.PermanentFailure,
                Error: $"{furniture.FurnitureIdentity}: {support.DescribeForLog()}.");
        }

        FurnitureStainColor? stainColor = null;
        if (furniture.Stain == 0)
        {
            // Default: the item as the game shows it undyed, i.e. with its own
            // default stain from its layout, if it has one (Brio does the
            // same for a shared group). Without one, the plain model.
            if (stains.TryGetDefaultColor(support.DefaultStain, out var defaultColor))
                stainColor = defaultColor;
        }
        else
        {
            // Fail closed before anything is created: draw nothing rather
            // than an undyed object that differs from the shared one.
            string? refusal = null;
            if (support.Row is not { IsDyeable: true })
                refusal = $"Dyed (stain {furniture.Stain}), but this item cannot be dyed; not drawn.";
            else if (!stains.TryGet(furniture.Stain, out var entry))
                refusal = $"Dye {furniture.Stain} is not a housing dye this client knows; not drawn.";
            else
                stainColor = entry.Color;
            if (refusal is not null)
            {
                Report(furniture.ObjectId, furniture.FurnitureIdentity, support.Row?.Name, refusal);
                return new(ApplyPortStatus.PermanentFailure, Error: $"{furniture.FurnitureIdentity}: {refusal}");
            }
        }

        var instance = backend.Create(support.ModelPath!);
        if (instance == 0)
        {
            // Nothing was created, so nothing is owned.
            return new(ApplyPortStatus.TransientFailure, Error: "The model could not be created.");
        }

        var handle = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            request.Token.ObjectId,
            RscObjectKind.Furniture,
            request.SpawnGeneration);
        var created = new Owned(handle, instance, stainColor);
        lock (gate)
        {
            if (closed)
            {
                DestroyExact(created);
                return new(ApplyPortStatus.Unavailable, Error: "The furniture host is shut down.");
            }
            owned[handle.OwnershipId] = created;
        }

        // From here the receipt is returned on every path, so the reconciler
        // owns the instance and can release it.
        try
        {
            return Place(created, furniture)
                ? new(ApplyPortStatus.Succeeded, handle)
                : new(ApplyPortStatus.AwaitingPayload, handle);
        }
        catch (Exception exception)
        {
            return new(ApplyPortStatus.TransientFailure, handle, $"The model could not be placed: {exception.Message}");
        }
    }

    public ApplyPortResult ApplyOwned(LocalObjectInstanceHandle handle, AuthoritativeObjectState desiredState)
    {
        if (desiredState is not FurnitureState furniture || furniture.ObjectId != handle.ObjectId)
            return new(ApplyPortStatus.PermanentFailure, "The desired state does not match this receipt.");

        Owned? entry;
        lock (gate)
            owned.TryGetValue(handle.OwnershipId, out entry);
        if (entry is null || entry.Handle != handle)
        {
            // Released, or wiped by a location change: there is nothing to
            // apply to. The reconciler releases the receipt and respawns.
            return new(ApplyPortStatus.TransientFailure, "The instance no longer exists.");
        }

        // The authoritative state wins over a local preview of this object:
        // a remote update (or our own accepted one) ends the preview.
        EndPreviewFor(entry, FurniturePreviewStatus.Superseded);

        // A backend (native) fault leaves the instance in an unknown state.
        // It is not caught here: the reconciler then releases this exact
        // receipt before any retry respawns, rather than keeping it owned.
        return Place(entry, furniture)
            ? new(ApplyPortStatus.Succeeded)
            : new(ApplyPortStatus.AwaitingPayload, "The model is still loading.");
    }

    public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
    {
        Owned? entry;
        lock (gate)
        {
            if (!owned.TryGetValue(handle.OwnershipId, out entry) || entry.Handle != handle)
                return new(ApplyPortStatus.Succeeded);
            owned.Remove(handle.OwnershipId);
            EndPreviewForUnsafe(entry, FurniturePreviewStatus.Released);
        }

        return DestroyExact(entry)
            ? new(ApplyPortStatus.Succeeded)
            : new(ApplyPortStatus.Succeeded, "The model was released, but freeing it reported an error.");
    }

    /// <summary>
    /// Frees every instance this host owns. Called on the game thread when the
    /// player's location changes (objects of the old place must not be drawn
    /// in the new one) and on unload. Receipts the receiver still holds then
    /// release as already gone.
    /// </summary>
    public int DestroyAll()
    {
        Owned[] all;
        lock (gate)
        {
            all = owned.Values.ToArray();
            owned.Clear();
            if (preview is { } active)
                FinishPreviewUnsafe(active, FurniturePreviewStatus.Released);
        }
        foreach (var entry in all)
            DestroyExact(entry);
        return all.Length;
    }

    /// <summary>
    /// Final cleanup on unload, on the game thread: frees every instance and
    /// refuses any spawn that is still queued, so nothing outlives the plugin.
    /// </summary>
    public int Shutdown()
    {
        lock (gate)
            closed = true;
        return DestroyAll();
    }

    /// <summary>After <see cref="Shutdown"/>: the host touches no game object any more.</summary>
    public bool IsShutDown => IsClosed;

    private bool IsClosed
    {
        get
        {
            lock (gate)
                return closed;
        }
    }

    public void ClearUnsupported()
    {
        lock (gate)
            unsupported.Clear();
    }

    /// <summary>
    /// Drops the reports of objects that are no longer wanted (deleted, or
    /// gone from the desired state). <paramref name="isWanted"/> is asked per
    /// reported object ID.
    /// </summary>
    public int RetainUnsupported(Func<RscObjectId, bool> isWanted)
    {
        ArgumentNullException.ThrowIfNull(isWanted);
        lock (gate)
            return unsupported.RemoveAll(report => !isWanted(report.ObjectId));
    }

    private bool Place(Owned entry, FurnitureState furniture)
    {
        var loaded = backend.IsLoaded(entry.Instance);
        backend.SetTransform(entry.Instance, ToVector(furniture.Position), ToQuaternion(furniture.Rotation), loaded);
        if (!loaded)
            return false;
        // Only once the model has loaded; the stain buffer may still be
        // missing, in which case this is retried on the next poll. Until the
        // stain is set the instance stays hidden: a dyed item is never seen
        // undyed.
        if (entry.StainColor is { } color && !entry.StainApplied)
            entry.StainApplied = backend.TrySetStain(entry.Instance, color);
        if (entry.StainColor is not null && !entry.StainApplied)
            return false;
        if (!entry.Shown)
        {
            backend.Show(entry.Instance);
            entry.Shown = true;
        }
        lock (gate)
        {
            entry.Applied = furniture;
            // Drawn now: an earlier report for this exact object is resolved.
            unsupported.RemoveAll(report => report.ObjectId == furniture.ObjectId);
        }
        return true;
    }

    private bool DestroyExact(Owned entry)
    {
        try
        {
            backend.Destroy(entry.Instance);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Report(RscObjectId objectId, string identity, string? name, string reason)
    {
        lock (gate)
        {
            var index = unsupported.FindIndex(report => report.ObjectId == objectId);
            var count = 1;
            if (index >= 0)
            {
                count = unsupported[index].Count + 1;
                unsupported.RemoveAt(index);
            }
            else if (unsupported.Count == MaximumUnsupportedReports)
            {
                unsupported.RemoveAt(0);
            }
            unsupported.Add(new FurnitureUnsupportedReport(objectId, identity, name, reason, count));
        }
    }

    internal static Vector3 ToVector(Vector3State value) => new(value.X, value.Y, value.Z);

    internal static Quaternion ToQuaternion(RotationState value)
    {
        var rotation = new Quaternion(value.X, value.Y, value.Z, value.W);
        var length = rotation.Length();
        return length > 1e-6f && float.IsFinite(length)
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;
    }

    private sealed class Owned(LocalObjectInstanceHandle handle, nint instance, FurnitureStainColor? stainColor = null)
    {
        internal LocalObjectInstanceHandle Handle { get; } = handle;
        internal nint Instance { get; } = instance;
        /// <summary>The stain colour this instance was spawned for (null = Default, undyed).</summary>
        internal FurnitureStainColor? StainColor { get; } = stainColor;
        internal bool StainApplied { get; set; }
        /// <summary>Made visible (only after it was fully applied).</summary>
        internal bool Shown { get; set; }
        /// <summary>The last authoritative state fully applied (loaded and placed).</summary>
        internal FurnitureState? Applied { get; set; }
    }

    #region Preview

    // A preview is a local, temporary transform of exactly one owned and
    // drawn instance while the player drags the gizmo. It never touches the
    // desired-state store and sends nothing; the controller sends one update
    // on release. It is bound to the exact receipt it started on, so it can
    // never act on a replacement instance with the same object ID.

    /// <summary>Whether the object is drawn here and fully applied (selectable for the gizmo).</summary>
    public bool IsDrawn(RscObjectId objectId)
    {
        lock (gate)
            return !closed && FindDrawnUnsafe(objectId) is not null;
    }

    FurniturePreviewBegin ILocalObjectPreviewHost.BeginPreview(AuthoritativeObjectState authoritative) =>
        authoritative is FurnitureState furniture
            ? BeginPreview(furniture)
            : new(FurniturePreviewBeginStatus.NotDrawn, Guid.Empty);

    /// <summary>Game thread. Starts a preview of the drawn object at exactly <paramref name="authoritative"/>.</summary>
    public FurniturePreviewBegin BeginPreview(FurnitureState authoritative)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        lock (gate)
        {
            if (closed)
                return new(FurniturePreviewBeginStatus.Unavailable, Guid.Empty);
            if (preview is not null)
                return new(FurniturePreviewBeginStatus.Busy, Guid.Empty);
            var entry = FindDrawnUnsafe(authoritative.ObjectId);
            if (entry is null)
                return new(FurniturePreviewBeginStatus.NotDrawn, Guid.Empty);
            // Only the exact state this client shows can be the basis.
            if (entry.Applied != authoritative)
                return new(FurniturePreviewBeginStatus.Stale, Guid.Empty);
            var id = Guid.NewGuid();
            preview = new Preview(id, entry.Handle, authoritative)
            {
                Position = ToVector(authoritative.Position),
                Rotation = ToQuaternion(authoritative.Rotation),
            };
            lastPreview = (id, FurniturePreviewStatus.Active);
            return new(FurniturePreviewBeginStatus.Started, id);
        }
    }

    public FurniturePreviewStatus PreviewStatus(Guid id)
    {
        lock (gate)
            return lastPreview.Id == id && id != Guid.Empty ? lastPreview.Status : FurniturePreviewStatus.Unknown;
    }

    /// <summary>
    /// Game thread. Moves the previewed instance (only it). Invalid values
    /// (NaN, infinity, a zero rotation) are refused without touching it.
    /// </summary>
    public bool MovePreview(Guid id, Vector3 position, Quaternion rotation)
    {
        if (!FurnitureTransforms.IsValidPosition(position) ||
            !FurnitureTransforms.TryNormalize(rotation, out var normalized))
            return false;
        bool retired;
        lock (gate)
        {
            if (preview is not { Phase: FurniturePreviewStatus.Active } active || active.Id != id ||
                !owned.TryGetValue(active.Receipt.OwnershipId, out var entry) || entry.Handle != active.Receipt)
                return false;
            if (TryWriteUnsafe(entry, position, normalized))
            {
                active.Position = position;
                active.Rotation = normalized;
                return true;
            }
            retired = true;
        }
        if (retired)
            InstanceRetired?.Invoke();
        return false;
    }

    /// <summary>
    /// Game thread. Ends the drag. A drag that changed nothing restores the
    /// basis and finishes; otherwise the preview waits (Committing) for the
    /// server's answer to the one update the controller sends.
    /// </summary>
    public FurniturePreviewEnd EndPreview(Guid id)
    {
        var retiredOnEnd = false;
        try
        {
            return EndPreviewCore(id, ref retiredOnEnd);
        }
        finally
        {
            if (retiredOnEnd)
                InstanceRetired?.Invoke();
        }
    }

    private FurniturePreviewEnd EndPreviewCore(Guid id, ref bool retiredOnEnd)
    {
        lock (gate)
        {
            if (preview is not { Phase: FurniturePreviewStatus.Active } active || active.Id != id)
                return new(false, null, default, default);
            var basisPosition = ToVector(active.Basis.Position);
            var basisRotation = ToQuaternion(active.Basis.Rotation);
            if (FurnitureTransforms.SameTransform(basisPosition, basisRotation, active.Position, active.Rotation))
            {
                if (RestoreUnsafe(active))
                    FinishPreviewUnsafe(active, FurniturePreviewStatus.Finished);
                else
                    retiredOnEnd = true;
                return new(false, active.Basis, basisPosition, basisRotation);
            }
            active.Phase = FurniturePreviewStatus.Committing;
            lastPreview = (id, FurniturePreviewStatus.Committing);
            return new(true, active.Basis, active.Position, active.Rotation);
        }
    }

    /// <summary>Game thread. The update was accepted: keep what is shown; the server event confirms it.</summary>
    public void CompletePreview(Guid id)
    {
        lock (gate)
        {
            if (preview is { } active && active.Id == id)
                FinishPreviewUnsafe(active, FurniturePreviewStatus.Finished);
        }
    }

    /// <summary>
    /// Game thread. Discards the preview and restores the latest authoritative
    /// state of the exact receipt it started on; nothing happens to a
    /// released instance or to a replacement with a newer receipt.
    /// </summary>
    public void CancelPreview(Guid id)
    {
        bool retired;
        lock (gate)
        {
            if (preview is not { } active || active.Id != id)
                return;
            retired = !RestoreUnsafe(active);
            if (!retired)
                FinishPreviewUnsafe(active, FurniturePreviewStatus.Cancelled);
        }
        if (retired)
            InstanceRetired?.Invoke();
    }

    /// <summary>Restores the latest applied state; false when a native fault retired the instance.</summary>
    private bool RestoreUnsafe(Preview active)
    {
        if (!owned.TryGetValue(active.Receipt.OwnershipId, out var entry) || entry.Handle != active.Receipt)
            return true;
        var latest = entry.Applied ?? active.Basis;
        return TryWriteUnsafe(entry, ToVector(latest.Position), ToQuaternion(latest.Rotation));
    }

    /// <summary>
    /// One native transform write. A fault leaves the instance in an unknown
    /// state, so it is retired at once: fenced out of this host (no further
    /// write can reach it), its preview ended, and freed best-effort. The
    /// caller then raises <see cref="InstanceRetired"/> outside the lock.
    /// </summary>
    private bool TryWriteUnsafe(Owned entry, Vector3 position, Quaternion rotation)
    {
        try
        {
            backend.SetTransform(entry.Instance, position, rotation, loaded: true);
            return true;
        }
        catch (Exception)
        {
            owned.Remove(entry.Handle.OwnershipId);
            if (preview is { } active && active.Receipt == entry.Handle)
                FinishPreviewUnsafe(active, FurniturePreviewStatus.Faulted);
            DestroyExact(entry);
            return false;
        }
    }

    private void EndPreviewFor(Owned entry, FurniturePreviewStatus status)
    {
        lock (gate)
            EndPreviewForUnsafe(entry, status);
    }

    private void EndPreviewForUnsafe(Owned entry, FurniturePreviewStatus status)
    {
        if (preview is { } active && active.Receipt == entry.Handle)
            FinishPreviewUnsafe(active, status);
    }

    private void FinishPreviewUnsafe(Preview active, FurniturePreviewStatus status)
    {
        if (!ReferenceEquals(preview, active))
            return;
        preview = null;
        lastPreview = (active.Id, status);
    }

    private Owned? FindDrawnUnsafe(RscObjectId objectId)
    {
        foreach (var entry in owned.Values)
        {
            if (entry.Handle.ObjectId == objectId && entry.Applied is not null)
                return entry;
        }
        return null;
    }

    private sealed class Preview(Guid id, LocalObjectInstanceHandle receipt, FurnitureState basis)
    {
        internal Guid Id { get; } = id;
        internal LocalObjectInstanceHandle Receipt { get; } = receipt;
        internal FurnitureState Basis { get; } = basis;
        internal Vector3 Position { get; set; }
        internal Quaternion Rotation { get; set; }
        internal FurniturePreviewStatus Phase { get; set; } = FurniturePreviewStatus.Active;
    }

    #endregion
}

public enum FurniturePreviewBeginStatus
{
    Started,
    /// <summary>Not owned here, or not fully loaded and applied yet.</summary>
    NotDrawn,
    /// <summary>The state given is not the one this client shows (a newer one is being applied).</summary>
    Stale,
    Busy,
    Unavailable,
}

public readonly record struct FurniturePreviewBegin(FurniturePreviewBeginStatus Status, Guid Id)
{
    public bool Started => Status == FurniturePreviewBeginStatus.Started;
}

public enum FurniturePreviewStatus
{
    Unknown,
    Active,
    Committing,
    /// <summary>Ended normally (accepted, or a drag that changed nothing).</summary>
    Finished,
    Cancelled,
    /// <summary>A newer authoritative state was applied to the object.</summary>
    Superseded,
    /// <summary>A native fault retired the instance; it respawns fresh.</summary>
    Faulted,
    /// <summary>The object was released (deleted, location change, host loss).</summary>
    Released,
}

/// <summary>The drag result: whether anything changed, and the final transform.</summary>
public readonly record struct FurniturePreviewEnd(
    bool Changed,
    AuthoritativeObjectState? Basis,
    Vector3 Position,
    Quaternion Rotation);

/// <summary>
/// A local, temporary move of one drawn object while the gizmo is dragged
/// (furniture and lights). Game thread only. See <see cref="FurnitureLocalObjectHost"/>.
/// </summary>
public interface ILocalObjectPreviewHost
{
    bool IsDrawn(RscObjectId objectId);
    FurniturePreviewBegin BeginPreview(AuthoritativeObjectState authoritative);
    FurniturePreviewStatus PreviewStatus(Guid id);
    bool MovePreview(Guid id, Vector3 position, Quaternion rotation);
    FurniturePreviewEnd EndPreview(Guid id);
    void CompletePreview(Guid id);
    void CancelPreview(Guid id);
}
