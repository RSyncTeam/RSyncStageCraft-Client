using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

public enum ReceiverActorSnapshotStatus
{
    Staged,
    Completed,
    AlreadyCompleted,
    Invalid,
    InactiveSession,
    Busy,
    StoreRejected,
    AdmissionBackpressure,
}

public readonly record struct ReceiverActorSnapshotResult(
    ReceiverActorSnapshotStatus Status,
    ServerSequence Watermark,
    int RemainingAdmissions)
{
    public DesiredLocalObjectSnapshotStatus? LocalStoreStatus { get; init; }

    public bool Accepted => Status is
        ReceiverActorSnapshotStatus.Staged or
        ReceiverActorSnapshotStatus.Completed or
        ReceiverActorSnapshotStatus.AlreadyCompleted;
}

/// <summary>
/// Atomically stages the supported portions of a validated authoritative
/// snapshot, then admits local-object identities into their bounded mailbox
/// over one or more explicit passes. Incremental intake is initialized only
/// after the final admission succeeds.
/// </summary>
public sealed class ReceiverActorSnapshotIngress
{
    private readonly record struct PreparedSnapshot(
        AuthoritativeObjectState[] Objects,
        ObjectTombstone[] Tombstones);

    private readonly object gate = new();
    private readonly ReceiverApplyAuthority authority;
    private readonly ReceiverActorEventIngress eventIngress;
    private readonly DesiredLocalObjectStore? localObjectStore;
    private readonly LocalObjectApplyCoordinator? localObjectCoordinator;
    private readonly ILocalObjectStateFingerprint? localObjectFingerprint;
    private readonly IReceiverReadinessPort? readiness;
    private readonly IReceiverReadinessAdmissionFence? readinessAdmissionFence;
    private readonly HumanActorDesiredSet? humanActors;
    private readonly Queue<LocalObjectApplyToken> pending = new();
    private ReceiverSessionKey? stagedSession;
    private ServerSequence stagedWatermark;

    public ReceiverActorSnapshotIngress(
        ReceiverApplyAuthority authority,
        ReceiverActorEventIngress eventIngress,
        DesiredLocalObjectStore? localObjectStore = null,
        LocalObjectApplyCoordinator? localObjectCoordinator = null,
        ILocalObjectStateFingerprint? localObjectFingerprint = null,
        IReceiverReadinessPort? readiness = null,
        HumanActorDesiredSet? humanActors = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(eventIngress);
        this.humanActors = humanActors;
        if ((localObjectStore is null) != (localObjectCoordinator is null))
        {
            throw new ArgumentException(
                "Local object store and coordinator must be configured together.");
        }
        this.authority = authority;
        this.eventIngress = eventIngress;
        this.localObjectStore = localObjectStore;
        this.localObjectCoordinator = localObjectCoordinator;
        this.localObjectFingerprint = localObjectStore is null
            ? null
            : localObjectFingerprint ?? new Sha256LocalObjectStateFingerprint();
        this.readiness = readiness;
        readinessAdmissionFence = readiness as IReceiverReadinessAdmissionFence;
    }

    public int PendingCount
    {
        get
        {
            lock (gate)
                return pending.Count;
        }
    }

    public bool HasPendingFor(ReceiverSessionKey receiverSession)
    {
        lock (gate)
            return stagedSession == receiverSession && pending.Count > 0;
    }

    public ReceiverActorSnapshotResult Stage(
        ReceiverSessionKey receiverSession,
        ObjectSnapshotMessage snapshot)
    {
        if (!receiverSession.IsValid ||
            !snapshot.Watermark.IsValid ||
            snapshot.Objects is null ||
            snapshot.Tombstones is null)
        {
            return Result(ReceiverActorSnapshotStatus.Invalid, snapshot.Watermark);
        }
        if (!authority.IsCurrent(receiverSession))
            return Result(ReceiverActorSnapshotStatus.InactiveSession, snapshot.Watermark);

        // Fast point-in-time rejection avoids invoking supplied fingerprint
        // implementations for a snapshot that is already known to conflict.
        // All state is checked again after calculation.
        lock (gate)
        {
            if (eventIngress.IsInitializedFor(receiverSession, snapshot.Watermark))
            {
                return ResultUnsafe(
                    ReceiverActorSnapshotStatus.AlreadyCompleted,
                    snapshot.Watermark);
            }
            if (eventIngress.IsInitializedForSession(receiverSession))
                return ResultUnsafe(ReceiverActorSnapshotStatus.Busy, snapshot.Watermark);

            if (stagedSession is not null)
            {
                return ResultUnsafe(ReceiverActorSnapshotStatus.Busy, snapshot.Watermark);
            }
        }

        DesiredLocalObjectSnapshotState[] localStates;
        PreparedSnapshot prepared;
        try
        {
            if (!TryPrepareSnapshot(snapshot, out prepared))
                return Result(ReceiverActorSnapshotStatus.Invalid, snapshot.Watermark);

            // Fingerprinting is pure preparation. Keep host-supplied
            // implementations outside the staging and receiver-authority
            // locks so disconnect can invalidate immediately. A delayed
            // result still has to win the exact final commit below.
            localStates = localObjectStore is null
                ? Array.Empty<DesiredLocalObjectSnapshotState>()
                : prepared.Objects
                    .Where(state => state is ActorState or FurnitureState or LightState)
                    .Select(state => new DesiredLocalObjectSnapshotState(
                        state,
                        localObjectFingerprint!.Compute(state)))
                    .ToArray();
        }
        catch
        {
            return Result(ReceiverActorSnapshotStatus.Invalid, snapshot.Watermark);
        }

        // Protocol 7: the snapshot's humans replace the human domain's desired
        // set in the same authority commit; they never reach the local store.
        var humanStates = humanActors is null
            ? Array.Empty<HumanActorState>()
            : prepared.Objects.OfType<HumanActorState>().ToArray();

        var localTombstones = localObjectStore is null
            ? Array.Empty<ObjectTombstone>()
            : prepared.Tombstones
                .Where(tombstone =>
                    tombstone.Kind is RscObjectKind.Actor or RscObjectKind.Furniture or RscObjectKind.Light)
                .ToArray();

        ReceiverActorSnapshotResult Commit()
        {
            lock (gate)
            {
                if (eventIngress.IsInitializedFor(receiverSession, snapshot.Watermark))
                {
                    return ResultUnsafe(
                        ReceiverActorSnapshotStatus.AlreadyCompleted,
                        snapshot.Watermark);
                }
                if (eventIngress.IsInitializedForSession(receiverSession) ||
                    stagedSession is not null)
                {
                    return ResultUnsafe(ReceiverActorSnapshotStatus.Busy, snapshot.Watermark);
                }

                DesiredLocalObjectSnapshotResult? localApplied = null;
                var authorized = authority.TryExecute(
                    receiverSession,
                    () =>
                    {
                        if (humanActors is not null && !HumanActorDesiredSet.CanApplySnapshot(humanStates.Length))
                            return false;

                        if (localObjectStore is not null)
                        {
                            var localValidation = localObjectStore.ValidateSnapshot(
                                receiverSession,
                                localStates,
                                localTombstones);
                            localApplied = localValidation;
                            if (!localValidation.Applied)
                                return false;
                        }

                        if (localObjectStore is not null)
                        {
                            localApplied = localObjectStore.ApplySnapshot(
                                receiverSession,
                                localStates,
                                localTombstones);
                            if (!localApplied.Value.Applied)
                            {
                                throw new InvalidOperationException(
                                    "Validated local snapshot plan changed during authority commit.");
                            }
                        }

                        humanActors?.ApplySnapshot(receiverSession, humanStates);
                        return true;
                    },
                    out var committed);
                if (!authorized)
                {
                    return ResultUnsafe(
                        ReceiverActorSnapshotStatus.InactiveSession,
                        snapshot.Watermark);
                }
                if (!committed)
                {
                    return new(
                        ReceiverActorSnapshotStatus.StoreRejected,
                        snapshot.Watermark,
                        RemainingAdmissions: 0)
                    {
                        LocalStoreStatus = localApplied?.Status,
                    };
                }

                stagedSession = receiverSession;
                stagedWatermark = snapshot.Watermark;
                var localWork = localApplied?.Work ?? Array.Empty<LocalObjectApplyWorkItem>();
                foreach (var work in localWork)
                    pending.Enqueue(work.Token);

                if (pending.Count != 0)
                    return ResultUnsafe(ReceiverActorSnapshotStatus.Staged, stagedWatermark);

                return CompleteUnsafe(receiverSession);
            }
        }

        return TryExecuteIfReady(Commit, out var admitted)
            ? admitted
            : Result(ReceiverActorSnapshotStatus.InactiveSession, snapshot.Watermark);
    }

    private static bool TryPrepareSnapshot(
        ObjectSnapshotMessage snapshot,
        out PreparedSnapshot prepared)
    {
        var identities = new HashSet<RscObjectId>();
        var objects = new List<AuthoritativeObjectState>();
        foreach (var state in snapshot.Objects)
        {
            if (objects.Count >= ProtocolLimits.MaximumObjectsPerSnapshot ||
                state is null ||
                !state.IsValid ||
                !identities.Add(state.ObjectId))
            {
                prepared = default;
                return false;
            }

            objects.Add(state);
        }

        var tombstones = new List<ObjectTombstone>();
        foreach (var tombstone in snapshot.Tombstones)
        {
            if (objects.Count + tombstones.Count >= ProtocolLimits.MaximumObjectsPerSnapshot ||
                !tombstone.IsValid ||
                !identities.Add(tombstone.ObjectId))
            {
                prepared = default;
                return false;
            }

            tombstones.Add(tombstone);
        }

        prepared = new(objects.ToArray(), tombstones.ToArray());
        return true;
    }

    public ReceiverActorSnapshotResult AdmitPass(
        ReceiverSessionKey receiverSession,
        int maximumEntries)
    {
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        return TryExecuteIfReady(
            () => AdmitPassReady(receiverSession, maximumEntries),
            out var admitted)
            ? admitted
            : CurrentResult(ReceiverActorSnapshotStatus.InactiveSession);
    }

    private ReceiverActorSnapshotResult AdmitPassReady(
        ReceiverSessionKey receiverSession,
        int maximumEntries)
    {
        lock (gate)
        {
            if (!authority.IsCurrent(receiverSession))
            {
                var watermark = stagedWatermark;
                ClearUnsafe(receiverSession);
                return ResultUnsafe(
                    ReceiverActorSnapshotStatus.InactiveSession,
                    watermark);
            }
            if (stagedSession != receiverSession)
                return ResultUnsafe(ReceiverActorSnapshotStatus.Invalid, stagedWatermark);

            var blocked = false;
            var examinationLimit = Math.Min(maximumEntries, pending.Count);
            for (var examined = 0;
                examined < examinationLimit && pending.TryPeek(out var admission);
                examined++)
            {
                if (!localObjectStore!.IsCurrent(admission))
                {
                    pending.Dequeue();
                    continue;
                }

                var admittedCurrent = localObjectCoordinator!.TryAdmit(
                    admission.Session,
                    admission.ObjectId);
                if (!admittedCurrent)
                {
                    pending.Dequeue();
                    pending.Enqueue(admission);
                    blocked = true;
                    continue;
                }

                pending.Dequeue();
            }

            return pending.Count == 0
                ? CompleteUnsafe(receiverSession)
                : ResultUnsafe(
                    blocked
                        ? ReceiverActorSnapshotStatus.AdmissionBackpressure
                        : ReceiverActorSnapshotStatus.Staged,
                    stagedWatermark);
        }
    }

    private bool TryExecuteIfReady<T>(Func<T> operation, out T result)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (readinessAdmissionFence is not null)
            return readinessAdmissionFence.TryExecuteIfReady(operation, out result);

        if (readiness is not null)
        {
            try
            {
                if (!readiness.IsReady)
                {
                    result = default!;
                    return false;
                }
            }
            catch
            {
                result = default!;
                return false;
            }
        }

        result = operation();
        return true;
    }

    public bool ClearSession(ReceiverSessionKey receiverSession)
    {
        lock (gate)
            return ClearUnsafe(receiverSession);
    }

    private ReceiverActorSnapshotResult CompleteUnsafe(ReceiverSessionKey receiverSession)
    {
        var watermark = stagedWatermark;
        if (!eventIngress.InitializeFromSnapshotWatermark(receiverSession, watermark))
        {
            ClearUnsafe(receiverSession);
            return ResultUnsafe(ReceiverActorSnapshotStatus.InactiveSession, watermark);
        }

        ClearUnsafe(receiverSession);
        return ResultUnsafe(ReceiverActorSnapshotStatus.Completed, watermark);
    }

    private bool ClearUnsafe(ReceiverSessionKey receiverSession)
    {
        if (stagedSession != receiverSession)
            return false;

        pending.Clear();
        stagedSession = null;
        stagedWatermark = default;
        return true;
    }

    private ReceiverActorSnapshotResult Result(
        ReceiverActorSnapshotStatus status,
        ServerSequence watermark)
    {
        lock (gate)
            return ResultUnsafe(status, watermark);
    }

    private ReceiverActorSnapshotResult CurrentResult(
        ReceiverActorSnapshotStatus status)
    {
        lock (gate)
            return ResultUnsafe(status, stagedWatermark);
    }

    private ReceiverActorSnapshotResult ResultUnsafe(
        ReceiverActorSnapshotStatus status,
        ServerSequence watermark) =>
        new(status, watermark, pending.Count);
}
