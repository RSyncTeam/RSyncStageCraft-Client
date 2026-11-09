#if RSC_DEVELOPMENT
using System.Numerics;

namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>Where an object stands and which way it faces (radians, the game's convention).</summary>
public readonly record struct HumanActorPlacement(Vector3 Position, float Rotation);

/// <summary>
/// HV-1's native primitives: one regular, visible, client-only human. Every
/// call is made on the game thread and every decision is
/// <see cref="HumanActorBaseline"/>'s. Nothing here sends anything to the game
/// server, calls another plugin or writes the object kind. The only
/// implementation is the plugin's Debug-only HV-1 adapter.
/// </summary>
public interface IHumanActorBackend
{
    bool IsAvailable { get; }

    /// <summary>Every raw COM entry (index 0–248), read without writing, immediately before creating.</summary>
    nint[] ComEntriesSnapshot();

    /// <summary><c>CreateBattleCharacter()</c> with its default arguments: the COM index, or −1.</summary>
    int CreateCharacter();

    nint ComEntry(int comIndex);

    nint ComObjectByIndex(int comIndex);

    int ComIndexOf(nint address);

    nint ObjectTableEntry(int globalIndex);

    nint LocalPlayer { get; }

    int ObjectTableLength { get; }

    /// <summary>The name at a global index (null for an empty or nameless slot).</summary>
    HumanPuppetScanEntry? ScanEntry(int globalIndex);

    HumanPuppetFacts ReadFacts(nint address);

    /// <summary>The name buffer only (the name and its NUL).</summary>
    void WriteName(nint actor, string name);

    /// <summary>
    /// <c>CharacterSetup.CopyFromCharacter</c>: the position flag (which
    /// includes the rotation) from another object; no flag when
    /// <paramref name="source"/> is the actor itself (the self-copy).
    /// </summary>
    void CopyCharacter(nint actor, nint source);

    HumanActorPlacement? ReadPlacement(nint address);

    /// <summary>Position, default position, rotation and default rotation.</summary>
    void Place(nint actor, HumanActorPlacement placement);

    /// <summary>Clears the targetable bit and hides the nameplate, as RSC's other actors are.</summary>
    void MakeUntargetable(nint actor);

    void SetAlpha(nint actor, float alpha);

    bool IsReadyToDraw(nint actor);

    void EnableDraw(nint actor);

    void DisableDraw(nint actor);

    /// <summary><c>DeleteObjectByIndex(comIndex, 0)</c>.</summary>
    void DeleteComObject(int comIndex);
}

/// <summary>Where HV-1's one actor is.</summary>
public enum HumanActorPhase
{
    None,
    WaitingForReady,
    WaitingForModel,

    /// <summary>HV-2 only: drawn at alpha 0, its temporary collection assigned, undrawn; waiting for the draw object to go.</summary>
    WaitingForRelease,

    /// <summary>HV-2 only: no human draw object seen after the undraw, and the draw requested again at alpha 0; waiting for a new one.</summary>
    WaitingForRebuild,
    Visible,
}

/// <summary>
/// HV-1 (Debug only): one regular, visible, client-only human actor, created
/// and deleted correctly, with no Penumbra, Glamourer or Customize+ call and
/// no IVCS dependency. The sequence follows the game's own initialization as
/// a posing tool's complete spawn does it, with RSC's ownership proof and
/// invisible construction from its working monster path:
/// <list type="number">
/// <item>a synthetic name (<see cref="HumanPuppetName"/>) that no object in
/// the table carries;</item>
/// <item><c>CreateBattleCharacter()</c> and the creation proof;</item>
/// <item>alpha 0, then the name, read back, before anything else;</item>
/// <item>the copy from the local player, then the self-copy; the object kind
/// is whatever the copies leave and is never written: the player's kind is
/// required (the copy's own result), and anything else stops;</item>
/// <item>placed two yalms in front of the player, facing them;
/// untargetable;</item>
/// <item>on later updates: ready to draw, then the draw (still alpha 0), then
/// a human draw object, then alpha 1;</item>
/// <item>removal: alpha 0, the draw disabled, the COM slot deleted and read back.</item>
/// </list>
/// Checks 1–5 (the COM slot, the object-table entry, the global index, the
/// object and entity ids, not the player) precede every write after
/// creation. An object that is no longer provably this actor is forgotten
/// and never touched again. A stop deletes the actor if it is still provably
/// RSC's and refuses every later spawn until unload: nothing is retried.
/// <para>HV-2 adds one optional stage, <see cref="HumanActorPenumbraStage"/>:
/// without it the sequence is HV-1's exactly. With it, the actor stays at
/// alpha 0 after its first draw while the stage assigns its temporary
/// collection. RSC then redraws the actor itself with HV-1's own primitives,
/// not Penumbra's redraw: the draw disabled, the human draw object seen gone,
/// ready to draw again, the draw enabled at alpha 0, a new human draw object
/// (built while the collection is assigned). Alpha 1 follows only once the
/// stage has confirmed the rebuilt object's resources. Nothing is queued in
/// Penumbra. On removal, a stop, a loss of ownership and unload,
/// the stage's mod and collection are removed before the actor is undrawn
/// and deleted.</para>
/// </summary>
public sealed class HumanActorBaseline
{
    /// <summary>Updates to wait for the game to report the actor ready to draw (about 5 s at 60 fps).</summary>
    public const int ReadyLimit = 300;

    /// <summary>Updates to wait for the human draw object after the draw.</summary>
    public const int ModelLimit = 300;

    /// <summary>HV-2: updates to wait for the draw object to be released after the undraw, and for the new one after the draw.</summary>
    public const int RedrawLimit = 300;

    /// <summary>
    /// HV-2: updates after the new human draw object first appears during
    /// which packaged redirects not yet reported are read again (about 1 s at
    /// 60 fps). A path resolved elsewhere refuses at once.
    /// </summary>
    public const int SettleLimit = 60;

    /// <summary>The draw is never requested on the spawn's own update, as in the posing tool's sequence.</summary>
    public const int FirstReadyCheck = 2;

    public const float Distance = 2f;

    private const int PlayerKind = 1;
    private const int NameAttempts = 8;
    private const float PlacementTolerance = 0.01f;

    private readonly IHumanActorBackend backend;
    private readonly Func<string?> sceneExit;
    private readonly Action<string> log;
    private readonly HumanActorPenumbraStage? penumbra;
    private readonly string prefix;
    private Actor? actor;

    public HumanActorBaseline(IHumanActorBackend backend, Func<string?> sceneExit, Action<string> log)
        : this(backend, sceneExit, log, null, Prefix)
    {
    }

    /// <summary>HV-2: the same sequence with the Penumbra stage, logging under <paramref name="prefix"/>.</summary>
    public HumanActorBaseline(
        IHumanActorBackend backend, Func<string?> sceneExit, Action<string> log, HumanActorPenumbraStage? penumbra, string prefix)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.sceneExit = sceneExit ?? throw new ArgumentNullException(nameof(sceneExit));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.penumbra = penumbra;
        this.prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
    }

    /// <summary>Test seam: where names come from (production: <see cref="HumanPuppetName.Create"/>).</summary>
    internal Func<IReadOnlySet<char>, string> NameSource { get; set; } = HumanPuppetName.Create;

    public const string Prefix = "RSC HV-1 ";

    public HumanActorPhase Phase => actor?.Phase ?? HumanActorPhase.None;

    /// <summary>The stop that latched, or null. Every later spawn is refused.</summary>
    public string? Stop { get; private set; }

    /// <summary>Objects that were RSC's but are no longer provably so: never touched again.</summary>
    public int Residue { get; private set; }

    /// <summary>1 while an actor exists (any phase), else 0.</summary>
    public int LiveCount => actor is null ? 0 : 1;

    /// <summary>The spawn, on the game thread. True when the actor was built and is waiting to draw.</summary>
    public bool Spawn()
    {
        if (Stop is not null)
            return Refuse("an earlier step stopped; nothing is retried until unload");
        if (actor is not null)
            return Refuse("an actor already exists; remove it first");
        if (!backend.IsAvailable)
            return Refuse("the client-object functions are unavailable on this game version");
        if (Safe(sceneExit) is { } exit)
            return Refuse(exit);
        if (penumbra is not null && !penumbra.Available)
            return Refuse("Penumbra is unavailable or incompatible");
        var player = backend.LocalPlayer;
        if (player == 0 || backend.ReadPlacement(player) is not { } beside)
            return Refuse("the local player cannot be read");

        // 1. A name no object in the table carries (ignoring case and world).
        var name = FreshName();
        if (name is null)
            return Refuse("no unused synthetic name was drawn");

        // 2. Create, and prove the exact object is new and RSC's.
        var before = backend.ComEntriesSnapshot();
        var comIndex = backend.CreateCharacter();
        if (comIndex < 0 || comIndex >= before.Length)
            return Halt("creation", "the allocator returned no client-object slot", null);
        var address = backend.ComObjectByIndex(comIndex);
        var isNew = address != 0 && address != player && Array.IndexOf(before, address) < 0 && before[comIndex] == 0 &&
            backend.ComIndexOf(address) == comIndex && backend.ComEntry(comIndex) == address;
        if (!isNew)
        {
            // Not provably RSC's: never written or deleted.
            Residue += address == 0 ? 0 : 1;
            return Halt("creation", "the created object could not be proved new", null);
        }
        var created = backend.ReadFacts(address);
        var candidate = new Actor(address, comIndex, created.GlobalIndex, created.GameObjectId, created.EntityId, name);
        if (!HumanPuppetSlotRange.Contains(created.GlobalIndex) || backend.ObjectTableEntry(created.GlobalIndex) != address)
        {
            // Proved new in its COM slot, so deleted there, unwritten.
            backend.DeleteComObject(comIndex);
            return Halt("creation", $"the created object is outside global indices {HumanPuppetSlotRange.MinimumGlobalIndex}–" +
                $"{HumanPuppetSlotRange.MaximumGlobalIndex} or not in the object table; deleted unwritten", null);
        }
        actor = candidate;

        // 3. Invisible, then named, before the copies and before any draw.
        if (Write(candidate, "alpha", owned => backend.SetAlpha(owned, 0f)) is { } lost)
            return Lost(lost);
        if (Write(candidate, "name", owned => backend.WriteName(owned, name)) is { } lostAtName)
            return Lost(lostAtName);
        var named = backend.ReadFacts(address);
        if (!string.Equals(named.Name, name, StringComparison.Ordinal) || !named.NameTerminated)
            return Halt("name", "the name did not read back", candidate);

        // 4. The game's own initialization: the copy from the player, then the self-copy.
        if (Write(candidate, "copy", owned => backend.CopyCharacter(owned, player)) is { } lostAtCopy)
            return Lost(lostAtCopy);
        if (Write(candidate, "self-copy", owned => backend.CopyCharacter(owned, owned)) is { } lostAtSelfCopy)
            return Lost(lostAtSelfCopy);
        if (OwnershipMismatch(candidate) is { } lostAfterCopies)
            return Lost(lostAfterCopies);
        var copied = backend.ReadFacts(address);
        var customizeFilled = copied.Customize is { Length: HumanPuppetFacts.CustomizeLength } bytes && bytes[0] != 0;
        log(prefix + $"after the copies: kind {ActorObjectKinds.Describe(copied.RawKind)} (created {ActorObjectKinds.Describe(created.RawKind)}), " +
            $"name kept {(string.Equals(copied.Name, name, StringComparison.Ordinal) && copied.NameTerminated ? "yes" : "no")}, " +
            $"customize filled {customizeFilled}, human model {copied.HasHumanModel} (no draw object before the draw).");
        if (copied.RawKind != PlayerKind)
            return Halt("copy", $"the copies left kind {ActorObjectKinds.Describe(copied.RawKind)}, not the player's kind; the kind is never written", candidate);
        if (!string.Equals(copied.Name, name, StringComparison.Ordinal) || !copied.NameTerminated)
            return Halt("copy", "the copies changed the name", candidate);
        if (!customizeFilled)
            return Halt("copy", "the copies left no appearance", candidate);

        // 5. Beside the player, facing them; untargetable.
        var placement = InFrontOf(beside);
        if (Write(candidate, "placement", owned => backend.Place(owned, placement)) is { } lostAtPlacement)
            return Lost(lostAtPlacement);
        if (backend.ReadPlacement(address) is not { } placed ||
            Vector3.Distance(placed.Position, placement.Position) > PlacementTolerance ||
            MathF.Abs(placed.Rotation - placement.Rotation) > PlacementTolerance)
            return Halt("placement", "the placement did not read back", candidate);
        if (Write(candidate, "untargetable", owned => backend.MakeUntargetable(owned)) is { } lostAtTargetable)
            return Lost(lostAtTargetable);
        if (backend.ReadFacts(address).Targetable)
            return Halt("untargetable", "the actor is still targetable", candidate);

        candidate.Phase = HumanActorPhase.WaitingForReady;
        log(prefix + $"spawned at global index {candidate.GlobalIndex}: named, player kind, placed beside you, untargetable, " +
            "alpha 0; waiting to draw.");
        return true;
    }

    /// <summary>Every framework update, on the game thread.</summary>
    public void OnFrameworkUpdate()
    {
        if (actor is not { } current)
            return;
        current.Updates++;
        if (OwnershipMismatch(current) is { } lost)
        {
            Lost(lost);
            return;
        }
        switch (current.Phase)
        {
            case HumanActorPhase.WaitingForReady:
                if (current.Updates >= FirstReadyCheck && backend.IsReadyToDraw(current.Address))
                {
                    backend.SetAlpha(current.Address, 0f);
                    backend.EnableDraw(current.Address);
                    current.DrawUpdate = current.Updates;
                    current.Phase = HumanActorPhase.WaitingForModel;
                    log(prefix + $"ready to draw after {current.Updates} update(s); draw requested at alpha 0.");
                }
                else if (current.Updates > ReadyLimit)
                {
                    Halt("draw", $"not ready to draw within {ReadyLimit} updates", current);
                }
                break;
            case HumanActorPhase.WaitingForModel:
                if (backend.ReadFacts(current.Address).HasHumanModel)
                {
                    if (penumbra is not null)
                    {
                        log(prefix + $"drawn: human model after {current.Updates - current.DrawUpdate} update(s), still alpha 0.");
                        var player = backend.ReadFacts(backend.LocalPlayer).GlobalIndex;
                        if (player < 0 || player == current.GlobalIndex)
                        {
                            Halt("penumbra", "the local player's index cannot be read", current);
                            break;
                        }
                        if (penumbra.Apply(current.GlobalIndex, player) is { } refused)
                        {
                            Halt("penumbra", refused, current);
                            break;
                        }
                        if (OwnershipMismatch(current) is { } lostAtApply)
                        {
                            Lost(lostAtApply);
                            break;
                        }
                        // RSC's own redraw: the first draw object was built before the
                        // assignment, so it is released and a new one is built under it.
                        backend.DisableDraw(current.Address);
                        current.UndrawUpdate = current.Updates;
                        current.Released = false;
                        current.Phase = HumanActorPhase.WaitingForRelease;
                        break;
                    }
                    backend.SetAlpha(current.Address, 1f);
                    current.Phase = HumanActorPhase.Visible;
                    log(prefix + $"visible: human model after {current.Updates - current.DrawUpdate} update(s), alpha 1.");
                }
                else if (current.Updates - current.DrawUpdate > ModelLimit)
                {
                    Halt("draw", $"no human draw object within {ModelLimit} updates of the draw", current);
                }
                break;
            case HumanActorPhase.WaitingForRelease:
                if (!current.Released && !backend.ReadFacts(current.Address).HasHumanModel)
                    current.Released = true;
                if (current.Released && current.Updates - current.UndrawUpdate >= FirstReadyCheck && backend.IsReadyToDraw(current.Address))
                {
                    backend.SetAlpha(current.Address, 0f);
                    backend.EnableDraw(current.Address);
                    current.RebuildUpdate = current.Updates;
                    current.RebuiltUpdate = -1;
                    current.Phase = HumanActorPhase.WaitingForRebuild;
                    log(prefix + $"undrawn: no human draw object after the undraw, ready again after {current.Updates - current.UndrawUpdate} " +
                        "update(s); draw requested again at alpha 0.");
                }
                else if (current.Updates - current.UndrawUpdate > RedrawLimit)
                {
                    Halt("redraw", current.Released
                        ? $"not ready to draw again within {RedrawLimit} updates of the undraw"
                        : "a human draw object was still reported after the undraw; the rebuild cannot be proved", current);
                }
                break;
            case HumanActorPhase.WaitingForRebuild:
                if (backend.ReadFacts(current.Address).HasHumanModel)
                {
                    var stage = penumbra!;
                    if (current.RebuiltUpdate < 0)
                        current.RebuiltUpdate = current.Updates;
                    if (stage.Confirm(out var settling) is { } unconfirmed)
                    {
                        // Only redirects not yet reported are read again, within the window.
                        if (settling && current.Updates - current.RebuiltUpdate < SettleLimit)
                            break;
                        Halt("redraw", unconfirmed, current);
                        break;
                    }
                    if (OwnershipMismatch(current) is { } lostAtRebuild)
                    {
                        Lost(lostAtRebuild);
                        break;
                    }
                    backend.SetAlpha(current.Address, 1f);
                    current.Phase = HumanActorPhase.Visible;
                    log(prefix + $"visible: redrawn after {current.Updates - current.UndrawUpdate} update(s), alpha 1.");
                }
                else if (current.Updates - current.RebuildUpdate > RedrawLimit)
                {
                    Halt("redraw", $"no new human draw object within {RedrawLimit} updates of the draw", current);
                }
                break;
        }
    }

    /// <summary>Removes the actor, on the game thread. True when it was deleted and its slot read back empty.</summary>
    public bool Remove()
    {
        if (actor is not { } current)
            return Refuse("there is no actor to remove");
        if (OwnershipMismatch(current) is { } lost)
            return Lost(lost);
        var empty = Delete(current);
        log(prefix + $"removed: deleted, slot empty {(empty ? "yes" : "no")}.");
        if (!empty)
            Stop ??= "the slot did not read back empty after the delete";
        return empty;
    }

    /// <summary>Plugin unload, on the game thread: the same removal; a lost actor is only reported.</summary>
    public void Unload()
    {
        if (actor is not { } current)
        {
            ReleasePenumbra(-1);
            log(prefix + $"unload: no actor, residue {Residue}.");
            return;
        }
        if (OwnershipMismatch(current) is { } lost)
        {
            Lost(lost);
            log(prefix + $"unload: residue {Residue}.");
            return;
        }
        var empty = Delete(current);
        log(prefix + $"unload: deleted, slot empty {(empty ? "yes" : "no")}, residue {Residue}.");
    }

    public string Status() => prefix + $"status: {Phase}, stop {(Stop is null ? "none" : "latched")}, residue {Residue}" +
        (penumbra is null ? "." : $", temporary collection {(penumbra.Holding ? "held" : "none")}, collection residue {penumbra.Residue}.");

    /// <summary>Two yalms along the player's facing, turned to face the player.</summary>
    public static HumanActorPlacement InFrontOf(HumanActorPlacement player)
    {
        var forward = new Vector3(MathF.Sin(player.Rotation), 0f, MathF.Cos(player.Rotation));
        var facing = player.Rotation + MathF.PI;
        if (facing > MathF.PI)
            facing -= 2 * MathF.PI;
        return new(player.Position + forward * Distance, facing);
    }

    private string? FreshName()
    {
        for (var attempt = 0; attempt < NameAttempts; attempt++)
        {
            var name = NameSource(new HashSet<char>());
            if (!HumanPuppetName.IsWellFormed(name))
                throw new InvalidOperationException("the name source returned a name outside P2");
            if (!Carried(name))
                return name;
        }
        return null;
    }

    /// <summary>The collision scan: any object in the table with the name, ignoring case and world.</summary>
    private bool Carried(string name)
    {
        var length = backend.ObjectTableLength;
        for (var index = 0; index < length; index++)
        {
            if (backend.ScanEntry(index) is { } entry && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Checks 1–5, read-only: null while the object is still exactly this actor.</summary>
    private string? OwnershipMismatch(Actor owned)
    {
        if (backend.ComObjectByIndex(owned.ComIndex) != owned.Address || backend.ComIndexOf(owned.Address) != owned.ComIndex)
            return "its client-object slot no longer holds it";
        if (backend.ObjectTableEntry(owned.GlobalIndex) != owned.Address || owned.Address == backend.LocalPlayer)
            return "the object table no longer holds it at its index";
        var facts = backend.ReadFacts(owned.Address);
        if (facts.GlobalIndex != owned.GlobalIndex || facts.GameObjectId != owned.GameObjectId || facts.EntityId != owned.EntityId)
            return "its object or entity id changed";
        return null;
    }

    /// <summary>One write, immediately preceded by checks 1–5. Returns why ownership was lost, or null after writing.</summary>
    private string? Write(Actor owned, string step, Action<nint> write)
    {
        if (OwnershipMismatch(owned) is { } lost)
            return $"before the {step}: {lost}";
        write(owned.Address);
        return null;
    }

    private bool Delete(Actor owned)
    {
        backend.SetAlpha(owned.Address, 0f);
        // HV-2: the mod and the collection go before the actor they apply to.
        ReleasePenumbra(owned.GlobalIndex);
        backend.DisableDraw(owned.Address);
        backend.DeleteComObject(owned.ComIndex);
        actor = null;
        return backend.ComObjectByIndex(owned.ComIndex) != owned.Address && backend.ComEntry(owned.ComIndex) != owned.Address;
    }

    private bool Refuse(string why)
    {
        log(prefix + "refused: " + why + ".");
        return false;
    }

    /// <summary>A stop: deletes the actor while it is still provably RSC's, latches, and is never retried.</summary>
    private bool Halt(string step, string why, Actor? owned)
    {
        Stop = $"{step}: {why}";
        var deleted = "no actor held";
        if (owned is not null)
        {
            if (OwnershipMismatch(owned) is { } lost)
            {
                actor = null;
                Residue++;
                ReleasePenumbra(-1);
                deleted = "not deleted: " + lost;
            }
            else
            {
                deleted = Delete(owned) ? "deleted, slot empty yes" : "deleted, slot empty no";
            }
        }
        log(prefix + $"STOP at {step}: {why}; {deleted}.");
        return false;
    }

    /// <summary>The object is no longer provably this actor: forgotten, never written, hidden or deleted again.</summary>
    private bool Lost(string why)
    {
        actor = null;
        Residue++;
        Stop ??= "ownership: " + why;
        // The collection is RSC's by its id whatever became of the object; the object itself is not read.
        ReleasePenumbra(-1);
        log(prefix + $"STOP: the actor is no longer provably RSC's ({why}); it is not touched again.");
        return false;
    }

    /// <summary>HV-2: the stage's release; a problem latches a stop (the actor is still deleted).</summary>
    private void ReleasePenumbra(int actorIndex)
    {
        if (penumbra?.Release(actorIndex) is { } problem)
            Stop ??= "penumbra release: " + problem;
    }

    private static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            return $"the scene could not be read ({exception.GetType().Name})";
        }
    }

    private sealed class Actor(nint address, int comIndex, int globalIndex, ulong gameObjectId, uint entityId, string name)
    {
        public nint Address { get; } = address;
        public int ComIndex { get; } = comIndex;
        public int GlobalIndex { get; } = globalIndex;
        public ulong GameObjectId { get; } = gameObjectId;
        public uint EntityId { get; } = entityId;
        public string Name { get; } = name;
        public HumanActorPhase Phase { get; set; }
        public int Updates { get; set; }
        public int DrawUpdate { get; set; }
        public int UndrawUpdate { get; set; }
        public bool Released { get; set; }
        public int RebuildUpdate { get; set; }
        public int RebuiltUpdate { get; set; } = -1;
    }
}
#endif
