#if RSC_DEVELOPMENT
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// The stage-3 Debug host for hidden human puppets with vanilla skeletons
/// (decision sheet r2, owner decisions H-0 to H-12). Compiled only under
/// <c>RSC_DEVELOPMENT</c>, so a Release build has none of it. Only the
/// plugin's Debug-only T3-1 runner composes it; running that, IPC and every
/// experiment need their own approval.
///
/// <para>Every method except <see cref="TrackWork"/> and <see cref="UnloadAsync"/>
/// runs on the game thread. Its scope:</para>
/// <list type="bullet">
/// <item>M-1 in one call (<see cref="Spawn"/>): create, B-2 copy from the
/// player, self-copy, untargetable with the nameplate hidden (H1-a), both
/// maps to −1 and read back, the O1 scan, the identity write, then both maps,
/// the identity and the targetable bit re-verified, at alpha 0, with no draw
/// and no by-index call.</item>
/// <item>M-2 and F2 (<see cref="CaptureBaseline"/>, <see cref="OnFrameworkUpdate"/>):
/// every puppet's maps on every update, and its slot after deletion.</item>
/// <item>P5 checks 1–7, P6(a) and P16 (<see cref="VerifyIsolation"/>), and H-5's
/// non-forced temporary assignment as candidate code (<see cref="AssignTemporaryCollection"/>).</item>
/// <item>P13 steps 1–3 and 6–8 with D1 and D2 (<see cref="Remove"/>); the
/// unload order of H-7 (<see cref="UnloadAsync"/>).</item>
/// </list>
/// <para>Glamourer is read-only (H-12): there is no state, apply, unlock or
/// deletion call, so P13 steps 4, 5, 9 and 10 and every P14 path are absent.
/// Every stop latches: no spawn, draw or assignment follows, and nothing is
/// retried. Lost receipts are stops with the residue recorded (H-6).</para>
/// <para>T3-1 amendments (H1, an explicit T3-1-only freeze exception): the
/// targetable bit is read back after H1-a, before the draw and on every update
/// (H1-e); a provider generation change while a puppet is live is a Provider
/// stop (H1-b); a scene that is not quiet refuses a spawn and, while a puppet
/// is live, stops (H1-c); and a port call that throws inside a host call is a
/// latched Provider stop with clean-up, the new object being tracked before
/// its first read (H1-d).</para>
/// </summary>
public sealed class HumanPuppetHost
{
    /// <summary>D2 (r5b): a puppet's object may be deleted only in an update strictly after t + 8.</summary>
    public const int DeletionDelayUpdates = 8;

    /// <summary>F2-R item 1's phase when a provider did not answer after the deletion (H1-b).</summary>
    public const string NotMeasuredPhase = "F2-R item 1 not measured";

    private readonly IHumanPuppetBackend backend;
    private readonly IHumanParentMapPort maps;
    private readonly IHumanPenumbraPort penumbra;
    private readonly IHumanGlamourerReadPort glamourer;
    private readonly HumanPuppetWorld world;
    private readonly HumanPuppetKind kind;
    private readonly Func<int> otherRscActors;
    private readonly Func<long> providerGeneration;
    private readonly Func<string?> liveExit;
    private readonly int puppetLimit;
    private readonly Dictionary<HumanPuppetHandle, Puppet> puppets = [];
    private readonly HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HumanPuppetResidue> residue = [];
    private readonly List<HumanParentMapReading> readings = [];
    private readonly List<IDisposable> dependencies = [];
    private readonly object workGate = new();
    private readonly List<Task> work = [];
    private bool customizePlusChecked;
    private long customizePlusCheckedGeneration;
    private bool unloading;
    private long update;

    /// <param name="otherRscActors">Every other RSC actor in the scene; they count toward the eight.</param>
    /// <param name="providerGeneration">H1-b: changes whenever Penumbra, Glamourer or Customize+ loads, unloads or reinitializes.</param>
    /// <param name="liveExit">H1-c: null while the scene is quiet; otherwise why it is not.</param>
    /// <param name="puppetLimit">At most <see cref="ActorCapacityPolicy.ActiveMaximumActorsPerScene"/>.</param>
    public HumanPuppetHost(
        IHumanPuppetBackend backend,
        IHumanParentMapPort maps,
        IHumanPenumbraPort penumbra,
        IHumanGlamourerReadPort glamourer,
        HumanPuppetWorld world,
        HumanPuppetKind kind,
        Func<int> otherRscActors,
        Func<long> providerGeneration,
        Func<string?> liveExit,
        int puppetLimit = 1)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.maps = maps ?? throw new ArgumentNullException(nameof(maps));
        this.penumbra = penumbra ?? throw new ArgumentNullException(nameof(penumbra));
        this.glamourer = glamourer ?? throw new ArgumentNullException(nameof(glamourer));
        this.otherRscActors = otherRscActors ?? throw new ArgumentNullException(nameof(otherRscActors));
        this.providerGeneration = providerGeneration ?? throw new ArgumentNullException(nameof(providerGeneration));
        this.liveExit = liveExit ?? throw new ArgumentNullException(nameof(liveExit));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (puppetLimit is < 1 or > ActorCapacityPolicy.ActiveMaximumActorsPerScene)
            throw new ArgumentOutOfRangeException(nameof(puppetLimit));
        this.world = world;
        this.kind = kind;
        this.puppetLimit = puppetLimit;
    }

    /// <summary>The first stop, or null. It never clears.</summary>
    public HumanPuppetStopRecord? Stop { get; private set; }

    public IReadOnlyList<HumanPuppetResidue> Residue => residue;

    /// <summary>Every map reading with its update (the run's record).</summary>
    public IReadOnlyList<HumanParentMapReading> Readings => readings;

    public long CurrentUpdate => update;

    public int InFlightWork
    {
        get
        {
            lock (workGate)
                return work.Count(task => !task.IsCompleted);
        }
    }

    /// <summary>Puppets that still occupy a slot: live, awaiting deletion, or left as residue.</summary>
    public int OccupiedSlots => puppets.Values.Count(puppet => puppet.Phase != HumanPuppetPhase.Deleted);

    public IReadOnlyList<HumanPuppetSnapshot> Snapshot() => puppets.Values.Select(puppet => puppet.Snapshot()).ToArray();

    /// <summary>Test seam: where names come from (production: <see cref="HumanPuppetName.Create"/>).</summary>
    internal Func<IReadOnlySet<char>, string> NameSource { get; set; } = HumanPuppetName.Create;

    /// <summary>Test and record seam: the puppet's synthetic name (never logged or sent).</summary>
    internal string? NameOf(HumanPuppetHandle handle) => puppets.TryGetValue(handle, out var puppet) ? puppet.Name : null;

    internal uint? KeyOf(HumanPuppetHandle handle) => puppets.TryGetValue(handle, out var puppet) ? puppet.Key : null;

    /// <summary>
    /// CP-3, read-only: the Customize+ IPC version is (6, 4) and both parent
    /// calls are present. On failure Customize+ stays off for humans and the
    /// host stops (fail closed). Spawning needs a passed check.
    /// </summary>
    public HumanPuppetOutcome CheckCustomizePlus()
    {
        try
        {
            return CheckCustomizePlusCore();
        }
        catch (Exception exception)
        {
            return HumanPuppetOutcome.Stopped(Fault("CP-3", exception));
        }
    }

    private HumanPuppetOutcome CheckCustomizePlusCore()
    {
        if (Stop is { } stopped)
            return HumanPuppetOutcome.Stopped(stopped);
        if (!maps.CustomizePlusLoaded)
            return HumanPuppetOutcome.Stopped(Latch(HumanPuppetStopRule.CustomizePlus, "CP-3", "Customize+ is not loaded"));
        if (maps.CustomizePlusApiVersion != (6, 4))
            return HumanPuppetOutcome.Stopped(Latch(HumanPuppetStopRule.CustomizePlus, "CP-3", "the Customize+ IPC version is not (6, 4)"));
        if (!maps.CustomizePlusParentIpcPresent)
            return HumanPuppetOutcome.Stopped(Latch(HumanPuppetStopRule.CustomizePlus, "CP-3", "the Customize+ parent IPC is absent"));
        if (!maps.CustomizePlusValid)
            return HumanPuppetOutcome.Stopped(Latch(HumanPuppetStopRule.CustomizePlus, "CP-3", "Customize+ does not report itself valid"));
        // H1-b: CP-3 holds only under the provider generation it was checked in.
        customizePlusCheckedGeneration = providerGeneration();
        customizePlusChecked = true;
        return HumanPuppetOutcome.Ok;
    }

    /// <summary>M-2's baseline: both maps for every slot 200–439 and the read-only provider facts. Reads only.</summary>
    public HumanProviderBaseline CaptureBaseline()
    {
        var penumbraMap = new int?[HumanPuppetSlotRange.Count];
        var customizeMap = new int?[HumanPuppetSlotRange.Count];
        for (var i = 0; i < HumanPuppetSlotRange.Count; i++)
        {
            var index = HumanPuppetSlotRange.MinimumGlobalIndex + i;
            penumbraMap[i] = maps.GetPenumbraParent(index);
            customizeMap[i] = maps.CustomizePlusLoaded ? maps.GetCustomizePlusParent(index) : null;
        }
        return new(update, penumbraMap, customizeMap, maps.CustomizePlusApiVersion, glamourer.ApiVersion, glamourer.CanUnlockBaseline());
    }

    /// <summary>
    /// M-1 in this one game-thread call. Refuses before any side effect when
    /// the host is stopped or unloading, a provider or CP-3 is missing, the
    /// world is not F11-verified (no W-1 fallback), or the eight-actor total
    /// would be exceeded. After creation, any failure hides the puppet, makes
    /// no by-index call, deletes it if it is still provably RSC's, and stops.
    /// A port call that throws is a Provider stop with the same clean-up (H1-d).
    /// </summary>
    public HumanPuppetSpawnResult Spawn()
    {
        try
        {
            return SpawnCore();
        }
        catch (Exception exception)
        {
            return new(null, HumanPuppetRefusal.None, Fault("spawn", exception), [], []);
        }
    }

    private HumanPuppetSpawnResult SpawnCore()
    {
        var refusal = SpawnRefusal();
        if (refusal != HumanPuppetRefusal.None)
            return new(null, refusal, refusal == HumanPuppetRefusal.Stopped ? Stop : null, [], []);

        var callReadings = new List<HumanParentMapReading>();
        var copies = new List<HumanPuppetCopyRecord>();
        HumanPuppetKindRestore? kindRestore = null;
        var name = FreshName();
        usedNames.Add(name);
        // H1-b: the generation this puppet lives under; any change while it is live stops.
        var puppet = new Puppet(new(Guid.NewGuid()), name, world.WorldId, HumanPuppetKey.Create(), kind, providerGeneration());

        HumanPuppetSpawnResult Failed(HumanPuppetStopRule rule, string step, string detail)
        {
            var stop = Latch(rule, step, detail);
            CleanLive("stop");
            readings.AddRange(callReadings);
            return new(null, HumanPuppetRefusal.None, stop, callReadings, copies) { KindRestore = kindRestore };
        }

        // 1. Create, and prove the exact new object is RSC's (checks 1–5).
        var before = backend.ComEntriesSnapshot();
        var comIndex = backend.CreateCharacter();
        if (!ActorSlotRange.IsComIndex(comIndex) || comIndex >= before.Length || before[comIndex] != 0)
        {
            // H-6: the body receipt is the before/after snapshot. A new entry
            // that RSC cannot attribute is residue, never deleted.
            var after = backend.ComEntriesSnapshot();
            if (!after.SequenceEqual(before))
                residue.Add(new(puppet.Handle, "an object the allocator created without a usable COM index", "body receipt lost"));
            return Failed(after.SequenceEqual(before) ? HumanPuppetStopRule.Ownership : HumanPuppetStopRule.S6Residue,
                "create", "the allocator returned no new COM slot");
        }
        nint address;
        bool proved;
        try
        {
            address = backend.ComObjectByIndex(comIndex);
            proved = address != 0 && Array.IndexOf(before, address) < 0 && backend.ComIndexOf(address) == comIndex && backend.ComEntry(comIndex) == address;
        }
        catch
        {
            // H1-d: a new object exists but is not yet tracked; it is residue, never deleted.
            residue.Add(new(puppet.Handle, "the object in the returned COM slot", "a port call threw before its creation was proved"));
            throw;
        }
        if (!proved)
        {
            residue.Add(new(puppet.Handle, "the object in the returned COM slot", "its creation could not be proved"));
            return Failed(HumanPuppetStopRule.S6Residue, "create", "the returned object could not be proved new");
        }
        // H1-d: tracked before its first read, so a throw from here on still
        // reaches clean-up. Until its facts are bound, only the creation slot
        // proves it is RSC's, and it has had no write.
        puppet.BindCreation(address, comIndex);
        puppets[puppet.Handle] = puppet;
        var created = backend.ReadFacts(address);
        if (!HumanPuppetSlotRange.Contains(created.GlobalIndex) || backend.ObjectTableEntry(created.GlobalIndex) != address || address == backend.LocalPlayer)
        {
            puppet.BindFacts(created);
            // No field write outside 200–439 (SI-1 item 1): it was never drawn, so it is only deleted.
            puppet.Phase = HumanPuppetPhase.AwaitingDeletion;
            DeleteObject(puppet);
            return Failed(HumanPuppetStopRule.Ownership, "create", "the new object is outside 200–439");
        }
        puppet.BindFacts(created);
        // R-8a: the kind and sub-kind as created, before any write.
        puppet.CreatedKind = backend.ReadKind(address);
        backend.SetAlpha(address, 0f);

        // 2–3. B-2: copy from the player, then the self-copy; re-prove after each.
        var player = backend.LocalPlayer;
        if (player == 0)
        {
            AbortInCall(puppet, "no local player to copy");
            return Failed(HumanPuppetStopRule.Ownership, "copy", "no local player");
        }
        foreach (var (copy, source) in new[] { ("from the player", player), ("self-copy", address) })
        {
            backend.CopyCharacter(address, source);
            if (OwnershipMismatch(puppet) is { } mismatch)
            {
                Forget(puppet, $"after the copy {copy}: {mismatch}");
                return Failed(HumanPuppetStopRule.Ownership, "copy", mismatch);
            }
            var facts = backend.ReadFacts(address);
            copies.Add(new(copy, facts.RawKind, facts.HomeWorld, facts.NameId, facts.OwnerId, facts.HasHumanModel, CustomizeFilled(facts), facts.Targetable));
            backend.SetAlpha(address, 0f);
        }

        // R-8a (D4 as amended): the copy from the player can leave the
        // player's kind and sub-kind. Only on a BattleNpc request whose
        // creation produced BattleNpc, and only when the copies changed the
        // pair, the pair recorded at creation is written back once, on the
        // proved object, and both bytes are read back before any hiding, map
        // or identity write. No other kind is ever written, and no other
        // object is written. An unreadable pair gets no write.
        if (kind == HumanPuppetKind.BattleNpc &&
            puppet.CreatedKind is { Kind: (byte)HumanPuppetKind.BattleNpc } createdKind &&
            backend.ReadKind(address) is { } afterCopies && afterCopies != createdKind)
        {
            // Checks 1–5 immediately before the write.
            if (OwnershipMismatch(puppet) is { } beforeRestore)
            {
                Forget(puppet, $"before the kind restore: {beforeRestore}");
                return Failed(HumanPuppetStopRule.Ownership, "copy", beforeRestore);
            }
            backend.RestoreKind(address, createdKind);
            kindRestore = new(createdKind, afterCopies, backend.ReadKind(address));
            if (!kindRestore.Restored)
            {
                AbortInCall(puppet, "the restored kind did not read back");
                return Failed(HumanPuppetStopRule.S5Identity, "copy", "the kind and sub-kind did not read back as created after the restore");
            }
        }

        // D4: a kind other than BattleNpc after the copies (and any restore)
        // stops here, before any hiding, map or identity write.
        if (kind == HumanPuppetKind.BattleNpc && backend.ReadFacts(address).RawKind != (int)HumanPuppetKind.BattleNpc)
        {
            AbortInCall(puppet, "the copies left a kind other than BattleNpc");
            return Failed(HumanPuppetStopRule.S5Identity, "copy", "the kind after the copies is not BattleNpc");
        }

        // H1-a (R-7): after the last copy, untargetable with the nameplate
        // hidden, on the proved object only; then the bit is read back.
        if (OwnershipMismatch(puppet) is { } beforeHiding)
        {
            Forget(puppet, $"before hiding: {beforeHiding}");
            return Failed(HumanPuppetStopRule.Ownership, "hide", beforeHiding);
        }
        backend.PrepareHidden(address);
        if (backend.ReadFacts(address).Targetable)
        {
            AbortInCall(puppet, "the puppet stayed targetable");
            return Failed(HumanPuppetStopRule.Hiding, "hide", "the puppet is still targetable after hiding");
        }

        // 4–5. Both maps to −1, each read back through its owner.
        var detach = Detach(puppet, "M-1 detach", callReadings);
        if (detach is not null)
        {
            AbortInCall(puppet, detach);
            return Failed(HumanPuppetStopRule.S4ParentMapOrIsolation, "detach", detach);
        }

        // 6. The O1 scan, immediately before the identity write. A hit stops (H-10).
        if (Carrier(puppet) is { } carrier)
        {
            AbortInCall(puppet, "another object carries the identity");
            return Failed(HumanPuppetStopRule.Collision, "O1 scan", $"global index {carrier} carries the identity");
        }

        // 7. The identity write (SI-1 item 2 only).
        var beforeWrite = backend.ReadFacts(address);
        if (kind == HumanPuppetKind.BattleNpc && beforeWrite.RawKind != (int)HumanPuppetKind.BattleNpc)
        {
            AbortInCall(puppet, "the kind changed after the copies");
            return Failed(HumanPuppetStopRule.S5Identity, "identity", "the kind before the identity write is not BattleNpc");
        }
        var clear = beforeWrite.NameId != 0 || beforeWrite.OwnerId != HumanPuppetFacts.NoOwner;
        // SI-1 item 1: checks 1–5 immediately before the write.
        if (OwnershipMismatch(puppet) is { } beforeIdentity)
        {
            Forget(puppet, $"before the identity write: {beforeIdentity}");
            return Failed(HumanPuppetStopRule.Ownership, "identity", beforeIdentity);
        }
        backend.WriteIdentity(address, name, world.WorldId, kind == HumanPuppetKind.Player, clear);

        // 8. Both maps and the identity again.
        if (Verify(puppet, "M-1 re-verify", callReadings) is { } failure)
        {
            AbortInCall(puppet, failure.Detail);
            return Failed(failure.Rule, "M-1 re-verify", failure.Detail);
        }
        readings.AddRange(callReadings);
        return new(puppet.Handle, HumanPuppetRefusal.None, null, callReadings, copies) { KindRestore = kindRestore };
    }

    /// <summary>Asks the hidden puppet to draw, still at alpha 0, after re-verifying it.</summary>
    public HumanPuppetOutcome RequestDraw(HumanPuppetHandle handle) => Guarded("draw", () => RequestDrawCore(handle));

    private HumanPuppetOutcome RequestDrawCore(HumanPuppetHandle handle)
    {
        if (Guard(handle, out var puppet) is { } refused)
            return refused;
        if (puppet.Phase != HumanPuppetPhase.Hidden)
            return HumanPuppetOutcome.Refused(HumanPuppetRefusal.NotHidden);
        if (VerifyOrStop(puppet, "before draw") is { } stop)
            return HumanPuppetOutcome.Stopped(stop);
        backend.SetAlpha(puppet.Address, 0f);
        backend.EnableDraw(puppet.Address);
        puppet.Phase = HumanPuppetPhase.DrawRequested;
        puppet.DrawRequestedUpdate = update;
        return HumanPuppetOutcome.Ok;
    }

    /// <summary>
    /// T3-1 step 3's isolation checks: P6(a), <c>IndividualSet = false</c>,
    /// and P16, the effective collection equals the expected one, never by
    /// the <c>Yourself</c> path. Reads only; a failure stops (S4).
    /// </summary>
    public HumanPuppetOutcome VerifyIsolation(HumanPuppetHandle handle) => Guarded("isolation", () => VerifyIsolationCore(handle));

    private HumanPuppetOutcome VerifyIsolationCore(HumanPuppetHandle handle)
    {
        if (Guard(handle, out var puppet) is { } refused)
            return refused;
        // P6(a) and P16 hold only while unassigned: once assigned, RSC's own collection resolves first.
        if (puppet.Collection is not null || puppet.CollectionPending)
            return HumanPuppetOutcome.Refused(HumanPuppetRefusal.AlreadyAssigned);
        if (VerifyOrStop(puppet, "isolation") is { } stop)
            return HumanPuppetOutcome.Stopped(stop);
        var facts = backend.ReadFacts(puppet.Address);
        if (!CustomizeFilled(facts) || !facts.HasHumanModel)
            return HumanPuppetOutcome.Refused(HumanPuppetRefusal.CustomizeNotFilled);
        return IsolationFailure(puppet, facts) is { } failure
            ? HumanPuppetOutcome.Stopped(StopAndClean(failure.Rule, "isolation", failure.Detail))
            : HumanPuppetOutcome.Ok;
    }

    /// <summary>
    /// H-5, candidate code only (F1): a per-puppet temporary collection and a
    /// non-forced assignment, after P5, both maps, P6(a) and P16 in this same
    /// call. A lost collection id is S6. A refused assignment means an exact
    /// entry exists: a collision, so the host stops.
    /// </summary>
    public HumanPuppetOutcome AssignTemporaryCollection(HumanPuppetHandle handle)
    {
        if (Guard(handle, out var puppet) is { } refused)
            return refused;
        if (puppet.Collection is not null || puppet.CollectionPending)
            return HumanPuppetOutcome.Refused(HumanPuppetRefusal.AlreadyAssigned);
        if (VerifyOrStop(puppet, "before assignment") is { } stop)
            return HumanPuppetOutcome.Stopped(stop);
        var facts = backend.ReadFacts(puppet.Address);
        if (!CustomizeFilled(facts) || !facts.HasHumanModel)
            return HumanPuppetOutcome.Refused(HumanPuppetRefusal.CustomizeNotFilled);
        if (IsolationFailure(puppet, facts) is { } isolation)
            return HumanPuppetOutcome.Stopped(StopAndClean(isolation.Rule, "before assignment", isolation.Detail));

        // H-6: the receipt, with its change time for D2, exists before the call.
        puppet.CollectionPending = true;
        RecordChange(puppet);
        var collection = penumbra.CreateTemporaryCollection("RscHuman" + puppet.Handle.Value.ToString("N")[..8]);
        if (collection is not { } id || id == Guid.Empty)
        {
            residue.Add(new(puppet.Handle, "a temporary collection whose id did not come back", "collection receipt lost"));
            return HumanPuppetOutcome.Stopped(StopAndClean(HumanPuppetStopRule.S6Residue, "create collection", "the temporary collection id was lost"));
        }
        puppet.CollectionPending = false;
        puppet.Collection = id;
        RecordChange(puppet);

        if (VerifyOrStop(puppet, "before the by-index assignment") is { } again)
            return HumanPuppetOutcome.Stopped(again);
        // O2: in the same call as the side effect.
        if (Carrier(puppet) is { } carrier)
            return HumanPuppetOutcome.Stopped(StopAndClean(HumanPuppetStopRule.Collision, "before assignment",
                $"global index {carrier} carries the identity"));
        switch (penumbra.AssignTemporaryCollection(id, puppet.GlobalIndex))
        {
            case HumanAssignmentResult.Success:
                puppet.Assigned = true;
                RecordChange(puppet);
                break;
            case HumanAssignmentResult.Refused:
                return HumanPuppetOutcome.Stopped(StopAndClean(HumanPuppetStopRule.Collision, "assign", "an exact Penumbra entry exists for the identity"));
            default:
                // Unknown whether anything was assigned: treat it as assigned for D1.
                puppet.Assigned = true;
                RecordChange(puppet);
                return HumanPuppetOutcome.Stopped(StopAndClean(HumanPuppetStopRule.Provider, "assign", "the assignment failed"));
        }
        var reading = penumbra.GetCollectionForObject(puppet.GlobalIndex);
        if (reading is not { ObjectValid: true } read || read.EffectiveCollection != id)
            return HumanPuppetOutcome.Stopped(StopAndClean(HumanPuppetStopRule.S4ParentMapOrIsolation, "after assignment",
                "the effective collection is not the puppet's own"));
        if (VerifyOrStop(puppet, "after assignment") is { } after)
            return HumanPuppetOutcome.Stopped(after);
        return HumanPuppetOutcome.Ok;
    }

    /// <summary>
    /// P13 steps 1–3 and 6–8 with P18-D: hide (only after checks 1–5 show the
    /// object is still RSC's), revalidate, verify both maps (re-detach once on
    /// a mismatch), remove the assignment before the collection (D1), then
    /// delete the object only in an update strictly after t + 8 from the
    /// latest change (D2); until then it stays hidden and keeps its slot.
    /// </summary>
    public HumanPuppetOutcome Remove(HumanPuppetHandle handle) => Guarded("remove", () => RemoveCore(handle));

    private HumanPuppetOutcome RemoveCore(HumanPuppetHandle handle)
    {
        if (!puppets.TryGetValue(handle, out var puppet))
            return HumanPuppetOutcome.Refused(HumanPuppetRefusal.UnknownPuppet);
        lock (workGate)
            if (unloading)
                return HumanPuppetOutcome.Refused(HumanPuppetRefusal.Unloading);
        if (puppet.Phase is HumanPuppetPhase.Deleted or HumanPuppetPhase.Residue or HumanPuppetPhase.AwaitingDeletion)
            return HumanPuppetOutcome.Ok;
        BeginRemoval(puppet, "requested");
        return HumanPuppetOutcome.Ok;
    }

    /// <summary>
    /// Once per framework update (strictly increasing): F2's readings for every
    /// live puppet, P5 for each, the draw observation, F2-R item 1 for a slot
    /// deleted on an earlier update, and every deletion D2 now allows.
    /// </summary>
    public void OnFrameworkUpdate(long frameworkUpdate)
    {
        if (frameworkUpdate <= update)
            throw new ArgumentOutOfRangeException(nameof(frameworkUpdate), "updates must increase");
        update = frameworkUpdate;
        try
        {
            Tick();
        }
        catch (Exception exception)
        {
            Fault("F2 tick", exception);
        }
    }

    private void Tick()
    {
        bool cleanUpAllowed;
        lock (workGate)
            cleanUpAllowed = !unloading;

        foreach (var puppet in puppets.Values.ToArray())
        {
            switch (puppet.Phase)
            {
                case HumanPuppetPhase.Hidden or HumanPuppetPhase.DrawRequested when Stop is null:
                    if (Verify(puppet, "F2 tick", readings) is { } failure)
                    {
                        Latch(failure.Rule, "F2 tick", failure.Detail);
                        break;
                    }
                    // O3/O4: observed on every update; any carrier stops (H-10).
                    if (Carrier(puppet) is { } carrier)
                    {
                        Latch(HumanPuppetStopRule.Collision, "F2 tick", $"global index {carrier} carries the identity");
                        break;
                    }
                    if (puppet.Phase == HumanPuppetPhase.DrawRequested && puppet.DrawObservedUpdate is null && backend.HasHumanDrawObject(puppet.Address))
                        puppet.DrawObservedUpdate = update;
                    break;
                // While unloading, deletion waits for the unload's own clean-up after the drain.
                case HumanPuppetPhase.AwaitingDeletion when cleanUpAllowed && DeletionAllowed(puppet):
                    DeleteObject(puppet);
                    break;
                case HumanPuppetPhase.Deleted when puppet.DeletedUpdate < update && !puppet.SlotCheckedAfterDeletion:
                    puppet.SlotCheckedAfterDeletion = true;
                    // H1-b: measured only when both providers answer; otherwise recorded as not measured.
                    var after = Read(puppet.GlobalIndex, "F2-R item 1");
                    if (after.Penumbra is null || after.CustomizePlus is null)
                    {
                        readings.Add(after with { Phase = NotMeasuredPhase });
                        break;
                    }
                    readings.Add(after);
                    if (after.Penumbra != -1 || after.CustomizePlus != -1)
                        Latch(HumanPuppetStopRule.S4ParentMapOrIsolation, "F2-R item 1", "a deleted puppet's slot is not −1 in both maps");
                    break;
            }
        }
        // Any stop latched on this update, or outside an update since the last
        // one, ends every live puppet here. While unloading, the unload's own
        // clean-up does that after the drain (H-7).
        if (Stop is not null && cleanUpAllowed)
            CleanLive("stop");
    }

    /// <summary>
    /// Keeps track of work that must finish before dependencies are released
    /// (the W1 drain requirement). False once unloading has begun.
    /// </summary>
    public bool TrackWork(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (workGate)
        {
            if (unloading)
                return false;
            work.RemoveAll(entry => entry.IsCompleted);
            work.Add(task);
            return true;
        }
    }

    /// <summary>Released last, in reverse order, after the puppets and the drained work.</summary>
    public void RegisterDependency(IDisposable dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        lock (workGate)
        {
            if (unloading)
                throw new InvalidOperationException("the host is unloading");
            dependencies.Add(dependency);
        }
    }

    /// <summary>
    /// H-7's unload order: refuse new work; on the game thread, hide every
    /// puppet and revalidate it; drain in-flight work; on the game thread, run
    /// P13 for each puppet, deleting only outside D2 (a puppet inside D2 is
    /// left hidden and alive, recorded as residue: never a pass); release
    /// dependencies last. No deletion breaches D2.
    /// </summary>
    public async Task<HumanPuppetUnloadReport> UnloadAsync(Func<Action, Task> runOnGameThread)
    {
        ArgumentNullException.ThrowIfNull(runOnGameThread);
        var order = new List<string>();
        Task[] pending;
        lock (workGate)
        {
            if (unloading)
                throw new InvalidOperationException("the host is already unloading");
            unloading = true;
            pending = work.Where(task => !task.IsCompleted).ToArray();
        }
        order.Add("refuse new work");

        var failures = new List<string>();
        async Task OnGameThread(string step, Action action)
        {
            try
            {
                await runOnGameThread(action).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add($"{step}: {exception.GetType().Name}");
                Latch(HumanPuppetStopRule.S6Residue, "unload", $"{step} failed on the game thread");
            }
        }

        await OnGameThread("hide and revalidate", () =>
        {
            foreach (var puppet in puppets.Values.Where(puppet => puppet.Phase is HumanPuppetPhase.Hidden or HumanPuppetPhase.DrawRequested).ToArray())
                OrResidue(puppet, "unload hide", () => HideIfOwned(puppet, "unload"));
        }).ConfigureAwait(false);
        order.Add("hide and revalidate");

        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // The work's own failure is its owner's; draining only waits for it.
            }
        }
        order.Add("drain work");

        var deleted = 0;
        await OnGameThread("clean up puppets", () =>
        {
            foreach (var puppet in puppets.Values.ToArray())
            {
                var deletedBefore = puppet.Phase == HumanPuppetPhase.Deleted;
                OrResidue(puppet, "unload clean-up", () =>
                {
                    if (puppet.Phase is HumanPuppetPhase.Hidden or HumanPuppetPhase.DrawRequested)
                        BeginRemoval(puppet, "unload");
                    if (puppet.Phase == HumanPuppetPhase.AwaitingDeletion)
                    {
                        if (DeletionAllowed(puppet))
                            DeleteObject(puppet);
                        else
                        {
                            // Provisional: the unload-within-D2 decision stays the owner's.
                            // The fail-safe here leaves the puppet hidden and alive; never a pass.
                            puppet.Phase = HumanPuppetPhase.Residue;
                            residue.Add(new(puppet.Handle, "a hidden puppet left alive", "unload within D2; deleting it would breach D2"));
                            Latch(HumanPuppetStopRule.S6Residue, "unload", "a puppet was left alive inside D2");
                        }
                    }
                });
                if (!deletedBefore && puppet.Phase == HumanPuppetPhase.Deleted)
                    deleted++;
            }
        }).ConfigureAwait(false);
        order.Add("clean up puppets");

        IDisposable[] release;
        lock (workGate)
            release = Enumerable.Reverse(dependencies).ToArray();
        foreach (var dependency in release)
        {
            try
            {
                dependency.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add($"release: {exception.GetType().Name}");
            }
        }
        order.Add("release dependencies");
        return new(deleted, residue.ToArray(), pending.Length, failures, order);
    }

    /// <summary>Penumbra available, and Customize+ loaded, valid, at (6, 4) and with its parent IPC, as CP-3 requires.</summary>
    private bool ProvidersAsChecked() =>
        maps.PenumbraAvailable && maps.CustomizePlusLoaded && maps.CustomizePlusValid &&
        maps.CustomizePlusApiVersion == (6, 4) && maps.CustomizePlusParentIpcPresent;

    private HumanPuppetRefusal SpawnRefusal()
    {
        if (Stop is not null)
            return HumanPuppetRefusal.Stopped;
        lock (workGate)
            if (unloading)
                return HumanPuppetRefusal.Unloading;
        // H1-b, H1-d: every provider present and as CP-3 found it; Glamourer must answer its version.
        if (!backend.IsAvailable || !ProvidersAsChecked() || glamourer.ApiVersion is null)
            return HumanPuppetRefusal.Unavailable;
        if (liveExit() is not null)
            return HumanPuppetRefusal.SceneNotQuiet;
        if (!customizePlusChecked || providerGeneration() != customizePlusCheckedGeneration)
            return HumanPuppetRefusal.CustomizePlusNotChecked;
        if (!world.IsUsable)
            return HumanPuppetRefusal.WorldUnverified;
        // H-3: a configured world equal to the player's own would be a silent W-1.
        var player = backend.LocalPlayer;
        if (player == 0)
            return HumanPuppetRefusal.Unavailable;
        if (backend.ReadFacts(player).HomeWorld == world.WorldId)
            return HumanPuppetRefusal.WorldUnverified;
        var occupied = OccupiedSlots;
        if (occupied >= puppetLimit || occupied + Math.Max(0, otherRscActors()) >= ActorCapacityPolicy.ActiveMaximumActorsPerScene)
            return HumanPuppetRefusal.AtCapacity;
        return HumanPuppetRefusal.None;
    }

    private HumanPuppetOutcome? Guard(HumanPuppetHandle handle, out Puppet puppet)
    {
        puppet = null!;
        if (Stop is { } stop)
            return HumanPuppetOutcome.Stopped(stop);
        lock (workGate)
            if (unloading)
                return HumanPuppetOutcome.Refused(HumanPuppetRefusal.Unloading);
        if (!puppets.TryGetValue(handle, out var found))
            return HumanPuppetOutcome.Refused(HumanPuppetRefusal.UnknownPuppet);
        puppet = found;
        if (puppet.Phase is not (HumanPuppetPhase.Hidden or HumanPuppetPhase.DrawRequested))
            return HumanPuppetOutcome.Refused(HumanPuppetRefusal.NotHidden);
        return null;
    }

    private string FreshName()
    {
        var initials = puppets.Values.Where(puppet => puppet.Phase != HumanPuppetPhase.Deleted)
            .Select(puppet => HumanPuppetName.SurnameInitial(puppet.Name)).ToHashSet();
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var name = NameSource(initials);
            if (!HumanPuppetName.IsWellFormed(name) || initials.Contains(HumanPuppetName.SurnameInitial(name)))
                throw new InvalidOperationException("the name source returned a name outside P2");
            if (!usedNames.Contains(name))
                return name;
        }
        throw new InvalidOperationException("no unused name was drawn");
    }

    /// <summary>Checks 1–5, read-only: null while the object is still exactly this spawn's.</summary>
    private string? OwnershipMismatch(Puppet puppet)
    {
        try
        {
            if (!puppet.FactsBound)
            {
                // H1-d: before its first read, only the creation slot proves it.
                if (backend.ComObjectByIndex(puppet.ComIndex) != puppet.Address || backend.ComIndexOf(puppet.Address) != puppet.ComIndex)
                    return "its creation slot no longer holds it";
                return puppet.Address == backend.LocalPlayer ? "it is the local player" : null;
            }
            if (backend.ObjectTableEntry(puppet.GlobalIndex) != puppet.Address)
                return "the object table no longer holds it at its index";
            if (backend.ComObjectByIndex(puppet.ComIndex) != puppet.Address)
                return "its creation slot holds another object or none";
            if (backend.ComIndexOf(puppet.Address) != puppet.ComIndex)
                return "the object maps to another creation slot";
            if (puppet.Address == backend.LocalPlayer)
                return "it is the local player";
            var facts = backend.ReadFacts(puppet.Address);
            if (facts.GlobalIndex != puppet.GlobalIndex)
                return "its global index changed";
            if (facts.GameObjectId != puppet.GameObjectId || facts.EntityId != puppet.EntityId)
                return "its object or entity id changed";
            return null;
        }
        catch (Exception exception)
        {
            return $"reading it failed ({exception.GetType().Name})";
        }
    }

    /// <summary>P5-6 and P5-7: the recorded kind, the name with its NUL, the world, name ID 0 and owner none.</summary>
    private string? IdentityMismatch(Puppet puppet)
    {
        var facts = backend.ReadFacts(puppet.Address);
        if (facts.RawKind != (int)puppet.Kind)
            return "its kind is not the recorded kind";
        if (!string.Equals(facts.Name, puppet.Name, StringComparison.Ordinal) || !facts.NameTerminated)
            return "its name is not this spawn's";
        if (facts.HomeWorld != puppet.World)
            return "its world is not this spawn's";
        if (facts.NameId != 0 || facts.OwnerId != HumanPuppetFacts.NoOwner)
            return "its name ID or owner is set";
        return null;
    }

    private (HumanPuppetStopRule Rule, string Detail)? Verify(Puppet puppet, string phase, List<HumanParentMapReading> into)
    {
        if (OwnershipMismatch(puppet) is { } ownership)
            return (HumanPuppetStopRule.Ownership, ownership);
        if (liveExit() is { } exit)
            return (HumanPuppetStopRule.LiveExit, exit);
        if (providerGeneration() != puppet.ProviderGeneration)
            return (HumanPuppetStopRule.Provider, "a provider was loaded, unloaded or reinitialized while the puppet was live");
        if (!ProvidersAsChecked())
            return (HumanPuppetStopRule.Provider, "a parent-map provider is not available or not as CP-3 found it");
        var reading = Read(puppet.GlobalIndex, phase);
        into.Add(reading);
        if (!reading.Resolved)
            return (HumanPuppetStopRule.S4ParentMapOrIsolation, "a parent map does not resolve the puppet to itself");
        if (IdentityMismatch(puppet) is { } identity)
            return (HumanPuppetStopRule.S5Identity, identity);
        // H1-e: never targetable, in the re-verify, before the draw and on every update.
        if (backend.ReadFacts(puppet.Address).Targetable)
            return (HumanPuppetStopRule.Hiding, "the puppet is targetable");
        return null;
    }

    private HumanPuppetStopRecord? VerifyOrStop(Puppet puppet, string phase) =>
        Verify(puppet, phase, readings) is { } failure ? StopAndClean(failure.Rule, phase, failure.Detail) : null;

    private HumanParentMapReading Read(int globalIndex, string phase) => new(update, phase, globalIndex,
        maps.GetPenumbraParent(globalIndex), maps.CustomizePlusLoaded ? maps.GetCustomizePlusParent(globalIndex) : null);

    /// <summary>Sets both maps to −1 (Penumbra first) and reads both back; null on success.</summary>
    private string? Detach(Puppet puppet, string phase, List<HumanParentMapReading> into)
    {
        if (!maps.PenumbraAvailable || !maps.CustomizePlusLoaded)
            return "a parent-map provider is not available";
        if (!maps.SetPenumbraParent(puppet.GlobalIndex, -1))
            return "Penumbra's detach failed";
        if (!maps.SetCustomizePlusParent(puppet.GlobalIndex, -1))
            return "Customize+'s detach failed";
        var reading = Read(puppet.GlobalIndex, phase);
        into.Add(reading);
        return reading.Resolved ? null : "a map did not read back −1 or self";
    }

    /// <summary>The O1 scan: any other object with the name (ignoring case) on the same world, or either side AnyWorld.</summary>
    private int? Carrier(Puppet puppet)
    {
        var length = backend.ObjectTableLength;
        for (var index = 0; index < length; index++)
        {
            if (index == puppet.GlobalIndex || backend.ScanEntry(index) is not { } entry)
                continue;
            if (string.Equals(entry.Name, puppet.Name, StringComparison.OrdinalIgnoreCase) &&
                (entry.HomeWorld == puppet.World || entry.HomeWorld == HumanPuppetWorld.AnyWorld))
                return index;
        }
        return null;
    }

    /// <summary>P6(a) and P16, reads only: null when the puppet resolves to its expected collection by no individual entry.</summary>
    private (HumanPuppetStopRule Rule, string Detail)? IsolationFailure(Puppet puppet, HumanPuppetFacts facts)
    {
        // P16 step 1: the Yourself path must not apply.
        if (puppet.GlobalIndex == 0 || puppet.Address == backend.LocalPlayer || maps.GetPenumbraParent(puppet.GlobalIndex) == 0)
            return (HumanPuppetStopRule.S4ParentMapOrIsolation, "the Yourself path could apply");
        if (penumbra.GetCollectionForObject(puppet.GlobalIndex) is not { ObjectValid: true } reading)
            return (HumanPuppetStopRule.Provider, "Penumbra did not report the puppet's collection");
        if (reading.IndividualSet)
            return (HumanPuppetStopRule.Collision, "an exact individual entry exists for the identity");
        if (!TryExpectedCollection(facts, puppet.Kind, out var expected))
            return (HumanPuppetStopRule.Provider, "a collection type could not be read");
        if (expected is null)
            return (HumanPuppetStopRule.Provider, "no expected collection is set");
        return reading.EffectiveCollection == expected
            ? null
            : (HumanPuppetStopRule.S4ParentMapOrIsolation, "the effective collection is not the expected collection");
    }

    /// <summary>
    /// P16 steps 2 and 4: the first set collection in the resolver's order,
    /// from the customize bytes. False when any role in that order could not
    /// be read (H1-d: a failed read is a stop, never "not set").
    /// </summary>
    private bool TryExpectedCollection(HumanPuppetFacts facts, HumanPuppetKind puppetKind, out Guid? expected)
    {
        expected = null;
        foreach (var role in ExpectedCollectionOrder(facts.Customize!, puppetKind))
        {
            if (!penumbra.TryGetCollection(role, out var collection))
                return false;
            if (collection is { } set)
            {
                expected = set;
                return true;
            }
        }
        return true;
    }

    /// <summary>
    /// The resolver's order after the identifier step: body type (byte 2) 3 or 4
    /// gives the elderly or child type; otherwise the clan-and-gender type
    /// (byte 4, byte 1 + 1), then the gender type; then Default. isNpc is true
    /// unless the puppet has the player kind.
    /// </summary>
    internal static IReadOnlyList<HumanCollectionRole> ExpectedCollectionOrder(byte[] customize, HumanPuppetKind puppetKind)
    {
        ArgumentNullException.ThrowIfNull(customize);
        var isNpc = puppetKind != HumanPuppetKind.Player;
        var order = new List<HumanCollectionRole>();
        switch (customize[2])
        {
            case 3:
                order.Add(new(HumanCollectionRoleKind.NonPlayerElderly));
                break;
            case 4:
                order.Add(new(HumanCollectionRoleKind.NonPlayerChild));
                break;
            default:
                var gender = (byte)(customize[1] + 1);
                order.Add(new(HumanCollectionRoleKind.ClanGender, customize[4], gender, isNpc));
                order.Add(new(HumanCollectionRoleKind.Gender, 0, gender, isNpc));
                break;
        }
        order.Add(new(HumanCollectionRoleKind.Default));
        return order;
    }

    private static bool CustomizeFilled(HumanPuppetFacts facts) =>
        facts.Customize is { Length: HumanPuppetFacts.CustomizeLength } customize && customize[0] != 0;

    /// <summary>Latches the first stop, then hides every puppet and begins P13 for each.</summary>
    private HumanPuppetStopRecord StopAndClean(HumanPuppetStopRule rule, string step, string detail)
    {
        var stop = Latch(rule, step, detail);
        CleanLive("stop");
        return stop;
    }

    /// <summary>
    /// Begins P13 for every live puppet. Clean-up steps only latch, so this
    /// never re-enters. A puppet whose clean-up throws is left as residue (H1-d).
    /// </summary>
    private void CleanLive(string reason)
    {
        foreach (var puppet in puppets.Values.ToArray())
            if (puppet.Phase is HumanPuppetPhase.Hidden or HumanPuppetPhase.DrawRequested)
                OrResidue(puppet, reason, () => BeginRemoval(puppet, reason));
    }

    /// <summary>Runs one clean-up step; if a port throws, the puppet is recorded as residue and left alone.</summary>
    private void OrResidue(Puppet puppet, string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            puppet.Phase = HumanPuppetPhase.Residue;
            residue.Add(new(puppet.Handle, "a puppet whose clean-up did not finish", $"{step}: a port call threw ({exception.GetType().Name})"));
            Latch(HumanPuppetStopRule.S6Residue, step, "a port call threw during clean-up");
        }
    }

    /// <summary>
    /// H1-d: a port call that threw inside a host call. The stop latches as
    /// Provider, then every live puppet begins P13 (not while unloading: the
    /// unload's own clean-up does that after the drain).
    /// </summary>
    private HumanPuppetStopRecord Fault(string step, Exception exception)
    {
        var stop = Latch(HumanPuppetStopRule.Provider, step, $"a port call threw ({exception.GetType().Name})");
        bool cleanUpAllowed;
        lock (workGate)
            cleanUpAllowed = !unloading;
        if (cleanUpAllowed)
            CleanLive("fault");
        return stop;
    }

    private HumanPuppetOutcome Guarded(string step, Func<HumanPuppetOutcome> call)
    {
        try
        {
            return call();
        }
        catch (Exception exception)
        {
            return HumanPuppetOutcome.Stopped(Fault(step, exception));
        }
    }

    private HumanPuppetStopRecord Latch(HumanPuppetStopRule rule, string step, string detail)
    {
        Stop ??= new(rule, step, detail, update);
        return Stop;
    }

    /// <summary>Hides only an object that checks 1–5 still show is RSC's; otherwise forgets it, untouched.</summary>
    private bool HideIfOwned(Puppet puppet, string reason)
    {
        if (OwnershipMismatch(puppet) is { } mismatch)
        {
            Forget(puppet, $"{reason}: {mismatch}");
            return false;
        }
        // An object whose first read threw was never drawn, and its index is unknown: no write.
        if (!puppet.FactsBound)
            return true;
        backend.SetAlpha(puppet.Address, 0f);
        backend.DisableDraw(puppet.Address);
        return true;
    }

    /// <summary>Ownership was lost: nothing more is done to the object, and what RSC made is recorded.</summary>
    private void Forget(Puppet puppet, string reason)
    {
        puppet.Phase = HumanPuppetPhase.Residue;
        residue.Add(new(puppet.Handle, "an object RSC created, no longer provably its own", reason));
        if (puppet.Collection is not null || puppet.CollectionPending)
            residue.Add(new(puppet.Handle, "its temporary collection and any assignment", "left: removing them needs a by-index call on an unproved object"));
        Latch(HumanPuppetStopRule.S6Residue, "ownership", reason);
    }

    /// <summary>Inside M-1, before any assignment or change: hide and delete now (D2 is not engaged).</summary>
    private void AbortInCall(Puppet puppet, string reason)
    {
        if (OwnershipMismatch(puppet) is { } mismatch)
        {
            Forget(puppet, $"{reason}; {mismatch}");
            return;
        }
        backend.SetAlpha(puppet.Address, 0f);
        backend.DisableDraw(puppet.Address);
        puppet.Phase = HumanPuppetPhase.AwaitingDeletion;
        DeleteObject(puppet);
    }

    private void BeginRemoval(Puppet puppet, string reason)
    {
        if (!puppet.FactsBound)
        {
            // H1-d: its first read threw, so no copy ran and no map names its
            // slot. Its global index is unknown, so it gets no write (it may be
            // outside 200–439 and was never drawn); proved by its creation
            // slot only, it is deleted.
            puppet.Phase = HumanPuppetPhase.AwaitingDeletion;
            DeleteObject(puppet);
            return;
        }

        // 1–2. Hide, guarded by checks 1–5; then the identity (P5-6, P5-7).
        if (!HideIfOwned(puppet, reason))
            return;
        var verified = true;
        if (IdentityMismatch(puppet) is { } identity)
        {
            verified = false;
            Latch(HumanPuppetStopRule.S5Identity, "clean-up", identity);
        }

        // 3. Both maps; re-detach once on a mismatch. This candidate has no
        // by-index Glamourer call to withhold, and P14 is absent.
        var reading = Read(puppet.GlobalIndex, "P13 step 3");
        readings.Add(reading);
        if (!reading.Resolved && Detach(puppet, "P13 re-detach", readings) is { } detach)
        {
            verified = false;
            Latch(HumanPuppetStopRule.S4ParentMapOrIsolation, "P13 step 3", detach);
        }

        // SI-1 item 5: with the identity or a map unverified, no by-index call
        // is made. The assignment and the collection stay (deleting an assigned
        // collection would breach D1), and the hidden puppet is quarantined.
        if (!verified && (puppet.Collection is not null || puppet.CollectionPending || puppet.Assigned))
        {
            puppet.Phase = HumanPuppetPhase.Residue;
            residue.Add(new(puppet.Handle, "a hidden puppet with its temporary collection and any assignment",
                "left: the identity or a map was unverified, so no by-index call was made"));
            Latch(HumanPuppetStopRule.S6Residue, "P13 step 3", "the identity or a map was unverified with an assignment in place");
            return;
        }

        // 6–7. D1: the assignment before the collection.
        if (puppet.Collection is { } collection)
        {
            var assignmentGone = true;
            if (puppet.Assigned)
            {
                assignmentGone = penumbra.RemoveAssignment(collection, puppet.GlobalIndex);
                RecordChange(puppet);
                if (assignmentGone)
                    puppet.Assigned = false;
            }
            if (!assignmentGone)
            {
                residue.Add(new(puppet.Handle, "its temporary collection and assignment", "the assignment could not be removed, so D1 keeps the collection"));
                Latch(HumanPuppetStopRule.S6Residue, "P13 step 6", "the assignment could not be removed");
            }
            else
            {
                var deleted = penumbra.DeleteTemporaryCollection(collection);
                RecordChange(puppet);
                if (deleted)
                    puppet.Collection = null;
                else
                {
                    residue.Add(new(puppet.Handle, "its temporary collection", "deleting it failed"));
                    Latch(HumanPuppetStopRule.S6Residue, "P13 step 7", "the temporary collection could not be deleted");
                }
            }
        }

        // 8. D2: now, or on the first update strictly after t + 8.
        puppet.Phase = HumanPuppetPhase.AwaitingDeletion;
        if (DeletionAllowed(puppet))
            DeleteObject(puppet);
    }

    /// <summary>
    /// A change made between updates is recorded against the next update, the
    /// latest it can belong to, so D2 is never shortened.
    /// </summary>
    private void RecordChange(Puppet puppet) => puppet.LatestChangeUpdate = update + 1;

    private bool DeletionAllowed(Puppet puppet) =>
        puppet.LatestChangeUpdate is not { } change || update > change + DeletionDelayUpdates;

    /// <summary>Resolves the live index, proves checks 1–5 once more, and deletes exactly that object.</summary>
    private void DeleteObject(Puppet puppet)
    {
        if (OwnershipMismatch(puppet) is { } mismatch)
        {
            Forget(puppet, $"before deletion: {mismatch}");
            return;
        }
        backend.DeleteComObject(puppet.ComIndex);
        if (backend.ComObjectByIndex(puppet.ComIndex) == puppet.Address)
        {
            puppet.Phase = HumanPuppetPhase.Residue;
            residue.Add(new(puppet.Handle, "a hidden puppet", "deletion did not remove it"));
            Latch(HumanPuppetStopRule.S6Residue, "P13 step 8", "deletion did not remove the object");
            return;
        }
        puppet.Phase = HumanPuppetPhase.Deleted;
        puppet.DeletedUpdate = update;
    }

    private sealed class Puppet(HumanPuppetHandle handle, string name, ushort world, uint key, HumanPuppetKind kind, long providerGeneration)
    {
        public HumanPuppetHandle Handle { get; } = handle;
        public string Name { get; } = name;
        public ushort World { get; } = world;
        public uint Key { get; } = key;
        public HumanPuppetKind Kind { get; } = kind;
        public long ProviderGeneration { get; } = providerGeneration;
        public bool FactsBound { get; private set; }
        public nint Address { get; private set; }
        public int ComIndex { get; private set; }
        public int GlobalIndex { get; private set; }
        public ulong GameObjectId { get; private set; }
        public uint EntityId { get; private set; }
        public HumanPuppetPhase Phase { get; set; }
        public Guid? Collection { get; set; }
        public bool CollectionPending { get; set; }
        public bool Assigned { get; set; }
        public long? LatestChangeUpdate { get; set; }
        public long? DrawRequestedUpdate { get; set; }
        public long? DrawObservedUpdate { get; set; }
        public long? DeletedUpdate { get; set; }
        public bool SlotCheckedAfterDeletion { get; set; }

        /// <summary>R-8a: the kind and sub-kind read right after creation was proved, or null if unreadable.</summary>
        public HumanPuppetKindPair? CreatedKind { get; set; }

        public void BindCreation(nint address, int comIndex)
        {
            Address = address;
            ComIndex = comIndex;
        }

        public void BindFacts(HumanPuppetFacts facts)
        {
            GlobalIndex = facts.GlobalIndex;
            GameObjectId = facts.GameObjectId;
            EntityId = facts.EntityId;
            FactsBound = true;
        }

        public HumanPuppetSnapshot Snapshot() => new(Handle, Phase, GlobalIndex, Kind, Assigned, LatestChangeUpdate,
            DrawRequestedUpdate, DrawObservedUpdate, DeletedUpdate);
    }
}
#endif
