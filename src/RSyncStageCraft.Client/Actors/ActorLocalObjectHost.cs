using System.Globalization;
using System.Numerics;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Actors;

/// <summary>
/// Actor host for the local-object port: one client-only battle
/// character per protocol 6 <see cref="ActorState"/>, owned through an exact
/// receipt, with its own ownership records and native backend (separate from
/// furniture and lights).
/// <list type="bullet">
/// <item><b>Spawn.</b> A COM slot is chosen and the created object is proved
/// to be the one this call made (its own COM entry), then verified before any
/// other write: its actual global index is in 200–448, the object table holds
/// it there, it is not the local player and it is a battle NPC or has no kind
/// yet. On any mismatch only that exact new COM object is deleted and nothing
/// is owned.</item>
/// <item><b>Setup transition.</b> A fresh kind of None or EventNpc is tolerated only between
/// creation and the end of setup, within the same game-thread call. After
/// setup the object must be a battle NPC carrying this spawn's ownership tag
/// and still match its slot, address and table entry, or only that exact
/// allocation is released, the failure reported and no receipt
/// returned.</item>
/// <item><b>Hidden construction.</b> Alpha 0, set up as the creature, placed,
/// then drawn while invisible; shown (alpha 1) and counted as drawn only once
/// the draw object has the creature's model. The waits are the receiver's
/// bounded loading polls; a timeout releases the exact instance.</item>
/// <item><b>Ownership</b> (<see cref="Verify"/>) is re-read in full
/// immediately before every native write and before the delete: the object
/// table entry at the recorded global index is the recorded address; the
/// object's COM index is the slot it was created in and that COM entry holds
/// the address; its global index, GameObjectId and EntityId are unchanged;
/// it is a battle NPC; and its name is this spawn's random tag. No single
/// fact is trusted alone. On any mismatch the object is never touched: the
/// record is forgotten and reported as removed or replaced outside
/// RSC.</item>
/// <item><b>Release</b> re-verifies, clears RSC's per-actor control state
/// first, then hides, stops drawing and deletes through the creation slot,
/// re-running the full proof immediately before each of those steps. A lost
/// proof stops every later step (the record is abandoned, the object left
/// untouched); a step that only throws while the proof holds does not stop
/// the next one.</item>
/// <item>Not drawn (reported): unknown creature, demihuman equipment
/// missing, not a monster or demihuman, actor limit reached, unavailable on
/// this game version, and any non-default control.</item>
/// </list>
/// </summary>
public sealed class ActorLocalObjectHost :
    ILocalObjectRuntimePort,
    ILocalObjectRuntimeOwnershipDiagnostics,
    ILocalObjectPreviewHost
{
    public const int MaximumReports = 64;

    public const string UnavailableReason = "Actors are unavailable on this game version";
    public const string LimitReason = "Actor limit reached";
    public const string ControlsUnavailableReason =
        "Animation is not available on this client yet";
    /// <summary>
    /// A received actor carries a look-at value. RSC has no look-at (removed);
    /// the protocol field is reserved and always
    /// empty, so such an actor is refused before anything is touched or drawn.
    /// </summary>
    public const string LookAtNotSupportedReason = "Look-at is not supported";
    /// <summary>
    /// A received actor is frozen. RSC has no freeze (removed: it was not
    /// reliable across construction and redraw); the protocol field is reserved and always false, so such an
    /// actor is refused before anything is touched or drawn.
    /// </summary>
    public const string FreezeNotSupportedReason = "Freeze is not supported";
    /// <summary>
    /// The actor's synchronized loop could not be confirmed natively while it
    /// was being drawn: it was released and is not drawn (fail closed) until
    /// its controls change or the location changes.
    /// </summary>
    public const string StandingControlsFailedReason = "Its animation could not be applied on this client";
    /// <summary>The first such failure: released, and drawn once more from a fresh spawn.</summary>
    public const string StandingControlsRetryingReason = "Its animation could not be applied on this client (trying once more)";
    /// <summary>A control failure whose release RSC could not confirm (a step failed); see the log for which.</summary>
    public const string StandingControlsReleaseIncompleteReason =
        "Its animation could not be applied on this client, and RSC could not confirm it was hidden, undrawn and released";
    /// <summary>Added to a report while an earlier, unprovable object for the same actor is still present (left untouched).</summary>
    public const string UntouchedEarlierObjectNote =
        "an earlier object for this actor could no longer be proven RSC's and was left untouched (it may still be visible)";

    // Drawing an actor with a standing loop (observed in game: the
    // demihuman's loop did not take on a redraw after a reconnect or restart).
    /// <summary>Consecutive frames the creature's own animation must run before its loop is played.</summary>
    public const int ReadyStableFrames = 15;
    /// <summary>Frames a new actor may wait for its animation to run before failing closed.</summary>
    public const int StandingControlsDeadlineFrames = 240;
    /// <summary>Consecutive frames the played loop must be in a timeline slot before the actor is shown.</summary>
    public const int LoopHoldFrames = 10;
    /// <summary>Consecutive frames the loop may be missing from every slot before it is played again.</summary>
    public const int LoopReplayAfterFrames = 30;
    /// <summary>How many times a new actor's loop is played again while it is drawn.</summary>
    public const int MaximumLoopReplays = 4;
    /// <summary>Frames after the play within which the loop must hold, or the actor fails closed.</summary>
    public const int LoopConfirmDeadlineFrames = 240;
    /// <summary>Frames between checks that a drawn actor's loop is still in a slot.</summary>
    public const int LoopCheckIntervalFrames = 30;
    /// <summary>Fresh draws of one actor with the same controls before its failure sticks (until they or the location change).</summary>
    public const int StandingControlsAttempts = 2;
    /// <summary>How many standing-control failures are kept for diagnostics (newest last).</summary>
    public const int MaximumControlFailures = 8;
    public const string AnimationUnavailableReason = "Animation is not available on this game version";
    public const string UnknownAnimationReason = "Unknown animation";
    public const string AnimationNotVerifiedForVersionReason = "Animations are not verified for this game version";
    public const string AnimationNotAllowedReason = "This animation is not verified for this creature";
    public const string AnimationDataChangedReason = "This animation's game data differs from the verified row";
    public const string OneShotUnavailableReason = "One-shot animations are not available";
    public const string ReplacedReason =
        "The actor was removed or replaced outside RSC; that object was left untouched";
    public const string AbandonedNotice =
        "RSC could no longer prove this client-only object was its own, so it stopped writing to it and did not delete it";
    /// <summary>The fixed maximum of abandoned records kept (current orphans only; see <see cref="PruneAbandoned"/>).</summary>
    public const int MaximumAbandoned = 16;

    /// <summary>How many creation attempts are kept for diagnostics (newest last).</summary>
    public const int MaximumCreationAttempts = 8;

    /// <summary>
    /// A creation check that can never pass for this actor (the object-kind
    /// checks): not retried. The gate stays in <see cref="CreationAttempts"/>
    /// and is logged; it is not shown to the player.
    /// </summary>
    public const string CreationPermanentText =
        "This client cannot draw this actor safely (the game data did not match what RSC expects). " +
        "Update the plugin; Retry all scene objects in Scene here tries once more.";
    /// <summary>A transient creation check failed; another attempt is scheduled.</summary>
    public const string CreationRetryingText = "Could not verify a safe slot to draw this actor yet; retrying.";
    /// <summary>The transient creation checks kept failing until the attempts ran out.</summary>
    public const string CreationExhaustedText =
        "This actor could not be drawn after several tries. Use Retry all scene objects in Scene here.";
    public const string SetupFailedReason =
        "The actor did not become this client's battle NPC after setup; only that allocation was released";

    /// <summary>The receiver's local-object retry policy: an initial attempt plus five retries.</summary>
    public static readonly int DefaultCreationAttempts = LocalObjectApplyRetryPolicy.Default.MaximumAttempts + 1;

    private readonly IActorSceneBackend backend;
    private readonly IActorControlBackend? controls;
    private readonly IActorTimelineData? timelines;
    private readonly ActorControlVerification verification;
    private readonly ActorAnimationAllowlist allowlist;
    private readonly string? gameVersion;
    private readonly Func<uint, ActorDrawPlanResult> resolve;
    private readonly int creationAttempts;
    private readonly int activeLimit;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Owned> owned = new();
    // RSC's per-actor control/hook state. An address is in it
    // only while verified as owned; release removes it before any other step.
    private readonly HashSet<nint> controlled = new();
    private readonly List<FurnitureUnsupportedReport> reports = new();
    private readonly Dictionary<RscObjectId, (LocalObjectApplyToken Token, int Failures)> creationFailures = new();
    private readonly HashSet<RscObjectId> limitRefused = new();
    // Actors whose standing controls could not be confirmed while drawing
    // (by object and exact controls): refused until those controls change or
    // the location changes.
    private readonly Dictionary<(RscObjectId ObjectId, ActorControlState Controls), int> standingControlsFailed = new();
    private readonly List<ActorControlFailure> controlFailures = new();
    private long controlFailureSequence;
    private long frame;
    // Records retired without deleting their object (the proof failed). Kept
    // across cleanup (never cleared with the reports) so a location change or
    // unload never counts them as freed.
    private readonly List<(AbandonedActor Record, nint Address)> abandoned = new();
    private long abandonedTotal;
    private readonly List<ActorCreationAttempt> attemptHistory = new();
    private long creationSequence;
    private long abandonedDropped;
    private bool closed;
    private Preview? preview;
    private (Guid Id, FurniturePreviewStatus Status) lastPreview;

    /// <summary>Raised (game thread, outside the lock) after a native fault retired an owned actor.</summary>
    public event Action? InstanceRetired;

    /// <summary>
    /// Raised (game thread, outside the lock) when an actor was released while
    /// another one had been refused for the actor limit, so the refused one
    /// can be tried again.
    /// </summary>
    public event Action? CapacityFreed;

    /// <param name="resolve">The local draw plan for a BNpcBase row (<see cref="ActorAppearanceResolver.Resolve"/>).</param>
    /// <param name="activeLimit">Test seam; production uses <see cref="ActorCapacityPolicy.ActiveMaximumActorsPerScene"/>.</param>
    /// <param name="controls">Native control primitives; without them every non-default control is refused.</param>
    /// <param name="timelines">The local ActionTimeline sheet (loop or one-shot).</param>
    /// <param name="verification">Which control paths passed their in-game check; production uses <see cref="ActorControlVerification.Current"/>.</param>
    /// <param name="allowlist">The curated animation allowlist; production uses <see cref="ActorAnimationAllowlist.Current"/>.</param>
    /// <param name="gameVersion">This client's game version; animation is offered only when it matches the allowlist's.</param>
    public ActorLocalObjectHost(
        IActorSceneBackend backend,
        Func<uint, ActorDrawPlanResult> resolve,
        int? creationAttempts = null,
        int? activeLimit = null,
        IActorControlBackend? controls = null,
        IActorTimelineData? timelines = null,
        ActorControlVerification? verification = null,
        ActorAnimationAllowlist? allowlist = null,
        string? gameVersion = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(resolve);
        this.backend = backend;
        this.controls = controls;
        this.timelines = timelines;
        this.verification = verification ?? ActorControlVerification.Current;
        this.allowlist = allowlist ?? ActorAnimationAllowlist.Current;
        this.gameVersion = gameVersion;
        this.resolve = resolve;
        this.creationAttempts = creationAttempts ?? DefaultCreationAttempts;
        this.activeLimit = activeLimit ?? ActorCapacityPolicy.ActiveMaximumActorsPerScene;
        if (this.creationAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(creationAttempts));
        if (this.activeLimit < 1 || this.activeLimit > ActorCapacityPolicy.AbsoluteMaximumActorsPerScene)
            throw new ArgumentOutOfRangeException(nameof(activeLimit));
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

    /// <summary>
    /// The current abandoned client objects: records retired because their
    /// object could no longer be proven RSC's, whose object is still observed
    /// at its recorded slot or table entry. Newest last; at most
    /// <see cref="MaximumAbandoned"/>.
    /// </summary>
    public IReadOnlyList<AbandonedActor> Abandoned
    {
        get
        {
            lock (gate)
                return abandoned.Select(item => item.Record).ToArray();
        }
    }

    /// <summary>
    /// The last creation attempts (at most <see cref="MaximumCreationAttempts"/>,
    /// newest last), with the gate each one failed at, if any. Non-sensitive
    /// facts only: indices, booleans and the kind; no address.
    /// </summary>
    public IReadOnlyList<ActorCreationAttempt> CreationAttempts
    {
        get
        {
            lock (gate)
                return attemptHistory.ToArray();
        }
    }

    /// <summary>How many frames (<see cref="Tick"/>) this host has run.</summary>
    public long FramesRun
    {
        get
        {
            lock (gate)
                return frame;
        }
    }

    /// <summary>The last standing-control failures (at most <see cref="MaximumControlFailures"/>, newest last; every build).</summary>
    public IReadOnlyList<ActorControlFailure> ControlFailures
    {
        get
        {
            lock (gate)
                return controlFailures.ToArray();
        }
    }

    /// <summary>How many records were ever retired without deleting their object (a counter, not a history).</summary>
    public long AbandonedCount
    {
        get
        {
            lock (gate)
                return abandonedTotal;
        }
    }

    /// <summary>Abandoned records dropped because <see cref="MaximumAbandoned"/> was reached (reported, not kept).</summary>
    public long DroppedAbandonedRecords
    {
        get
        {
            lock (gate)
                return abandonedDropped;
        }
    }

    /// <summary>
    /// Read-only: forgets every abandoned record whose object is observably
    /// gone (neither its creation slot nor its global table entry holds the
    /// recorded address any more). Nothing is written to or deleted from the
    /// game. A record whose slot or entry still holds that address stays, even
    /// if another party's object now lives there. Game thread. Returns how many
    /// were forgotten.
    /// </summary>
    public int PruneAbandoned()
    {
        (AbandonedActor Record, nint Address)[] current;
        lock (gate)
            current = abandoned.ToArray();
        var gone = new List<long>();
        foreach (var (record, address) in current)
        {
            try
            {
                if (backend.ComObjectByIndex(record.CreatedComIndex) != address &&
                    backend.ObjectTableEntry(record.GlobalIndex) != address)
                    gone.Add(record.Sequence);
            }
            catch (Exception)
            {
                // Unknown: keep the record.
            }
        }
        if (gone.Count == 0)
            return 0;
        lock (gate)
            return abandoned.RemoveAll(item => gone.Contains(item.Record.Sequence));
    }

    public bool IsShutDown
    {
        get
        {
            lock (gate)
                return closed;
        }
    }

    /// <summary>Whether an address is a verified, RSC-owned actor (the set the control hooks consult).</summary>
    public bool IsOwnedAddress(nint address)
    {
        lock (gate)
            return address != 0 && controlled.Contains(address);
    }

    public static string IdentityOf(uint bnpcBaseId) =>
        "bnpc:" + bnpcBaseId.ToString(CultureInfo.InvariantCulture);

    public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(LocalObjectSpawnRequest request)
    {
        if (IsShutDown)
            return new(ApplyPortStatus.Unavailable, Error: "The actor host is shut down.");
        if (!request.IsValid)
            return new(ApplyPortStatus.PermanentFailure, Error: "Invalid spawn request.");
        if (request.DesiredState is not ActorState actor || !actor.IsValid)
            return new(ApplyPortStatus.PermanentFailure, Error: "Only valid actors are drawn by the actor host.");

        if (!IsAvailable())
            return Refuse(actor, UnavailableReason);
        var plan = resolve(actor.BNpcBaseId);
        if (plan.Plan is not { } draw)
            return Refuse(actor, plan.NotDrawnReason ?? ActorAppearanceResolver.UnknownCreature);
        // A control this client cannot apply: the actor is never shown in a
        // wrong state; it is listed instead (default controls always draw).
        if (UnavailableControlReason(actor) is { } controlReason)
            return Refuse(actor, controlReason);
        lock (gate)
        {
            if (owned.Count >= activeLimit)
            {
                limitRefused.Add(actor.ObjectId);
                ReportUnsafe(actor.ObjectId, IdentityOf(actor.BNpcBaseId), LocalObjectReportKind.NotSupported, LimitReason);
                return new(ApplyPortStatus.PermanentFailure, Error: LimitReason);
            }
        }

        var created = CreateVerified(ActorOwnershipTag.Create());
        if (created is not { } instance)
        {
            ActorCreationAttempt? last;
            lock (gate)
                last = attemptHistory.Count == 0 ? null : attemptHistory[^1];
            var reason = last?.FailedGate is { } failedGate ? "Could not create the actor: " + failedGate : null;
            if (last is { Permanent: true })
            {
                // Retrying would allocate and delete the same object again:
                // listed, not retried (a later reset or change tries once more).
                Report(actor.ObjectId, IdentityOf(actor.BNpcBaseId), LocalObjectReportKind.Failed, CreationPermanentText);
                return new(ApplyPortStatus.PermanentFailure, Error: reason);
            }
            // A creation check: the player text names no gate (null reason).
            RecordCreationFailure(actor, request.Token);
            return new(ApplyPortStatus.TransientFailure, Error: "No free client-only actor slot could be verified.");
        }

        var handle = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            request.Token.ObjectId,
            RscObjectKind.Actor,
            request.SpawnGeneration);
        var entry = new Owned(handle, instance, draw);
        lock (gate)
        {
            if (closed)
            {
                Free(entry);
                return new(ApplyPortStatus.Unavailable, Error: "The actor host is shut down.");
            }
            owned[handle.OwnershipId] = entry;
            controlled.Add(instance.Address);
            limitRefused.Remove(actor.ObjectId);
        }

        // The setup transition (same game-thread call as the creation, so no
        // frame passes): an unset kind is tolerated only until setup ends.
        // Each write is preceded by the full proof for its phase; a lost proof
        // stops every later write and leaves the object untouched.
        var setUp = false;
        try
        {
            WriteOwned(entry, OwnershipCheck.Created, address => backend.SetAlpha(address, 0f));
            WriteOwned(entry, OwnershipCheck.Created, address => backend.Setup(address, draw, instance.Tag));
            setUp = Verify(entry, OwnershipCheck.SetUp);
        }
        catch (InvalidOperationException) when (entry.Lost)
        {
            // Already forgotten and reported as abandoned: no delete.
            RecordCreationFailure(actor, request.Token, ReplacedReason);
            return new(ApplyPortStatus.TransientFailure, Error: ReplacedReason);
        }
        catch (Exception)
        {
            setUp = false;
        }
        if (!setUp)
        {
            // Release only this exact allocation (identity re-read before each
            // step, no kind or tag needed), report, and return no receipt.
            Forget(entry, null);
            var outcome = Free(entry, OwnershipCheck.Identity);
            var reason = outcome == FreeOutcome.Abandoned ? ReplacedReason : SetupFailedReason;
            RecordCreationFailure(actor, request.Token, reason);
            return new(ApplyPortStatus.TransientFailure, Error: reason);
        }
        lock (gate)
            entry.SetUp = true;

        // From here the receipt is returned on every path, so the receiver can
        // always release this exact instance.
        try
        {
            WriteTransform(entry, actor);
            return new(ApplyPortStatus.AwaitingPayload, handle, "The actor is being built.");
        }
        catch (Exception exception)
        {
            return new(ApplyPortStatus.TransientFailure, handle, $"The actor could not be placed: {exception.Message}");
        }
    }

    public ApplyPortResult ApplyOwned(LocalObjectInstanceHandle handle, AuthoritativeObjectState desiredState)
    {
        if (desiredState is not ActorState actor || actor.ObjectId != handle.ObjectId)
            return new(ApplyPortStatus.PermanentFailure, "The desired state does not match this receipt.");
        Owned? entry;
        lock (gate)
            owned.TryGetValue(handle.OwnershipId, out entry);
        if (entry is null || entry.Handle != handle)
            return new(ApplyPortStatus.TransientFailure, "The instance no longer exists.");
        if (!actor.IsValid || actor.BNpcBaseId != entry.Plan.BNpcBaseId)
            return new(ApplyPortStatus.PermanentFailure, "Unsupported actor.");

        // A requested control this client cannot apply: never show the actor
        // in a wrong state. The fault makes the receiver release this exact
        // instance; the respawn then lists it as not drawn.
        if (UnavailableControlReason(actor) is { } controlReason)
        {
            if (controlReason == StandingControlsFailedReason)
            {
                // Listed as not drawn only once this instance is hidden, undrawn and released.
                var release = ReleaseForControlFailure(entry);
                if (release.Outcome != FreeOutcome.Abandoned)
                    Report(actor.ObjectId, IdentityOf(actor.BNpcBaseId),
                        release.Complete ? LocalObjectReportKind.NotSupported : LocalObjectReportKind.Failed,
                        release.Complete ? controlReason : StandingControlsReleaseIncompleteReason);
                throw new InvalidOperationException(controlReason);
            }
            Report(actor.ObjectId, IdentityOf(actor.BNpcBaseId), LocalObjectReportKind.NotSupported, controlReason);
            throw new InvalidOperationException(controlReason);
        }

        // The ownership proof failed (the slot was freed or reused): forget
        // it without touching that object; the fault releases the receipt.
        EnsureOwned(entry, OwnershipCheck.SetUp);

        lock (gate)
            EndPreviewForUnsafe(entry, FurniturePreviewStatus.Superseded);

        if (!entry.Shown)
            return Construct(entry, actor);

        WriteTransform(entry, actor);
        // A control change applies only what changed.
        ApplyControls(entry, actor.Controls);
        lock (gate)
            entry.Applied = actor;
        return new(ApplyPortStatus.Succeeded);
    }

    public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
    {
        Owned? entry;
        bool capacityFreed;
        lock (gate)
        {
            if (!owned.TryGetValue(handle.OwnershipId, out entry) || entry.Handle != handle)
                return new(ApplyPortStatus.Succeeded);
            owned.Remove(handle.OwnershipId);
            EndPreviewForUnsafe(entry, FurniturePreviewStatus.Released);
            capacityFreed = limitRefused.Count > 0;
        }
        var freed = Free(entry);
        if (capacityFreed)
            CapacityFreed?.Invoke();
        return freed switch
        {
            FreeOutcome.Freed => new(ApplyPortStatus.Succeeded),
            FreeOutcome.Abandoned => new(ApplyPortStatus.Succeeded, AbandonedNotice + "."),
            _ => new(ApplyPortStatus.Succeeded, "The actor was released, but freeing it reported an error."),
        };
    }

    /// <summary>
    /// Frees every actor this host owns (location change, GPose or cutscene,
    /// unload). Returns how many were verified and deleted; a record whose
    /// object could no longer be proven RSC's is retired untouched, counted in
    /// <see cref="AbandonedCount"/>, and never reported as freed.
    /// </summary>
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
        var freed = 0;
        foreach (var entry in all)
        {
            if (Free(entry) != FreeOutcome.Abandoned)
                freed++;
        }
        // A location change may have let the game clear abandoned objects.
        PruneAbandoned();
        return freed;
    }

    public int Shutdown()
    {
        lock (gate)
            closed = true;
        var freed = DestroyAll();
        // The host ends here; its diagnostic records go with it (already logged).
        lock (gate)
        {
            abandoned.Clear();
        }
        return freed;
    }

    public void ClearUnsupported()
    {
        lock (gate)
        {
            reports.Clear();
            creationFailures.Clear();
            limitRefused.Clear();
            standingControlsFailed.Clear();
        }
    }

    public int RetainUnsupported(Func<RscObjectId, bool> isWanted)
    {
        ArgumentNullException.ThrowIfNull(isWanted);
        lock (gate)
        {
            foreach (var id in creationFailures.Keys.Where(id => !isWanted(id)).ToArray())
                creationFailures.Remove(id);
            limitRefused.RemoveWhere(id => !isWanted(id));
            return reports.RemoveAll(report => !isWanted(report.ObjectId));
        }
    }

    /// <summary>Whether every native call the actor backend uses resolved (read-only; game thread).</summary>
    public bool NativeCallsAvailable => IsAvailable();

    private bool IsAvailable()
    {
        try
        {
            return backend.IsAvailable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates one battle character through the game's automatic allocator
    /// (<c>CreateBattleCharacter()</c>, as Brio, Izzy and HDM do), treats its
    /// return solely as the COM index, retrieves that exact object with
    /// <c>GetObjectByIndex</c>, and proves before any other write that it is
    /// client-only and RSC's: the COM index round-trips through
    /// <c>GetIndexByObject</c>, its actual global index is in 200–448, the
    /// object table holds it there, it is not the local player, and its kind is
    /// BattleNpc or not set yet. On any failure only that exact object is
    /// deleted, after re-reading that the returned COM index still gives it.
    /// Every attempt is recorded (<see cref="CreationAttempts"/>). Returns
    /// null on failure.
    /// </summary>
    private Instance? CreateVerified(string tag)
    {
        var attempt = new CreationAttemptBuilder();
        try
        {
            // Read-only snapshot of every COM slot, immediately before creating.
            var before = backend.ComEntriesSnapshot();
            var returned = backend.CreateBattleCharacter();
            attempt.ReturnedComIndex = returned;
            if (!ActorSlotRange.IsComIndex(returned))
                return Fail(attempt, "the allocator returned no COM index in 0–248", 0, -1);
            // The slot must have been empty: an object already there is not a
            // new allocation and is never adopted or deleted.
            attempt.SlotWasEmpty = returned < before.Length && before[returned] == 0;
            if (attempt.SlotWasEmpty != true)
                return Fail(attempt, "the returned COM slot already held an object before the call", 0, -1);
            var address = backend.ComObjectByIndex(returned);
            attempt.ObjectReturned = address != 0;
            if (address == 0)
                return Fail(attempt, "GetObjectByIndex returned no object for that COM index", 0, -1);
            if (Array.IndexOf(before, address) >= 0)
                return Fail(attempt, "the returned object already existed in another COM slot", 0, -1);
            var roundTrip = backend.ComIndexOf(address);
            attempt.IndexRoundTrip = roundTrip;
            if (roundTrip != returned)
                return Fail(attempt, "GetIndexByObject does not give back the returned COM index", address, returned);
            attempt.RawEntryMatches = backend.ComEntry(returned) == address;
            if (attempt.RawEntryMatches != true)
                // Ambiguous: the two views of the slot disagree, so nothing is deleted.
                return Fail(attempt, "the raw COM entry does not hold the returned object", 0, -1);

            var facts = backend.ReadFacts(address);
            attempt.GlobalIndex = facts.GlobalIndex;
            attempt.Kind = ActorObjectKinds.Describe(facts.RawKind >= 0
                ? facts.RawKind
                : facts.IsBattleNpc ? ActorObjectKinds.BattleNpc : facts.KindUnset ? ActorObjectKinds.None : -1);
            // A policy refusal is deterministic: the allocator would hand back
            // the same object, so it is not retried (permanent).
            if (!ActorSlotRange.IsAcceptedGlobalIndex(facts.GlobalIndex))
                return Fail(attempt, "the actual global index is outside 200–448", address, returned, permanent: true);
            attempt.TableMatches = backend.ObjectTableEntry(facts.GlobalIndex) == address;
            if (attempt.TableMatches != true)
                return Fail(attempt, "the object table entry at that global index is another object", address, returned);
            attempt.IsLocalPlayer = address == backend.LocalPlayer;
            if (attempt.IsLocalPlayer == true)
                return Fail(attempt, "the object is the local player", address, returned);
            if (!ActorObjectKinds.IsApprovedFreshKind(facts))
                return Fail(attempt,
                    $"the fresh object's kind is {attempt.Kind}, not an approved pre-setup kind (BattleNpc, None or EventNpc)",
                    address, returned, permanent: true);

            RecordAttempt(attempt.Build(null, deleted: false));
            var createdKind = facts.RawKind >= 0
                ? facts.RawKind
                : facts.IsBattleNpc ? ActorObjectKinds.BattleNpc : ActorObjectKinds.None;
            return new Instance(address, facts.GlobalIndex, facts.GameObjectId, facts.EntityId, returned, tag, createdKind);
        }
        catch (Exception exception)
        {
            RecordAttempt(attempt.Build($"a native call failed ({exception.GetType().Name})", deleted: false));
            return null;
        }
    }

    private Instance? Fail(CreationAttemptBuilder attempt, string gate, nint address, int comIndex, bool permanent = false)
    {
        var deleted = address != 0 && TryDelete(comIndex, address);
        RecordAttempt(attempt.Build(gate, deleted) with { Permanent = permanent });
        return null;
    }

    private void RecordAttempt(ActorCreationAttempt attempt)
    {
        lock (gate)
        {
            creationSequence++;
            if (attemptHistory.Count == MaximumCreationAttempts)
                attemptHistory.RemoveAt(0);
            attemptHistory.Add(attempt with { Sequence = creationSequence });
        }
    }

    /// <summary>
    /// Deletes the exact object a failed creation retrieved, only after
    /// re-reading that the returned COM index still gives that object and the
    /// object still maps back to that index. Returns whether it was deleted.
    /// </summary>
    private bool TryDelete(int comIndex, nint address)
    {
        try
        {
            // Never the local player, whatever the allocator returned.
            if (!ActorSlotRange.IsComIndex(comIndex) || address == backend.LocalPlayer ||
                backend.ComObjectByIndex(comIndex) != address ||
                backend.ComIndexOf(address) != comIndex)
                return false;
            backend.DeleteComObject(comIndex);
            return true;
        }
        catch (Exception)
        {
            // Best effort: a failed delete leaves a hidden, unowned puppet the
            // game frees with the zone; nothing else is written.
            return false;
        }
    }

    private enum OwnershipCheck
    {
        /// <summary>Slot, address, table entry and object identity only (releasing a failed setup).</summary>
        Identity,
        /// <summary>Identity, and a battle NPC or no kind yet (before setup, same call as the creation).</summary>
        Created,
        /// <summary>Identity, a battle NPC, and this spawn's tag (every write and delete after setup).</summary>
        SetUp,
    }

    /// <summary>
    /// Re-reads, now, whether the live object is still exactly this spawn's.
    /// Every fact is read fresh from the game; nothing cached is trusted.
    /// </summary>
    private bool Verify(Owned entry, OwnershipCheck check)
    {
        var mismatch = ProofMismatch(entry, check);
        if (mismatch is null)
            return true;
        // Diagnostics only (no address): which part of the receipt no longer matched.
        entry.LastProofFailure = mismatch;
        return false;
    }

    /// <summary>
    /// Null when the live object is still exactly this spawn's; otherwise
    /// which part of the ownership receipt no longer matches (diagnostics, no
    /// address or name). Read-only.
    /// </summary>
    private string? ProofMismatch(Owned entry, OwnershipCheck check)
    {
        try
        {
            var instance = entry.Instance;
            // The global object table still holds this address at the recorded index.
            if (backend.ObjectTableEntry(instance.GlobalIndex) != instance.Address)
                return "the object table no longer holds it at its index";
            // The creation COM index still gives this address (GetObjectByIndex),
            // and the address still maps to that index (GetIndexByObject).
            if (backend.ComObjectByIndex(instance.CreatedComIndex) != instance.Address)
                return "its creation slot holds another object or none";
            if (backend.ComIndexOf(instance.Address) != instance.CreatedComIndex)
                return "the object maps to another creation slot";
            if (instance.Address == backend.LocalPlayer)
                return "it is the local player";
            var facts = backend.ReadFacts(instance.Address);
            if (facts.GlobalIndex != instance.GlobalIndex)
                return "its global index changed";
            if (facts.GameObjectId != instance.GameObjectId || facts.EntityId != instance.EntityId)
                return "its object or entity id changed";
            return check switch
            {
                OwnershipCheck.Identity => null,
                // Only in the creation call itself, before setup.
                OwnershipCheck.Created => ActorObjectKinds.IsApprovedFreshKind(facts) ? null : "its kind is not an approved fresh kind",
                _ => !facts.IsBattleNpc ? "it is no longer a battle NPC"
                    : !string.Equals(facts.Tag, instance.Tag, StringComparison.Ordinal) ? "its RSC tag no longer matches"
                    : null,
            };
        }
        catch (Exception exception)
        {
            return $"reading it failed ({exception.GetType().Name})";
        }
    }

    /// <summary>
    /// The full proof for <paramref name="check"/>, now. If it fails, the record
    /// is forgotten and reported as abandoned (<see cref="Owned.Lost"/>; the
    /// object is left untouched) and an <see cref="InvalidOperationException"/>
    /// stops the caller before any further native call.
    /// </summary>
    private void EnsureOwned(Owned entry, OwnershipCheck check)
    {
        if (Verify(entry, check))
            return;
        Forget(entry, ReplacedReason);
        throw new InvalidOperationException(LostMessage);
    }

    private const string LostMessage = "The actor no longer matches its recorded identity.";

    /// <summary>One native write, immediately preceded by the full proof (<see cref="EnsureOwned"/>).</summary>
    private void WriteOwned(Owned entry, OwnershipCheck check, Action<nint> write)
    {
        EnsureOwned(entry, check);
        write(entry.Instance.Address);
    }

    private ApplyPortResult Construct(Owned entry, ActorState actor)
    {
        var address = entry.Instance.Address;
        if (!entry.DrawEnabled)
        {
            if (!backend.IsReadyToDraw(address))
                return new(ApplyPortStatus.AwaitingPayload, "Waiting for the actor to be ready to draw.");
            WriteTransform(entry, actor);
            // Still invisible: alpha stays 0 until the body is the creature's.
            WriteOwned(entry, OwnershipCheck.SetUp, owner => backend.SetAlpha(owner, 0f));
            WriteOwned(entry, OwnershipCheck.SetUp, owner => backend.EnableDraw(owner));
            entry.DrawEnabled = true;
            return new(ApplyPortStatus.AwaitingPayload, "Waiting for the actor's model.");
        }
        if (!backend.HasDrawnModel(address, entry.Plan.ModelCharaId))
            return new(ApplyPortStatus.AwaitingPayload, "Waiting for the actor's model.");

        WriteTransform(entry, actor);
        // The standing loop, still invisible (an allowlisted loop only; no
        // freeze, no look-at, no one-shots). Owner or not, every client
        // applies it from the authoritative state before showing (owner-only
        // restricts editing, never drawing). The per-frame steps run in Tick.
        if (entry.StandingFailure is { } failure)
            FailStandingControls(entry, actor, failure);
        if (PrepareStandingControls(entry, actor) is { } waiting)
            return waiting;
        ApplyControls(entry, actor.Controls);
        // The loop must hold in a timeline slot on consecutive frames first.
        if (entry.NativeLoop is not null && !entry.LoopConfirmed)
            return new(ApplyPortStatus.AwaitingPayload, "Waiting for the actor's loop to take hold.");
        entry.ConstructionNote = DescribeConstruction(entry, actor.Controls);
        WriteOwned(entry, OwnershipCheck.SetUp, owner => backend.SetAlpha(owner, 1f));
        lock (gate)
        {
            entry.Shown = true;
            entry.Applied = actor;
            creationFailures.Remove(actor.ObjectId);
            reports.RemoveAll(report => report.ObjectId == actor.ObjectId);
        }
        return new(ApplyPortStatus.Succeeded);
    }

    /// <summary>
    /// Before a standing loop is applied to a new actor (observed in game:
    /// on a redraw after a reconnect or restart the demihuman's
    /// loop did not take, while the same loop applied live held): the
    /// creature's own animation must have been running on
    /// <see cref="ReadyStableFrames"/> consecutive frames
    /// (<see cref="IActorControlBackend.IsAnimationReady"/>, sampled by
    /// <see cref="Tick"/>), so the loop is played on a settled actor, as a live
    /// change is. Null when ready (or nothing to apply); otherwise the result
    /// to return. Past <see cref="StandingControlsDeadlineFrames"/> Tick
    /// records a failure and the actor fails closed.
    /// </summary>
    private ApplyPortResult? PrepareStandingControls(Owned entry, ActorState actor)
    {
        if (actor.Controls.IsDefault || controls is null || entry.StandingControlsReady)
            return null;
        if (!entry.AwaitingReady)
        {
            lock (gate)
                entry.ControlsWaitSince = frame;
            entry.AwaitingReady = true;
        }
        return new(ApplyPortStatus.AwaitingPayload, "Waiting for the actor's animation to start.");
    }

    /// <summary>
    /// One frame of drawing a new actor's standing loop, read-only unless a
    /// replay is due, every step behind the full proof (run by Tick):
    /// <list type="number">
    /// <item>while waiting: is the creature's own animation running? Ready
    /// after <see cref="ReadyStableFrames"/> consecutive frames; past
    /// <see cref="StandingControlsDeadlineFrames"/> the actor fails.</item>
    /// <item>after the play: is the row in a timeline slot? Held after
    /// <see cref="LoopHoldFrames"/> consecutive frames. Missing for
    /// <see cref="LoopReplayAfterFrames"/> frames: played again (at most
    /// <see cref="MaximumLoopReplays"/> times). Not held by
    /// <see cref="LoopConfirmDeadlineFrames"/>: the actor fails.</item>
    /// </list>
    /// A failure is recorded; the next apply releases the actor and lists it.
    /// </summary>
    private void StepConstruction(Owned entry, IActorControlBackend native, long now)
    {
        EnsureOwned(entry, OwnershipCheck.SetUp);
        var address = entry.Instance.Address;
        if (!entry.StandingControlsReady)
        {
            bool ready;
            try
            {
                ready = native.IsAnimationReady(address);
            }
            catch (Exception)
            {
                ready = false;
            }
            entry.ReadyRun = ready ? entry.ReadyRun + 1 : 0;
            entry.ControlsWaitFrames = now - (entry.ControlsWaitSince ?? now);
            if (entry.ReadyRun >= ReadyStableFrames)
                entry.StandingControlsReady = true;
            else if (entry.ControlsWaitFrames > StandingControlsDeadlineFrames)
                entry.StandingFailure = $"its animation was not running on {ReadyStableFrames} consecutive frames within " +
                    $"{entry.ControlsWaitFrames} frame(s) (ready on the last {entry.ReadyRun}; {DescribeAnimationState(entry, native)})";
            return;
        }
        if (entry.NativeLoop is not { } row || entry.LoopConfirmed)
            return;
        bool inSlot;
        try
        {
            inSlot = native.ReadActiveTimelines(address).Contains(row);
        }
        catch (Exception)
        {
            inSlot = false;
        }
        var since = now - entry.LoopStartedAt;
        if (inSlot)
        {
            entry.LoopHoldRun++;
            entry.LoopMissingRun = 0;
            entry.LoopInSlotFrames++;
            entry.LoopLongestRun = Math.Max(entry.LoopLongestRun, entry.LoopHoldRun);
            if (entry.LoopHoldRun >= LoopHoldFrames)
            {
                entry.LoopConfirmed = true;
                entry.LoopHeldAfterFrames = since;
                entry.LoopSlotEvidence = DescribeLoopSlot(entry, native, row);
            }
            return;
        }
        entry.LoopHoldRun = 0;
        entry.LoopMissingRun++;
        if (since > LoopConfirmDeadlineFrames)
        {
            entry.StandingFailure = $"its loop (row {row}) did not hold in a timeline slot within {since} frame(s): " +
                $"in a slot on {entry.LoopInSlotFrames} frame(s), longest run {entry.LoopLongestRun} (needs {LoopHoldFrames}), " +
                $"{entry.LoopReplays} replay(s); {DescribeLoopSlot(entry, native, row)}";
            return;
        }
        if (entry.LoopMissingRun >= LoopReplayAfterFrames && entry.LoopReplays < MaximumLoopReplays)
        {
            entry.LoopMissingRun = 0;
            ReplayLoop(entry, native, row);
        }
    }

    /// <summary>Tick works on an actor still being drawn (waiting for its animation, or for its loop to hold).</summary>
    private static bool ConstructionDue(Owned entry) =>
        !entry.Shown && entry.StandingFailure is null &&
        ((entry.AwaitingReady && !entry.StandingControlsReady) ||
         (entry.StandingControlsReady && entry.NativeLoop is not null && !entry.LoopConfirmed));

    /// <summary>Read-only behind the proof (diagnostics, no identity): the rows in the slots and the body's live controls.</summary>
    private string DescribeAnimationState(Owned entry, IActorControlBackend native)
    {
        EnsureOwned(entry, OwnershipCheck.SetUp);
        string rows;
        try
        {
            rows = "rows in slots [" + string.Join(", ", native.ReadActiveTimelines(entry.Instance.Address).Where(id => id != 0)) + "]";
        }
        catch (Exception)
        {
            rows = "slots unreadable";
        }
        string body;
        try
        {
            var sample = native.ReadAnimationSample(entry.Instance.Address);
            body = sample.Readable ? $"body: {sample.LiveControls} live of {sample.Controls.Count} control(s)" : "body unreadable";
        }
        catch (Exception)
        {
            body = "body unreadable";
        }
        return rows + "; " + body;
    }

    /// <summary>
    /// Read-only behind the proof (diagnostics): the rows in the slots, then the
    /// loop's own slot: scheduler timeline, key, timestamp and whether its
    /// resource data is present (slot-level evidence only, never proof of the
    /// visible pose), and the body's live controls.
    /// </summary>
    private string DescribeLoopSlot(Owned entry, IActorControlBackend native, ushort row)
    {
        var state = DescribeAnimationState(entry, native);
        try
        {
            var slot = native.ReadTimelineSlots(entry.Instance.Address).FirstOrDefault(item => item.TimelineId == row);
            return state + "; " + (slot is null ? $"row {row} in no slot" : slot.Describe());
        }
        catch (Exception)
        {
            return state + "; slot state unreadable";
        }
    }

    private static bool LoopCheckDue(Owned entry, long now) =>
        entry.Shown && entry.NativeLoop is not null &&
        now - entry.LastLoopCheckFrame >= LoopCheckIntervalFrames;

    /// <summary>Read-only behind the proof: the row is in one of the actor's timeline slots.</summary>
    private bool LoopInSlot(Owned entry, IActorControlBackend native, ushort row)
    {
        EnsureOwned(entry, OwnershipCheck.SetUp);
        try
        {
            return native.ReadActiveTimelines(entry.Instance.Address).Contains(row);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Starts the same allowlisted loop again (lock, override, play), each step behind the proof.</summary>
    private void ReplayLoop(Owned entry, IActorControlBackend native, ushort row)
    {
        entry.LoopReplays++;
        WriteOwned(entry, OwnershipCheck.SetUp, address => native.SetAnimationLock(address, true));
        WriteOwned(entry, OwnershipCheck.SetUp, address => native.SetBaseOverride(address, row));
        WriteOwned(entry, OwnershipCheck.SetUp, address => native.PlayTimeline(address, row));
    }

    /// <summary>
    /// Fail closed: release this exact instance first (hidden, undrawn and
    /// deleted, each behind the proof), count the failure for these exact
    /// controls on this object, and only then list it; then fault the apply
    /// (the receiver's release finds nothing left). The first failure is drawn
    /// once more from a fresh spawn; after <see cref="StandingControlsAttempts"/>
    /// the respawn is refused with the reason until the controls or the
    /// location change.
    /// </summary>
    private void FailStandingControls(Owned entry, ActorState actor, string detail)
    {
        var release = ReleaseForControlFailure(entry);
        ReportControlFailure(actor.ObjectId, actor.BNpcBaseId, actor.Controls, wasShown: false, detail, release);
        entry.ConstructionNote = $"not drawn: {detail}";
        throw new InvalidOperationException(StandingControlsFailedReason + ": " + detail);
    }

    /// <summary>
    /// After the release: record the failure (every build) and list the actor
    /// by what the release achieved. Released completely: listed with the
    /// control error ("trying once more" until the attempts are used). The
    /// proof failed: it stays listed as left untouched (RSC never touches an
    /// object it cannot prove is its own). Otherwise: listed as not confirmed
    /// hidden and released.
    /// </summary>
    private void ReportControlFailure(RscObjectId objectId, uint bnpcBaseId, ActorControlState desired, bool wasShown,
        string detail, ReleaseRecord release)
    {
        int failures;
        lock (gate)
        {
            failures = standingControlsFailed.TryGetValue((objectId, desired), out var count) ? count + 1 : 1;
            standingControlsFailed[(objectId, desired)] = failures;
            controlFailureSequence++;
            if (controlFailures.Count == MaximumControlFailures)
                controlFailures.RemoveAt(0);
            controlFailures.Add(new ActorControlFailure(controlFailureSequence, objectId, bnpcBaseId, desired, wasShown, detail)
            {
                Release = release.Describe(),
                Released = release.Complete,
                Attempt = failures,
            });
        }
        if (release.Outcome == FreeOutcome.Abandoned)
            return;
        if (!release.Complete)
            Report(objectId, IdentityOf(bnpcBaseId), LocalObjectReportKind.Failed, StandingControlsReleaseIncompleteReason);
        else if (failures < StandingControlsAttempts)
            Report(objectId, IdentityOf(bnpcBaseId), LocalObjectReportKind.Retrying, StandingControlsRetryingReason);
        else
            Report(objectId, IdentityOf(bnpcBaseId), LocalObjectReportKind.NotSupported, StandingControlsFailedReason);
    }

    private static string DescribeConstruction(Owned entry, ActorControlState controls) =>
        controls.IsDefault
            ? "drawn with default controls"
            : $"standing loop applied while drawing: the creature's animation ran on {ReadyStableFrames} consecutive frames " +
              $"(after {entry.ControlsWaitFrames} frame(s))" +
              (entry.NativeLoop is { } row
                  ? $"; loop {row} held in a slot on {LoopHoldFrames} consecutive frames after {entry.LoopHeldAfterFrames ?? 0} frame(s)" +
                    (entry.LoopReplays > 0 ? $" and {entry.LoopReplays} replay(s)" : "") +
                    (entry.LoopSlotEvidence is { } evidence ? $" ({evidence})" : "")
                  : "");

    private void WriteTransform(Owned entry, ActorState actor) =>
        WriteTransform(entry, PositionOf(actor), actor.Yaw, actor.Scale);

    /// <summary>Apply-on-change: native writes only when position, facing or scale changed.</summary>
    private void WriteTransform(Owned entry, Vector3 position, float yaw, float scale)
    {
        (Vector3 Position, float Yaw, float Scale) transform = (position, yaw, scale * entry.Plan.RowScale);
        if (entry.Written is { } written && written == transform)
            return;
        // Only after setup (every caller): the proof includes the tag.
        WriteOwned(entry, OwnershipCheck.SetUp,
            address => backend.SetTransform(address, transform.Position, transform.Yaw, transform.Scale));
        entry.Written = transform;
    }

    #region Controls

    /// <summary>
    /// Why this client cannot apply the actor's requested controls, or null.
    /// Default controls always draw. A path counts only when its native members
    /// resolved and its in-game check passed (<see cref="ActorControlVerification"/>).
    /// </summary>
    private string? UnavailableControlReason(ActorState actor)
    {
        var requested = actor.Controls;
        // The reserved fields: a look-at value
        // or a freeze is refused first, whatever else the actor carries,
        // before anything is touched or drawn.
        if (requested.LookAt is not null)
            return LookAtNotSupportedReason;
        if (requested.Frozen)
            return FreezeNotSupportedReason;
        if (requested.IsDefault)
            return null;
        if (controls is null)
            return ControlsUnavailableReason;
        var availability = ControlAvailability();
        if (requested.TimelineId is { } timeline)
        {
            if (availability.AnimationUnavailable is { } animation)
                return animation;
            if (Timeline(timeline) is not { } data)
                return UnknownAnimationReason;
            // Production proof is the curated allowlist only: this game version, this creature and row, and the
            // verified loop/one-shot behaviour.
            if (!allowlist.MatchesGameVersion(gameVersion))
                return AnimationNotVerifiedForVersionReason;
            var allowed = allowlist.Find(actor.BNpcBaseId, timeline);
            // Only verified loops ship: a one-shot is never played (no
            // animation, no attached sound), allowlisted or not.
            if (!data.IsLoop)
                return allowed is null ? OneShotUnavailableReason : AnimationDataChangedReason;
            if (allowed is null)
                return AnimationNotAllowedReason;
            if (allowed.IsLoop != data.IsLoop)
                return AnimationDataChangedReason;
        }
        lock (gate)
        {
            if (standingControlsFailed.TryGetValue((actor.ObjectId, requested), out var failures) &&
                failures >= StandingControlsAttempts)
                return StandingControlsFailedReason;
        }
        return null;
    }

    /// <summary>
    /// The animations this client may offer for a creature: the allowlist's
    /// rows for it, only when the animation path is available, this game
    /// version is the allowlist's, and each row's game data still has the
    /// verified loop/one-shot behaviour. Empty otherwise (the UI shows the reason).
    /// </summary>
    public IReadOnlyList<ActorAnimationAllowance> AllowedAnimations(uint bnpcBaseId)
    {
        if (ControlAvailability().AnimationUnavailable is not null || !allowlist.MatchesGameVersion(gameVersion))
            return [];
        return allowlist.ForCreature(bnpcBaseId)
            .Where(allowed => Timeline(allowed.TimelineId) is { } data && data.IsLoop == allowed.IsLoop)
            .ToArray();
    }

    /// <summary>Why this client offers no animation at all, or null when it does (UI text).</summary>
    public string? AnimationUnavailableReasonForUi() =>
        ControlAvailability().AnimationUnavailable ??
        (allowlist.MatchesGameVersion(gameVersion) ? null : AnimationNotVerifiedForVersionReason);

    /// <summary>
    /// What the player-facing controls panel offers for one creature, from
    /// the same verified availability synchronized actors use: the allowlisted loops for this creature, only on the
    /// allowlist's game version. There is no freeze and no look-at.
    /// </summary>
    public ActorControlOffer ControlOfferFor(uint bnpcBaseId) =>
        new(AnimationUnavailableReasonForUi(), AllowedAnimations(bnpcBaseId).Where(allowed => allowed.IsLoop).ToArray());

    /// <summary>
    /// What this client can apply for synchronized actors: resolved by the
    /// backend and verified in game. Unverified paths fail closed.
    /// </summary>
    public ActorControlAvailability ControlAvailability()
    {
        if (controls is null)
            return ActorControlAvailability.None(ControlsUnavailableReason);
        var resolved = ResolvedControls();
        return new(resolved.AnimationUnavailable ?? (verification.Animation ? null : AnimationUnavailableReason));
    }

    /// <summary>What the backend resolved, before the in-game verification gate.</summary>
    private ActorControlAvailability ResolvedControls()
    {
        if (controls is null)
            return ActorControlAvailability.None(ControlsUnavailableReason);
        try
        {
            return controls.Availability;
        }
        catch (Exception exception)
        {
            // Diagnostics only (Read control facts): which failure it was.
            controlsError = exception.GetType().Name;
            return ActorControlAvailability.None(ControlsUnavailableReason);
        }
    }

    private volatile string? controlsError;

    /// <summary>The row's ActionTimeline key from this client's game data, or null.</summary>
    public string? TimelineKey(ushort timelineId) => Timeline(timelineId)?.Key;

    private ActorTimelineRow? Timeline(ushort timelineId)
    {
        try
        {
            return timelines is not null && timelines.TryGetTimeline(timelineId, out var row) && row.Id == timelineId ? row : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies only what changed between the controls in effect and
    /// <paramref name="desired"/>, each write behind the full ownership proof.
    /// Only a looping row is ever played (standing state, applied on every
    /// draw); a one-shot is never played (one-shots are not supported;
    /// synchronized ones are refused before drawing). The reserved <c>AnimationSequence</c>
    /// is ignored. RSC has no freeze and no look-at: an actor carrying either
    /// reserved field is refused before it gets here.
    /// </summary>
    private void ApplyControls(Owned entry, ActorControlState desired)
    {
        if (controls is not { } native)
        {
            // Without a backend only default controls get here (others are refused).
            entry.Controls = desired;
            return;
        }
        ushort? loop = desired.TimelineId is { } id && Timeline(id) is { IsLoop: true } ? id : null;
        // A clear: the timeline was cleared, or the loop in effect is replaced.
        var clear = entry.AnimationTouched &&
            (desired.TimelineId is null || (entry.NativeLoop is not null && entry.NativeLoop != loop));
        // Every native write or call below is its own step, each immediately
        // preceded by the full ownership proof (WriteOwned).
        if (clear)
        {
            entry.NativeLoop = null;
            entry.AnimationTouched = false;
            foreach (var step in ClearAnimationSteps(native))
                WriteOwned(entry, OwnershipCheck.SetUp, step);
        }
        // A loop starts when the row changes; the reserved sequence never
        // restarts it (there is no one-shot or replay behaviour in production).
        if (loop is { } looping && entry.NativeLoop != looping)
        {
            entry.NativeLoop = looping;
            entry.AnimationTouched = true;
            StartAnimation(entry, looping, native);
        }
        entry.Controls = desired;
    }

    /// <summary>
    /// Plays an allowlisted row (anything else is refused before drawing):
    /// animation lock, then the base override, then play. A new loop must
    /// hold in its slot again before a new actor is shown.
    /// </summary>
    private void StartAnimation(Owned entry, ushort row, IActorControlBackend native)
    {
        lock (gate)
            entry.LoopStartedAt = frame;
        entry.LoopConfirmed = false;
        entry.LoopHoldRun = 0;
        entry.LoopMissingRun = 0;
        entry.LoopInSlotFrames = 0;
        entry.LoopLongestRun = 0;
        WriteOwned(entry, OwnershipCheck.SetUp, address => native.SetAnimationLock(address, true));
        WriteOwned(entry, OwnershipCheck.SetUp, address => native.SetBaseOverride(address, row));
        WriteOwned(entry, OwnershipCheck.SetUp, address => native.PlayTimeline(address, row));
    }

    /// <summary>A clear: base override 0, then normal mode (no idle timeline is assumed).</summary>
    private static IEnumerable<Action<nint>> ClearAnimationSteps(IActorControlBackend native)
    {
        yield return address => native.SetBaseOverride(address, 0);
        yield return address => native.SetAnimationLock(address, false);
    }

    /// <summary>
    /// Clears RSC's per-actor control state (so no per-frame step touches the
    /// actor again) and returns the game-side resets to run, in order: base
    /// override 0, then normal mode. Under the lock.
    /// </summary>
    private List<Action> TakeControlResets(Owned entry)
    {
        var resets = new List<Action>();
        var address = entry.Instance.Address;
        if (controls is { } native && (entry.AnimationTouched || entry.NativeLoop is not null))
        {
            // One native write or call per step; Free proves ownership before each.
            resets.AddRange(ClearAnimationSteps(native).Select(step => (Action)(() => step(address))));
        }
        entry.NativeLoop = null;
        entry.AnimationTouched = false;
        entry.AwaitingReady = false;
        return resets;
    }

    /// <summary>
    /// Game thread, once per frame: drawing steps for actors still
    /// hidden (their animation running, then their loop holding in a slot),
    /// and the loop check of drawn actors (played again if the game dropped
    /// it). Every read and write is preceded by the full ownership proof; a
    /// lost proof abandons the record untouched.
    /// </summary>
    public void Tick()
    {
        if (controls is not { } native)
            return;
        Owned[] work;
        long now;
        lock (gate)
        {
            if (closed)
                return;
            now = ++frame;
            work = owned.Values
                .Where(entry => LoopCheckDue(entry, now) || ConstructionDue(entry))
                .ToArray();
        }
        var retired = false;
        foreach (var entry in work)
        {
            try
            {
                if (ConstructionDue(entry))
                    StepConstruction(entry, native, now);
                // A drawn actor's loop must stay in a slot; if the game dropped
                // it, play it again (same verified calls, each behind the proof).
                if (LoopCheckDue(entry, now) && entry.NativeLoop is { } looping)
                {
                    entry.LastLoopCheckFrame = now;
                    if (!LoopInSlot(entry, native, looping))
                        ReplayLoop(entry, native, looping);
                }
            }
            catch (InvalidOperationException) when (entry.Lost)
            {
                // Abandoned untouched; the receiver releases its receipt.
                retired = true;
            }
            catch (Exception)
            {
                RetireFaulted(entry);
                retired = true;
            }
        }
        if (retired)
            InstanceRetired?.Invoke();
    }

    /// <summary>A native control call failed: release this exact instance (the receiver respawns it).</summary>
    private void RetireFaulted(Owned entry)
    {
        lock (gate)
        {
            if (owned.TryGetValue(entry.Handle.OwnershipId, out var current) && ReferenceEquals(current, entry))
                owned.Remove(entry.Handle.OwnershipId);
            EndPreviewForUnsafe(entry, FurniturePreviewStatus.Faulted);
        }
        Free(entry);
    }

    #endregion

    /// <summary>
    /// Frees one owned actor: re-verify in full; clear RSC's per-actor control
    /// state first; then alpha 0, stop drawing, and delete through the
    /// creation slot. The full proof is re-run immediately before each of
    /// those steps (and the slot re-read once more right before the delete).
    /// The moment the proof fails, nothing further is written or deleted: the
    /// record is forgotten and reported as abandoned. A step that merely
    /// throws while the proof still holds does not stop the next one (best
    /// effort). Never throws.
    /// </summary>
    private FreeOutcome Free(Owned entry) =>
        Free(entry, entry.SetUp ? OwnershipCheck.SetUp : OwnershipCheck.Created);

    private enum FreeOutcome
    {
        Freed,
        FreedWithErrors,
        /// <summary>
        /// The proof failed before a step: nothing further was written and
        /// nothing deleted (any earlier step reached only the proven object).
        /// </summary>
        Abandoned,
    }

    private FreeOutcome Free(Owned entry, OwnershipCheck check) => Free(entry, check, null);

    private FreeOutcome Free(Owned entry, OwnershipCheck check, ReleaseRecord? record)
    {
        var outcome = FreeSteps(entry, check, record);
        if (record is not null)
            record.Outcome = outcome;
        return outcome;
    }

    private FreeOutcome FreeSteps(Owned entry, OwnershipCheck check, ReleaseRecord? record)
    {
        var instance = entry.Instance;
        var address = instance.Address;
        FreeOutcome AbandonAt(string step)
        {
            if (record is not null)
            {
                record.AbandonedBefore = step;
                record.ProofFailure = entry.LastProofFailure;
            }
            return Abandon(entry);
        }
        // The proof immediately before the first step (alpha 0).
        if (!Verify(entry, check))
            return AbandonAt("any step");
        // RSC's per-actor control state goes first, so no per-frame write
        // (Tick) can touch the actor again; then the game-side resets.
        List<Action> resets;
        lock (gate)
        {
            controlled.Remove(address);
            resets = TakeControlResets(entry);
        }
        var reset = true;
        foreach (var step in resets)
        {
            if (!Verify(entry, check))
                return AbandonAt("the control reset");
            reset &= TryStep(step);
        }
        if (resets.Count > 0 && !Verify(entry, check))
            return AbandonAt("hiding");
        var hidden = TryStep(() => backend.SetAlpha(address, 0f));
        if (record is not null)
            (record.Reset, record.Hidden) = (reset, hidden);
        if (!Verify(entry, check))
            return AbandonAt("undrawing");
        var undrawn = TryStep(() => backend.DisableDraw(address));
        if (record is not null)
            record.Undrawn = undrawn;
        if (!Verify(entry, check))
            return AbandonAt("deleting");
        var deleted = TryStep(() =>
        {
            if (backend.ComObjectByIndex(instance.CreatedComIndex) != address ||
                backend.ComIndexOf(address) != instance.CreatedComIndex)
                throw new InvalidOperationException("The actor's COM entry no longer matches.");
            backend.DeleteComObject(instance.CreatedComIndex);
        });
        if (record is not null)
            record.Deleted = deleted;
        return reset && hidden && undrawn && deleted ? FreeOutcome.Freed : FreeOutcome.FreedWithErrors;
    }

    /// <summary>
    /// What a release for a control failure did (diagnostics, no address):
    /// whether the object still matched the ownership receipt, and whether
    /// the control reset, hide, undraw and delete each completed.
    /// </summary>
    private sealed class ReleaseRecord
    {
        internal string? ReceiptMismatch { get; set; }
        internal bool Reset { get; set; }
        internal bool Hidden { get; set; }
        internal bool Undrawn { get; set; }
        internal bool Deleted { get; set; }
        internal string? AbandonedBefore { get; set; }
        internal string? ProofFailure { get; set; }
        internal FreeOutcome Outcome { get; set; }

        /// <summary>Hidden, undrawn and deleted, with the proof holding throughout.</summary>
        internal bool Complete => Outcome == FreeOutcome.Freed;

        internal string Describe() =>
            $"receipt matched {(ReceiptMismatch is null ? "yes" : $"no ({ReceiptMismatch})")}; " +
            (AbandonedBefore is { } step
                ? $"proof failed before {step} ({ProofFailure ?? "unknown"}): RSC left that object untouched (not hidden, undrawn or deleted by RSC)"
                : $"control reset {Text(Reset)}, hidden {Text(Hidden)}, undrawn {Text(Undrawn)}, deleted {Text(Deleted)}");

        private static string Text(bool value) => value ? "yes" : "no";
    }

    /// <summary>
    /// Before an actor is listed as not drawn for a control failure: release
    /// this exact instance now (the receiver's later release finds nothing),
    /// each step behind the full proof, and record what completed.
    /// </summary>
    private ReleaseRecord ReleaseForControlFailure(Owned entry)
    {
        var check = entry.SetUp ? OwnershipCheck.SetUp : OwnershipCheck.Created;
        var record = new ReleaseRecord { ReceiptMismatch = ProofMismatch(entry, check) };
        lock (gate)
        {
            if (owned.TryGetValue(entry.Handle.OwnershipId, out var current) && ReferenceEquals(current, entry))
                owned.Remove(entry.Handle.OwnershipId);
            EndPreviewForUnsafe(entry, FurniturePreviewStatus.Faulted);
        }
        Free(entry, check, record);
        return record;
    }

    private FreeOutcome Abandon(Owned entry)
    {
        Forget(entry, ReplacedReason);
        return FreeOutcome.Abandoned;
    }

    /// <summary>Drops the record without touching the object; <paramref name="reason"/> is reported when set.</summary>
    private void Forget(Owned entry, string? reason)
    {
        lock (gate)
        {
            controlled.Remove(entry.Instance.Address);
            if (owned.TryGetValue(entry.Handle.OwnershipId, out var current) && ReferenceEquals(current, entry))
                owned.Remove(entry.Handle.OwnershipId);
            EndPreviewForUnsafe(entry, FurniturePreviewStatus.Faulted);
            // At most one abandoned record per spawn, however many steps notice.
            if (reason is not null && !entry.Lost)
            {
                entry.Lost = true;
                ReportUnsafe(entry.Handle.ObjectId, IdentityOf(entry.Plan.BNpcBaseId), LocalObjectReportKind.Failed, reason);
                if (abandoned.Count == MaximumAbandoned)
                {
                    abandoned.RemoveAt(0);
                    abandonedDropped++;
                }
                abandonedTotal++;
                abandoned.Add((new(abandonedTotal, entry.Handle.ObjectId, entry.Instance.GlobalIndex,
                    entry.Instance.CreatedComIndex, reason, entry.LastProofFailure), entry.Instance.Address));
            }
        }
    }

    private static bool TryStep(Action step)
    {
        try
        {
            step();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private ApplyPortResult<LocalObjectInstanceHandle> Refuse(ActorState actor, string reason)
    {
        Report(actor.ObjectId, IdentityOf(actor.BNpcBaseId), LocalObjectReportKind.NotSupported, reason);
        return new(ApplyPortStatus.PermanentFailure, Error: reason);
    }

    private void RecordCreationFailure(ActorState actor, LocalObjectApplyToken token, string? reason = null)
    {
        int failures;
        lock (gate)
        {
            failures = creationFailures.TryGetValue(actor.ObjectId, out var previous) && previous.Token == token
                ? previous.Failures + 1
                : 1;
            creationFailures[actor.ObjectId] = (token, failures);
        }
        // A creation check (no reason) uses the creation texts; a setup or
        // replacement reason keeps its own text, with the attempt state added.
        if (failures >= creationAttempts)
            Report(actor.ObjectId, IdentityOf(actor.BNpcBaseId), LocalObjectReportKind.Failed,
                reason is null ? CreationExhaustedText : reason + "; retries exhausted.");
        else
            Report(actor.ObjectId, IdentityOf(actor.BNpcBaseId), LocalObjectReportKind.Retrying,
                reason is null ? CreationRetryingText : reason + " (retrying).");
    }

    private void Report(RscObjectId objectId, string identity, LocalObjectReportKind kind, string reason)
    {
        lock (gate)
            ReportUnsafe(objectId, identity, kind, reason);
    }

    private void ReportUnsafe(RscObjectId objectId, string identity, LocalObjectReportKind kind, string reason)
    {
        // A later report never hides that an earlier object for this actor
        // was left untouched and may still be visible.
        if ((reason == StandingControlsFailedReason || reason == StandingControlsRetryingReason ||
             reason == StandingControlsReleaseIncompleteReason) &&
            abandoned.Any(item => item.Record.ObjectId == objectId))
            reason = $"{reason}; {UntouchedEarlierObjectNote}";
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
        reports.Add(new FurnitureUnsupportedReport(objectId, identity, "Actor", reason, count, kind));
    }

    internal static Vector3 PositionOf(ActorState actor) => new(actor.Position.X, actor.Position.Y, actor.Position.Z);

    /// <summary>The facing as a rotation around the vertical axis (the gizmo's form).</summary>
    public static Quaternion RotationOf(ActorState actor) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, actor.Yaw);

    /// <summary>The yaw of a rotation around the vertical axis, normalized to (−π, π].</summary>
    public static float YawOf(Quaternion rotation)
    {
        var q = rotation;
        return ActorLimits.NormalizeYaw(MathF.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.Y * q.Y + q.X * q.X)));
    }

    private sealed record Instance(nint Address, int GlobalIndex, ulong GameObjectId, uint EntityId, int CreatedComIndex, string Tag,
        int CreatedRawKind = ActorObjectKinds.BattleNpc);

    private sealed class Owned(LocalObjectInstanceHandle handle, Instance instance, ActorDrawPlan plan)
    {
        internal LocalObjectInstanceHandle Handle { get; } = handle;
        internal Instance Instance { get; } = instance;
        internal ActorDrawPlan Plan { get; } = plan;
        /// <summary>Setup finished and was verified: every later check requires a battle NPC with the tag.</summary>
        internal bool SetUp { get; set; }
        /// <summary>The proof failed: retired as abandoned (reported once); nothing more is written or deleted.</summary>
        internal bool Lost { get; set; }
        internal bool DrawEnabled { get; set; }
        internal bool Shown { get; set; }
        /// <summary>The controls last applied.</summary>
        internal ActorControlState Controls { get; set; } = ActorControlState.Default;
        /// <summary>The looping row in effect natively, if any.</summary>
        internal ushort? NativeLoop { get; set; }
        /// <summary>Any animation was played (a clear is then needed).</summary>
        internal bool AnimationTouched { get; set; }
        /// <summary>Construction: waiting (sampled by Tick) for the creature's own animation to run.</summary>
        internal bool AwaitingReady { get; set; }
        /// <summary>Construction: the creature's animation ran on enough consecutive frames before the loop was played.</summary>
        internal bool StandingControlsReady { get; set; }
        internal long? ControlsWaitSince { get; set; }
        internal long ControlsWaitFrames { get; set; }
        internal int ReadyRun { get; set; }
        /// <summary>Construction: the frame the loop was played, the consecutive frames it was in / missing from a slot, and totals.</summary>
        internal long LoopStartedAt { get; set; }
        internal int LoopHoldRun { get; set; }
        internal int LoopMissingRun { get; set; }
        internal int LoopInSlotFrames { get; set; }
        internal int LoopLongestRun { get; set; }
        internal long? LoopHeldAfterFrames { get; set; }
        internal string? LoopSlotEvidence { get; set; }
        /// <summary>The loop held in its slot on enough consecutive frames (a new actor may be shown).</summary>
        internal bool LoopConfirmed { get; set; }
        /// <summary>Construction failed (recorded by Tick); the next apply releases the actor and lists it.</summary>
        internal string? StandingFailure { get; set; }
        /// <summary>Times the loop was played again because it was not in a slot (construction or later).</summary>
        internal int LoopReplays { get; set; }
        internal long LastLoopCheckFrame { get; set; }
        /// <summary>How the actor's controls were applied while drawing, or why it was not drawn (diagnostics).</summary>
        internal string? ConstructionNote { get; set; }
        /// <summary>Which part of the ownership receipt last failed to match (diagnostics).</summary>
        internal string? LastProofFailure { get; set; }
        /// <summary>The native transform last written (preview or authoritative).</summary>
        internal (Vector3 Position, float Yaw, float Scale)? Written { get; set; }
        /// <summary>The last authoritative state fully applied (drawn).</summary>
        internal ActorState? Applied { get; set; }
    }

    /// <summary>
    /// Development builds only: false unless a Debug build (RSC_DEVELOPMENT)
    /// runs as a dev plugin. Never in a Release build.
    /// </summary>
    public bool DevelopmentToolsEnabled { get; set; }

    /// <summary>Each control path: resolved or not, and verified in game or not (log text).</summary>
    public string DescribeControlPaths()
    {
        var resolved = ResolvedControls();
        return $"freeze removed (reserved field); animation {Path(resolved.AnimationUnavailable, verification.Animation)}" +
            $"; game version {gameVersion ?? "unknown"} (allowlist {allowlist.GameVersion}: " +
            $"{(allowlist.MatchesGameVersion(gameVersion) ? "match" : "no match")})" +
            (controlsError is { } error ? $"; control backend failed ({error})" : "");

        static string Path(string? unresolved, bool verified) =>
            unresolved is not null ? $"unavailable ({unresolved})"
            : verified ? "available (verified)"
            : "resolved, not verified in game (refused for synchronized actors)";
    }

    #region Preview (gizmo Move and Rotate, yaw only)

    public bool IsDrawn(RscObjectId objectId)
    {
        lock (gate)
            return !closed && FindDrawnUnsafe(objectId) is not null;
    }

    public FurniturePreviewBegin BeginPreview(AuthoritativeObjectState authoritative)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        if (authoritative is not ActorState actor)
            return new(FurniturePreviewBeginStatus.NotDrawn, Guid.Empty);
        lock (gate)
        {
            if (closed)
                return new(FurniturePreviewBeginStatus.Unavailable, Guid.Empty);
            if (preview is not null)
                return new(FurniturePreviewBeginStatus.Busy, Guid.Empty);
            var entry = FindDrawnUnsafe(actor.ObjectId);
            if (entry is null)
                return new(FurniturePreviewBeginStatus.NotDrawn, Guid.Empty);
            if (entry.Applied != actor)
                return new(FurniturePreviewBeginStatus.Stale, Guid.Empty);
            var id = Guid.NewGuid();
            preview = new Preview(id, entry.Handle, actor) { Position = PositionOf(actor), Yaw = actor.Yaw };
            lastPreview = (id, FurniturePreviewStatus.Active);
            return new(FurniturePreviewBeginStatus.Started, id);
        }
    }

    public FurniturePreviewStatus PreviewStatus(Guid id)
    {
        lock (gate)
            return lastPreview.Id == id && id != Guid.Empty ? lastPreview.Status : FurniturePreviewStatus.Unknown;
    }

    /// <summary>Moves or turns (yaw only) the previewed actor, and only it.</summary>
    public bool MovePreview(Guid id, Vector3 position, Quaternion rotation)
    {
        if (!FurnitureTransforms.IsValidPosition(position) || !FurnitureTransforms.TryNormalize(rotation, out var normalized))
            return false;
        var yaw = YawOf(normalized);
        bool retired;
        lock (gate)
        {
            if (preview is not { Phase: FurniturePreviewStatus.Active } active || active.Id != id ||
                !owned.TryGetValue(active.Receipt.OwnershipId, out var entry) || entry.Handle != active.Receipt)
                return false;
            if (TryWriteUnsafe(entry, position, yaw, active.Basis.Scale))
            {
                active.Position = position;
                active.Yaw = yaw;
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
                var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, active.Yaw);
                if (Vector3.Distance(basisPosition, active.Position) < FurnitureTransforms.PositionEpsilon &&
                    FurnitureTransforms.AngleBetween(RotationOf(active.Basis), rotation) < FurnitureTransforms.AngleEpsilon)
                {
                    if (RestoreUnsafe(active))
                        FinishPreviewUnsafe(active, FurniturePreviewStatus.Finished);
                    else
                        retired = true;
                    return new(false, active.Basis, basisPosition, RotationOf(active.Basis));
                }
                active.Phase = FurniturePreviewStatus.Committing;
                lastPreview = (id, FurniturePreviewStatus.Committing);
                return new(true, active.Basis, active.Position, rotation);
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
        return TryWriteUnsafe(entry, PositionOf(latest), latest.Yaw, latest.Scale);
    }

    /// <summary>One native write during a preview; a mismatch or fault retires the exact instance at once.</summary>
    private bool TryWriteUnsafe(Owned entry, Vector3 position, float yaw, float scale)
    {
        try
        {
            EnsureOwned(entry, OwnershipCheck.SetUp);
            WriteTransform(entry, position, yaw, scale);
            return true;
        }
        catch (InvalidOperationException) when (entry.Lost)
        {
            // Forgotten and reported as abandoned (its preview ended as faulted); never freed.
            return false;
        }
        catch (Exception)
        {
            owned.Remove(entry.Handle.OwnershipId);
            if (preview is { } active && active.Receipt == entry.Handle)
                FinishPreviewUnsafe(active, FurniturePreviewStatus.Faulted);
            Free(entry);
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
            if (entry.Handle.ObjectId == objectId && entry.Shown && entry.Applied is not null)
                return entry;
        }
        return null;
    }

    private sealed class Preview(Guid id, LocalObjectInstanceHandle receipt, ActorState basis)
    {
        internal Guid Id { get; } = id;
        internal LocalObjectInstanceHandle Receipt { get; } = receipt;
        internal ActorState Basis { get; } = basis;
        internal Vector3 Position { get; set; }
        internal float Yaw { get; set; }
        internal FurniturePreviewStatus Phase { get; set; } = FurniturePreviewStatus.Active;
    }

    #endregion
}

/// <summary>
/// A record retired without touching its object: the ownership proof failed
/// (deleted, replaced or the tag lost or changed outside RSC). Its object, if
/// any, is left to whoever owns it now, or to the game.
/// </summary>
/// <param name="Sequence">1 for the first record this host retired, then increasing.</param>
/// <param name="ProofFailure">Which part of the ownership receipt no longer matched (diagnostics, no address).</param>
public readonly record struct AbandonedActor(long Sequence, RscObjectId ObjectId, int GlobalIndex, int CreatedComIndex, string Reason,
    string? ProofFailure = null);

/// <summary>
/// One native creation attempt (diagnostics, non-sensitive): what the
/// automatic allocator returned and each gate's fact. A null fact was not
/// reached. <see cref="FailedGate"/> is null for a verified creation.
/// </summary>
public sealed record ActorCreationAttempt(
    long Sequence,
    int? ReturnedComIndex,
    bool? ComIndexInRange,
    bool? SlotWasEmpty,
    bool? ObjectReturned,
    int? IndexRoundTrip,
    bool? RawEntryMatches,
    int? GlobalIndex,
    bool? GlobalIndexInRange,
    bool? TableMatches,
    bool? IsLocalPlayer,
    string? Kind,
    string? FailedGate,
    bool Deleted)
{
    public bool Succeeded => FailedGate is null;

    /// <summary>A deterministic policy refusal (not retried).</summary>
    public bool Permanent { get; init; }

    /// <summary>
    /// The log line for this attempt: in full with the development tools
    /// (dev plugin only); otherwise only a failed check's gate (the gate is
    /// not in the player text); none for a verified creation.
    /// </summary>
    public string? LogLine(bool developmentTools) => developmentTools
        ? "RSC actor creation " + Describe()
        : FailedGate is { } gate
            ? $"RSC actor creation attempt #{Sequence} failed a check: {gate}" + (Permanent ? " (not retried)." : ".")
            : null;

    public string Describe() =>
        $"#{Sequence}: returned COM index {Text(ReturnedComIndex)} (in 0–248: {Text(ComIndexInRange)}), " +
        $"slot empty before {Text(SlotWasEmpty)}, " +
        $"object returned {Text(ObjectReturned)}, GetIndexByObject {Text(IndexRoundTrip)}, " +
        $"raw COM entry matches {Text(RawEntryMatches)}, global index {Text(GlobalIndex)} " +
        $"(in 200–448: {Text(GlobalIndexInRange)}), table entry matches {Text(TableMatches)}, " +
        $"local player {Text(IsLocalPlayer)}, fresh kind {Kind ?? "n/a"}; " +
        (FailedGate is null ? "verified." :
            $"failed: {FailedGate}; deleted {(Deleted ? "yes" : "no")}; {(Permanent ? "not retried" : "retried")}.");

    private static string Text(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";

    private static string Text(bool? value) => value switch { true => "yes", false => "no", null => "n/a" };
}

internal sealed class CreationAttemptBuilder
{
    internal int? ReturnedComIndex { get; set; }
    internal bool? SlotWasEmpty { get; set; }
    internal bool? ObjectReturned { get; set; }
    internal int? IndexRoundTrip { get; set; }
    internal bool? RawEntryMatches { get; set; }
    internal int? GlobalIndex { get; set; }
    internal bool? TableMatches { get; set; }
    internal bool? IsLocalPlayer { get; set; }
    internal string? Kind { get; set; }

    internal ActorCreationAttempt Build(string? failedGate, bool deleted) => new(
        0,
        ReturnedComIndex,
        ReturnedComIndex is { } index ? ActorSlotRange.IsComIndex(index) : null,
        SlotWasEmpty,
        ObjectReturned,
        IndexRoundTrip,
        RawEntryMatches,
        GlobalIndex,
        GlobalIndex is { } global ? ActorSlotRange.IsAcceptedGlobalIndex(global) : null,
        TableMatches,
        IsLocalPlayer,
        Kind,
        failedGate,
        deleted);
}
