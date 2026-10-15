using System.Numerics;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Lights;

/// <summary>
/// The receiver's one local-object port. It only
/// dispatches by object kind to the furniture host, the light host and the
/// actor host, each with its own native backend and ownership records;
/// nothing kind-specific reaches another host. Shared behaviour (zone-change
/// wipe, unload, retirement, "not drawn" reports, gizmo previews) covers all.
/// Without an actor host, actors are refused (not drawn).
/// </summary>
public sealed class LocalObjectHost :
    ILocalObjectRuntimePort,
    ILocalObjectRuntimeOwnershipDiagnostics,
    ILocalObjectPreviewHost
{
    private readonly object gate = new();
    private ILocalObjectPreviewHost? previewOwner;
    private Guid previewId;

    public LocalObjectHost(
        FurnitureLocalObjectHost furniture,
        LightLocalObjectHost lights,
        ActorLocalObjectHost? actors = null)
    {
        ArgumentNullException.ThrowIfNull(furniture);
        ArgumentNullException.ThrowIfNull(lights);
        Furniture = furniture;
        Lights = lights;
        Actors = actors;
        furniture.InstanceRetired += RaiseRetired;
        lights.InstanceRetired += RaiseRetired;
        if (actors is not null)
            actors.InstanceRetired += RaiseRetired;
    }

    public FurnitureLocalObjectHost Furniture { get; }

    public LightLocalObjectHost Lights { get; }

    /// <summary>The actor host, or null when this client draws no actors.</summary>
    public ActorLocalObjectHost? Actors { get; }

    /// <summary>A native fault retired an instance of either host (game thread).</summary>
    public event Action? InstanceRetired;

    public int OwnedCount => Furniture.OwnedCount + Lights.OwnedCount + (Actors?.OwnedCount ?? 0);

    int ILocalObjectRuntimeOwnershipDiagnostics.PendingOwnershipCount => OwnedCount;

    /// <summary>Objects listed as not drawn on this client (furniture, then lights, then actors).</summary>
    public IReadOnlyList<FurnitureUnsupportedReport> Unsupported =>
        Furniture.Unsupported.Concat(Lights.Unsupported).Concat(Actors?.Unsupported ?? []).ToArray();

    public bool IsShutDown => Furniture.IsShutDown && Lights.IsShutDown && (Actors?.IsShutDown ?? true);

    public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(LocalObjectSpawnRequest request) =>
        request.DesiredState switch
        {
            LightState => Lights.SpawnOwned(request),
            ActorState => Actors is { } actors
                ? actors.SpawnOwned(request)
                : new(ApplyPortStatus.PermanentFailure, Error: "This client has no actor host."),
            _ => Furniture.SpawnOwned(request),
        };

    public ApplyPortResult ApplyOwned(LocalObjectInstanceHandle handle, AuthoritativeObjectState desiredState) =>
        handle.Kind switch
        {
            RscObjectKind.Light => Lights.ApplyOwned(handle, desiredState),
            RscObjectKind.Actor => Actors is { } actors
                ? actors.ApplyOwned(handle, desiredState)
                : new(ApplyPortStatus.PermanentFailure, "This client has no actor host."),
            _ => Furniture.ApplyOwned(handle, desiredState),
        };

    public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle) =>
        handle.Kind switch
        {
            RscObjectKind.Light => Lights.ReleaseOwned(handle),
            RscObjectKind.Actor => Actors?.ReleaseOwned(handle) ?? new(ApplyPortStatus.Succeeded),
            _ => Furniture.ReleaseOwned(handle),
        };

    public int DestroyAll() => Furniture.DestroyAll() + Lights.DestroyAll() + (Actors?.DestroyAll() ?? 0);

    public int Shutdown() => Furniture.Shutdown() + Lights.Shutdown() + (Actors?.Shutdown() ?? 0);

    public void ClearUnsupported()
    {
        Furniture.ClearUnsupported();
        Lights.ClearUnsupported();
        Actors?.ClearUnsupported();
    }

    public int RetainUnsupported(Func<RscObjectId, bool> isWanted) =>
        Furniture.RetainUnsupported(isWanted) + Lights.RetainUnsupported(isWanted) +
        (Actors?.RetainUnsupported(isWanted) ?? 0);

    public bool IsDrawn(RscObjectId objectId) =>
        Furniture.IsDrawn(objectId) || Lights.IsDrawn(objectId) || (Actors?.IsDrawn(objectId) ?? false);

    public FurniturePreviewBegin BeginPreview(AuthoritativeObjectState authoritative)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        ILocalObjectPreviewHost host = authoritative switch
        {
            LightState => Lights,
            ActorState when Actors is { } actors => actors,
            ActorState => Furniture,
            _ => Furniture,
        };
        lock (gate)
        {
            if (previewOwner is not null && previewOwner.PreviewStatus(previewId) is
                FurniturePreviewStatus.Active or FurniturePreviewStatus.Committing)
                return new(FurniturePreviewBeginStatus.Busy, Guid.Empty);
        }
        var started = host.BeginPreview(authoritative);
        if (started.Started)
        {
            lock (gate)
            {
                previewOwner = host;
                previewId = started.Id;
            }
        }
        return started;
    }

    public FurniturePreviewStatus PreviewStatus(Guid id) => Owner(id)?.PreviewStatus(id) ?? FurniturePreviewStatus.Unknown;

    public bool MovePreview(Guid id, Vector3 position, Quaternion rotation) =>
        Owner(id)?.MovePreview(id, position, rotation) ?? false;

    public FurniturePreviewEnd EndPreview(Guid id) =>
        Owner(id)?.EndPreview(id) ?? new(false, null, default, default);

    public void CompletePreview(Guid id) => Owner(id)?.CompletePreview(id);

    public void CancelPreview(Guid id) => Owner(id)?.CancelPreview(id);

    private ILocalObjectPreviewHost? Owner(Guid id)
    {
        lock (gate)
            return id != Guid.Empty && id == previewId ? previewOwner : null;
    }

    private void RaiseRetired() => InstanceRetired?.Invoke();
}
