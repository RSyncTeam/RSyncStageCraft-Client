using System.Numerics;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Lights;

/// <summary>
/// Light host for the local-object port: one client-only point light per
/// <c>light:point</c> object, owned through an exact receipt, with its own
/// ownership records and native backend (separate from furniture).
/// <list type="bullet">
/// <item>A light is created hidden, fully applied, then shown; it counts as
/// drawn only then. Native writes happen only when its settings changed
/// (apply-on-change); there are no per-frame updates.</item>
/// <item>A failed creation owns nothing and is a transient failure the
/// receiver retries (an initial attempt plus up to five retries per apply
/// generation). It is reported as <see cref="LocalObjectReportKind.Retrying"/>
/// while another attempt follows and as <see cref="LocalObjectReportKind.Failed"/>
/// once the last one failed; never as unsupported.</item>
/// <item>A non-canonical identity or invalid state is permanently not drawn
/// (<see cref="LocalObjectReportKind.NotSupported"/>).</item>
/// <item>A native fault while applying propagates (the reconciler releases
/// the exact receipt); a fault during a gizmo preview retires the instance
/// at once and raises <see cref="InstanceRetired"/>.</item>
/// </list>
/// </summary>
public sealed class LightLocalObjectHost :
    ILocalObjectRuntimePort,
    ILocalObjectRuntimeOwnershipDiagnostics,
    ILocalObjectPreviewHost
{
    public const int MaximumReports = 64;

    /// <summary>The receiver's local-object retry policy: an initial attempt plus five retries.</summary>
    public static readonly int DefaultCreationAttempts = LocalObjectApplyRetryPolicy.Default.MaximumAttempts + 1;

    private readonly ILightSceneBackend backend;
    private readonly int creationAttempts;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Owned> owned = new();
    private readonly List<FurnitureUnsupportedReport> reports = new();
    // Per object: the apply token (session, object, generation) of the current
    // budget and its failed creation calls. A new token is a fresh budget.
    private readonly Dictionary<RscObjectId, (LocalObjectApplyToken Token, int Failures)> creationFailures = new();
    private bool closed;
    private Preview? preview;
    private (Guid Id, FurniturePreviewStatus Status) lastPreview;

    /// <summary>Raised (game thread, outside the lock) after a native fault retired an owned light.</summary>
    public event Action? InstanceRetired;

    /// <param name="creationAttempts">
    /// Creation calls per apply generation before a light counts as failed:
    /// the receiver's initial attempt plus its retries.
    /// </param>
    public LightLocalObjectHost(ILightSceneBackend backend, int? creationAttempts = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        this.backend = backend;
        this.creationAttempts = creationAttempts ?? DefaultCreationAttempts;
        if (this.creationAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(creationAttempts));
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

    public IReadOnlyList<FurnitureUnsupportedReport> Unsupported
    {
        get
        {
            lock (gate)
                return reports.ToArray();
        }
    }

    public bool IsShutDown
    {
        get
        {
            lock (gate)
                return closed;
        }
    }

    public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(LocalObjectSpawnRequest request)
    {
        if (IsShutDown)
            return new(ApplyPortStatus.Unavailable, Error: "The light host is shut down.");
        // Checked again on the client (the server already validates): another
        // identity or a state outside the protocol 5 bounds is never drawn.
        if (request.DesiredState is LightState candidate && candidate.ObjectId.IsValid &&
            (!LightIdentity.IsCanonical(candidate.LightIdentity) || !candidate.IsValid))
            return RefuseUnsupported(candidate);
        if (!request.IsValid)
            return new(ApplyPortStatus.PermanentFailure, Error: "Invalid spawn request.");
        if (request.DesiredState is not LightState light)
            return new(ApplyPortStatus.PermanentFailure, Error: "Only lights are drawn by the light host.");

        var created = backend.Create();
        if (!created.Created)
        {
            // Nothing is owned (a partial allocation was already freed).
            RecordCreationFailure(light, request.Token);
            return new(ApplyPortStatus.TransientFailure, Error: created.Status == LightCreateStatus.IncompleteFreed
                ? "The light was created without its render part and was freed."
                : "The light could not be created.");
        }

        var handle = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            request.Token.ObjectId,
            RscObjectKind.Light,
            request.SpawnGeneration);
        var entry = new Owned(handle, created.Instance);
        lock (gate)
        {
            if (closed)
            {
                DestroyExact(entry);
                return new(ApplyPortStatus.Unavailable, Error: "The light host is shut down.");
            }
            owned[handle.OwnershipId] = entry;
        }

        // From here the receipt is returned on every path.
        try
        {
            Place(entry, light);
            return new(ApplyPortStatus.Succeeded, handle);
        }
        catch (Exception exception)
        {
            return new(ApplyPortStatus.TransientFailure, handle, $"The light could not be applied: {exception.Message}");
        }
    }

    public ApplyPortResult ApplyOwned(LocalObjectInstanceHandle handle, AuthoritativeObjectState desiredState)
    {
        if (desiredState is not LightState light || light.ObjectId != handle.ObjectId)
            return new(ApplyPortStatus.PermanentFailure, "The desired state does not match this receipt.");
        if (!light.IsValid)
        {
            Report(light.ObjectId, light.LightIdentity, LocalObjectReportKind.NotSupported, UnsupportedReason);
            return new(ApplyPortStatus.PermanentFailure, "Unsupported light.");
        }
        Owned? entry;
        lock (gate)
            owned.TryGetValue(handle.OwnershipId, out entry);
        if (entry is null || entry.Handle != handle)
            return new(ApplyPortStatus.TransientFailure, "The instance no longer exists.");

        // The authoritative state wins over a local preview of this light.
        lock (gate)
            EndPreviewForUnsafe(entry, FurniturePreviewStatus.Superseded);
        // A native fault propagates: the reconciler releases this exact receipt.
        Place(entry, light);
        return new(ApplyPortStatus.Succeeded);
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
            : new(ApplyPortStatus.Succeeded, "The light was released, but freeing it reported an error.");
    }

    /// <summary>Frees every light this host owns (location change, unload).</summary>
    public int DestroyAll()
    {
        Owned[] all;
        lock (gate)
        {
            all = owned.Values.ToArray();
            owned.Clear();
            creationFailures.Clear();
            if (preview is { } active)
                FinishPreviewUnsafe(active, FurniturePreviewStatus.Released);
        }
        foreach (var entry in all)
            DestroyExact(entry);
        return all.Length;
    }

    public int Shutdown()
    {
        lock (gate)
            closed = true;
        return DestroyAll();
    }

    public void ClearUnsupported()
    {
        lock (gate)
        {
            reports.Clear();
            creationFailures.Clear();
        }
    }

    public int RetainUnsupported(Func<RscObjectId, bool> isWanted)
    {
        ArgumentNullException.ThrowIfNull(isWanted);
        lock (gate)
        {
            foreach (var id in creationFailures.Keys.Where(id => !isWanted(id)).ToArray())
                creationFailures.Remove(id);
            return reports.RemoveAll(report => !isWanted(report.ObjectId));
        }
    }

    private void Place(Owned entry, LightState light)
    {
        var settings = LightColorConversion.ToNative(light);
        // Apply-on-change: native writes only when something changed.
        if (entry.Written != settings)
        {
            backend.Apply(entry.Instance, settings);
            entry.Written = settings;
        }
        if (!entry.Shown)
        {
            backend.Show(entry.Instance);
            entry.Shown = true;
        }
        lock (gate)
        {
            entry.Applied = light;
            creationFailures.Remove(light.ObjectId);
            reports.RemoveAll(report => report.ObjectId == light.ObjectId);
        }
    }

    private const string UnsupportedReason = "Not a point light this client can draw.";

    private ApplyPortResult<LocalObjectInstanceHandle> RefuseUnsupported(LightState light)
    {
        Report(light.ObjectId, light.LightIdentity, LocalObjectReportKind.NotSupported, UnsupportedReason);
        return new(ApplyPortStatus.PermanentFailure, Error: "Unsupported light.");
    }

    private void RecordCreationFailure(LightState light, LocalObjectApplyToken token)
    {
        int failures;
        lock (gate)
        {
            failures = creationFailures.TryGetValue(light.ObjectId, out var previous) && previous.Token == token
                ? previous.Failures + 1
                : 1;
            creationFailures[light.ObjectId] = (token, failures);
        }
        if (failures >= creationAttempts)
            Report(light.ObjectId, light.LightIdentity, LocalObjectReportKind.Failed,
                "Could not create the light; retries exhausted.");
        else
            Report(light.ObjectId, light.LightIdentity, LocalObjectReportKind.Retrying,
                "Could not create the light (retrying).");
    }

    /// <summary>
    /// Frees one created light: hide, render cleanup, then free (the
    /// reference lifecycle for every created light, even one whose first apply
    /// faulted before it was shown). The ownership record is already gone, so
    /// each step is attempted on its own: a fault in one never skips the
    /// later ones, and above all <c>Free</c> is always attempted. Returns
    /// whether every step succeeded. Never throws.
    /// </summary>
    private bool DestroyExact(Owned entry)
    {
        var hidden = TryStep(backend.Hide, entry.Instance);
        var cleaned = TryStep(backend.CleanupRender, entry.Instance);
        var freed = TryStep(backend.Free, entry.Instance);
        return hidden && cleaned && freed;
    }

    private static bool TryStep(Action<nint> step, nint instance)
    {
        try
        {
            step(instance);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Report(RscObjectId objectId, string identity, LocalObjectReportKind kind, string reason)
    {
        lock (gate)
        {
            var index = reports.FindIndex(report => report.ObjectId == objectId);
            var count = 1;
            if (index >= 0)
            {
                count = reports[index].Count + 1;
                reports.RemoveAt(index);
            }
            else if (reports.Count == MaximumReports)
            {
                reports.RemoveAt(0);
            }
            reports.Add(new FurnitureUnsupportedReport(objectId, identity, "Point light", reason, count, kind));
        }
    }

    private sealed class Owned(LocalObjectInstanceHandle handle, nint instance)
    {
        internal LocalObjectInstanceHandle Handle { get; } = handle;
        internal nint Instance { get; } = instance;
        internal bool Shown { get; set; }
        /// <summary>The native settings last written (preview or authoritative).</summary>
        internal NativeLightSettings? Written { get; set; }
        /// <summary>The last authoritative state fully applied.</summary>
        internal LightState? Applied { get; set; }
    }

    #region Preview (gizmo Move)

    public bool IsDrawn(RscObjectId objectId)
    {
        lock (gate)
            return !closed && FindDrawnUnsafe(objectId) is not null;
    }

    public FurniturePreviewBegin BeginPreview(AuthoritativeObjectState authoritative)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        if (authoritative is not LightState light)
            return new(FurniturePreviewBeginStatus.NotDrawn, Guid.Empty);
        lock (gate)
        {
            if (closed)
                return new(FurniturePreviewBeginStatus.Unavailable, Guid.Empty);
            if (preview is not null)
                return new(FurniturePreviewBeginStatus.Busy, Guid.Empty);
            var entry = FindDrawnUnsafe(light.ObjectId);
            if (entry is null)
                return new(FurniturePreviewBeginStatus.NotDrawn, Guid.Empty);
            if (entry.Applied != light)
                return new(FurniturePreviewBeginStatus.Stale, Guid.Empty);
            var id = Guid.NewGuid();
            preview = new Preview(id, entry.Handle, light) { Position = PositionOf(light) };
            lastPreview = (id, FurniturePreviewStatus.Active);
            return new(FurniturePreviewBeginStatus.Started, id);
        }
    }

    public FurniturePreviewStatus PreviewStatus(Guid id)
    {
        lock (gate)
            return lastPreview.Id == id && id != Guid.Empty ? lastPreview.Status : FurniturePreviewStatus.Unknown;
    }

    /// <summary>Moves the previewed light (only it); a point light has no rotation, so that is ignored.</summary>
    public bool MovePreview(Guid id, Vector3 position, Quaternion rotation)
    {
        if (!FurnitureTransforms.IsValidPosition(position))
            return false;
        bool retired;
        lock (gate)
        {
            if (preview is not { Phase: FurniturePreviewStatus.Active } active || active.Id != id ||
                !owned.TryGetValue(active.Receipt.OwnershipId, out var entry) || entry.Handle != active.Receipt)
                return false;
            if (TryWriteUnsafe(entry, LightColorConversion.ToNative(active.Basis, position)))
            {
                active.Position = position;
                return true;
            }
            retired = true;
        }
        if (retired)
            InstanceRetired?.Invoke();
        return false;
    }

    public FurniturePreviewEnd EndPreview(Guid id)
    {
        var retired = false;
        try
        {
            lock (gate)
            {
                if (preview is not { Phase: FurniturePreviewStatus.Active } active || active.Id != id)
                    return new(false, null, default, default);
                var basisPosition = PositionOf(active.Basis);
                if (Vector3.Distance(basisPosition, active.Position) < FurnitureTransforms.PositionEpsilon)
                {
                    if (RestoreUnsafe(active))
                        FinishPreviewUnsafe(active, FurniturePreviewStatus.Finished);
                    else
                        retired = true;
                    return new(false, active.Basis, basisPosition, Quaternion.Identity);
                }
                active.Phase = FurniturePreviewStatus.Committing;
                lastPreview = (id, FurniturePreviewStatus.Committing);
                return new(true, active.Basis, active.Position, Quaternion.Identity);
            }
        }
        finally
        {
            if (retired)
                InstanceRetired?.Invoke();
        }
    }

    public void CompletePreview(Guid id)
    {
        lock (gate)
        {
            if (preview is { } active && active.Id == id)
                FinishPreviewUnsafe(active, FurniturePreviewStatus.Finished);
        }
    }

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

    private bool RestoreUnsafe(Preview active)
    {
        if (!owned.TryGetValue(active.Receipt.OwnershipId, out var entry) || entry.Handle != active.Receipt)
            return true;
        var latest = entry.Applied ?? active.Basis;
        return TryWriteUnsafe(entry, LightColorConversion.ToNative(latest));
    }

    /// <summary>One native write during a preview; a fault retires the exact instance at once.</summary>
    private bool TryWriteUnsafe(Owned entry, NativeLightSettings settings)
    {
        try
        {
            if (entry.Written != settings)
            {
                backend.Apply(entry.Instance, settings);
                entry.Written = settings;
            }
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

    internal static Vector3 PositionOf(LightState light) => new(light.Position.X, light.Position.Y, light.Position.Z);

    private sealed class Preview(Guid id, LocalObjectInstanceHandle receipt, LightState basis)
    {
        internal Guid Id { get; } = id;
        internal LocalObjectInstanceHandle Receipt { get; } = receipt;
        internal LightState Basis { get; } = basis;
        internal Vector3 Position { get; set; }
        internal FurniturePreviewStatus Phase { get; set; } = FurniturePreviewStatus.Active;
    }

    #endregion
}
