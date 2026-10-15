using System.Security.Cryptography;
using System.Text.Json;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Receiver;

public interface ILocalObjectStateFingerprint
{
    string Compute(AuthoritativeObjectState state);
}

public sealed class Sha256LocalObjectStateFingerprint : ILocalObjectStateFingerprint
{
    public string Compute(AuthoritativeObjectState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state is not (ActorState or FurnitureState or LightState) || !state.IsValid)
            throw new ArgumentException("Actor/furniture/light state is invalid.", nameof(state));

        var normalized = state switch
        {
            ActorState actor => JsonSerializer.SerializeToUtf8Bytes(new
            {
                actor.OwnerId,
                actor.SceneId,
                actor.BNpcBaseId,
                actor.Position,
                actor.Yaw,
                actor.Scale,
                actor.Controls,
            }),
            FurnitureState furniture => JsonSerializer.SerializeToUtf8Bytes(new
            {
                furniture.CreatorId,
                furniture.SceneId,
                furniture.FurnitureIdentity,
                furniture.Position,
                furniture.Rotation,
                furniture.Stain,
            }),
            LightState light => JsonSerializer.SerializeToUtf8Bytes(new
            {
                light.CreatorId,
                light.SceneId,
                light.LightIdentity,
                light.Position,
                light.Scale,
                light.Color,
                light.Intensity,
                light.Range,
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        return Convert.ToHexString(SHA256.HashData(normalized));
    }
}

public enum ReceiverLocalObjectIngressStatus
{
    Accepted,
    UnsupportedKind,
    Invalid,
    InactiveSession,
    StateRejected,
    AdmissionBackpressure,
}

public readonly record struct ReceiverLocalObjectIngressResult(
    ReceiverLocalObjectIngressStatus Status,
    LocalObjectApplyToken WorkToken)
{
    public bool Accepted => Status == ReceiverLocalObjectIngressStatus.Accepted;
}

/// <summary>
/// Kind-specific desired-state admission invoked by the single ordered object
/// stream cursor. It owns no sequence number; only the outer stream ingress may
/// consume an event after both store and mailbox admission succeed.
/// </summary>
public sealed class ReceiverLocalObjectIngress
{
    private readonly DesiredLocalObjectStore desiredStore;
    private readonly LocalObjectApplyCoordinator coordinator;
    private readonly ILocalObjectStateFingerprint fingerprint;

    public ReceiverLocalObjectIngress(
        DesiredLocalObjectStore desiredStore,
        LocalObjectApplyCoordinator coordinator,
        ILocalObjectStateFingerprint? fingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(desiredStore);
        ArgumentNullException.ThrowIfNull(coordinator);
        this.desiredStore = desiredStore;
        this.coordinator = coordinator;
        this.fingerprint = fingerprint ?? new Sha256LocalObjectStateFingerprint();
    }

    public ReceiverLocalObjectIngressResult AcceptState(
        ReceiverSessionKey session,
        AuthoritativeObjectState state)
    {
        if (!session.IsValid || state is null || !state.IsValid)
            return new(ReceiverLocalObjectIngressStatus.Invalid, default);
        if (state is not (ActorState or FurnitureState or LightState))
            return new(ReceiverLocalObjectIngressStatus.UnsupportedKind, default);

        if (!TryComputeFingerprint(state, out var stateFingerprint))
            return new(ReceiverLocalObjectIngressStatus.StateRejected, default);

        return AcceptState(session, state, stateFingerprint);
    }

    internal bool TryComputeFingerprint(
        AuthoritativeObjectState state,
        out string stateFingerprint)
    {
        stateFingerprint = string.Empty;
        if (state is not (ActorState or FurnitureState or LightState) || !state.IsValid)
            return false;

        try
        {
            stateFingerprint = fingerprint.Compute(state);
            return !string.IsNullOrWhiteSpace(stateFingerprint) &&
                stateFingerprint.Length <=
                    ClientLimits.MaximumDesiredStateFingerprintLength;
        }
        catch
        {
            stateFingerprint = string.Empty;
            return false;
        }
    }

    internal ReceiverLocalObjectIngressResult AcceptState(
        ReceiverSessionKey session,
        AuthoritativeObjectState state,
        string stateFingerprint)
    {
        if (!session.IsValid ||
            state is not (ActorState or FurnitureState or LightState) ||
            !state.IsValid ||
            string.IsNullOrWhiteSpace(stateFingerprint) ||
            stateFingerprint.Length >
                ClientLimits.MaximumDesiredStateFingerprintLength)
        {
            return new(ReceiverLocalObjectIngressStatus.Invalid, default);
        }

        var desired = desiredStore.Advance(session, state, stateFingerprint);
        if (!desired.Accepted || desired.Current is null)
            return new(MapAdvanceFailure(desired.Status), desired.Current?.Token ?? default);
        if (!coordinator.TryAdmit(session, state.ObjectId))
        {
            return new(
                ReceiverLocalObjectIngressStatus.AdmissionBackpressure,
                desired.Current.Token);
        }

        return new(ReceiverLocalObjectIngressStatus.Accepted, desired.Current.Token);
    }

    public ReceiverLocalObjectIngressResult AcceptRemoval(
        ReceiverSessionKey session,
        ObjectTombstone tombstone)
    {
        if (!session.IsValid || !tombstone.IsValid)
            return new(ReceiverLocalObjectIngressStatus.Invalid, default);
        if (tombstone.Kind is not (RscObjectKind.Actor or RscObjectKind.Furniture or RscObjectKind.Light))
            return new(ReceiverLocalObjectIngressStatus.UnsupportedKind, default);

        var removal = desiredStore.AdvanceRemoval(session, tombstone);
        if (!removal.Accepted || removal.Current is null)
            return new(MapRemovalFailure(removal.Status), removal.Current?.Token ?? default);
        if (!removal.Current.Completed &&
            !coordinator.TryAdmit(session, tombstone.ObjectId))
        {
            return new(
                ReceiverLocalObjectIngressStatus.AdmissionBackpressure,
                removal.Current.Token);
        }

        return new(ReceiverLocalObjectIngressStatus.Accepted, removal.Current.Token);
    }

    private static ReceiverLocalObjectIngressStatus MapAdvanceFailure(
        DesiredLocalObjectAdvanceStatus status) => status switch
        {
            DesiredLocalObjectAdvanceStatus.InactiveSession =>
                ReceiverLocalObjectIngressStatus.InactiveSession,
            DesiredLocalObjectAdvanceStatus.CapacityExceeded =>
                ReceiverLocalObjectIngressStatus.AdmissionBackpressure,
            _ => ReceiverLocalObjectIngressStatus.StateRejected,
        };

    private static ReceiverLocalObjectIngressStatus MapRemovalFailure(
        DesiredLocalObjectRemovalStatus status) => status switch
        {
            DesiredLocalObjectRemovalStatus.InactiveSession =>
                ReceiverLocalObjectIngressStatus.InactiveSession,
            DesiredLocalObjectRemovalStatus.CapacityExceeded =>
                ReceiverLocalObjectIngressStatus.AdmissionBackpressure,
            _ => ReceiverLocalObjectIngressStatus.StateRejected,
        };
}
