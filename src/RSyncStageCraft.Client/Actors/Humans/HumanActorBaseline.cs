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

    /// <summary>HV-4 only: <c>Character.HomeWorld</c> and nothing else, on RSC's own proven actor.</summary>
    void WriteHomeWorld(nint actor, ushort homeWorld);

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
/// <para>HV-3 adds one optional stage after HV-2's,
/// <see cref="HumanActorGlamourerStage"/>, used only together with it:
/// without it the sequence is HV-2's exactly. With it, Glamourer is probed
/// once the actor's Penumbra parent reads −1 and before the collection is
/// made; on the first update after the actor is visible, it is probed again
/// and one equipment-only state is applied and read back. Before every
/// Glamourer call the actor must be provably RSC's, keep its name and the
/// player's kind, have Penumbra parent −1, and be the only object with its
/// name. Removal, a stop and unload revert it, by index, before the Penumbra
/// release; a lost actor gets no Glamourer call.</para>
/// <para>HV-4 adds one optional stage after HV-3's,
/// <see cref="HumanActorCustomizePlusStage"/>, used only together with it:
/// without it the sequence is HV-3's exactly. With it, the actor's own
/// Customize+ parent entry is set to −1 and read back just before HV-3's early
/// probe; on the update after HV-3's readback one temporary profile is applied.
/// Before the Customize+ apply the HV-3 checks hold, and the
/// Customize+ parent reads −1, the home world is a real one and the name is
/// not the player's. Removal, a stop, a loss of ownership and unload delete it
/// by its receipt first, before the Glamourer revert.</para>
/// <para>HV-4 also initializes the actor's identity (r1c): before anything
/// is created, W-3's row (<see cref="HumanActorW3"/>) and the player's worlds
/// are read and checked; after the copies and their checks, and before the
/// placement, the draw and any provider call, <c>HomeWorld</c> alone is
/// written on the proven actor, ownership is checked again and the world,
/// name and kind are read back. Every Glamourer check (the early probe, the
/// apply and the revert) and the Customize+ apply then require W-3. Without
/// the Customize+ stage none of this happens.</para>
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
    private const string GlamourerPrefix = "glamourer: ";
    private const string CustomizePlusPrefix = "customize+: ";
    private const int NameAttempts = 8;
    private const float PlacementTolerance = 0.01f;

    private readonly IHumanActorBackend backend;
    private readonly Func<string?> sceneExit;
    private readonly Action<string> log;
    private readonly HumanActorPenumbraStage? penumbra;
    private readonly HumanActorGlamourerStage? glamourer;
    private readonly HumanActorCustomizePlusStage? customizePlus;
    private readonly IHumanActorWorldSource? world;
    private readonly string prefix;
    private Actor? actor;

    public HumanActorBaseline(IHumanActorBackend backend, Func<string?> sceneExit, Action<string> log)
        : this(backend, sceneExit, log, null, Prefix)
    {
    }

    /// <summary>HV-2: the same sequence with the Penumbra stage, logging under <paramref name="prefix"/>.</summary>
    public HumanActorBaseline(
        IHumanActorBackend backend, Func<string?> sceneExit, Action<string> log, HumanActorPenumbraStage? penumbra, string prefix)
        : this(backend, sceneExit, log, penumbra, null, prefix)
    {
    }

    /// <summary>HV-3: HV-2's sequence with the Glamourer stage after it (which needs the Penumbra stage).</summary>
    public HumanActorBaseline(
        IHumanActorBackend backend, Func<string?> sceneExit, Action<string> log, HumanActorPenumbraStage? penumbra,
        HumanActorGlamourerStage? glamourer, string prefix)
        : this(backend, sceneExit, log, penumbra, glamourer, null, null, prefix)
    {
    }

    /// <summary>
    /// HV-4: HV-3's sequence with the Customize+ stage after it (which needs
    /// the Glamourer stage) and W-3's identity initialization (which needs the
    /// world source; the source is accepted only with the Customize+ stage).
    /// </summary>
    public HumanActorBaseline(
        IHumanActorBackend backend, Func<string?> sceneExit, Action<string> log, HumanActorPenumbraStage? penumbra,
        HumanActorGlamourerStage? glamourer, HumanActorCustomizePlusStage? customizePlus, IHumanActorWorldSource? world, string prefix)
    {
        if (customizePlus is not null && glamourer is null)
            throw new ArgumentException("the Customize+ stage needs the Glamourer stage", nameof(customizePlus));
        if ((customizePlus is null) != (world is null))
            throw new ArgumentException("the world source comes with the Customize+ stage and only with it", nameof(world));
        this.customizePlus = customizePlus;
        this.world = world;
        if (glamourer is not null && penumbra is null)
            throw new ArgumentException("the Glamourer stage needs the Penumbra stage", nameof(glamourer));
        this.glamourer = glamourer;
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
        // HV-4 only: W-3's row, in English and in the client's language, and the player's worlds, read only, before anything is created.
        if (world is not null && WorldRefusal(world, player) is { } worldRefusal)
            return Refuse(worldRefusal);

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

        // 4b. HV-4 only: the home world alone, on the proven actor, after the copies and before the
        // placement, the draw and any provider call; ownership again, then the world, name and kind read back.
        if (world is not null)
        {
            if (Write(candidate, "home world", owned => backend.WriteHomeWorld(owned, HumanActorW3.WorldId)) is { } lostAtWorld)
                return Lost(lostAtWorld);
            if (OwnershipMismatch(candidate) is { } lostAfterWorld)
                return Lost("after the home world: " + lostAfterWorld);
            var worlded = backend.ReadFacts(address);
            if (worlded.HomeWorld != HumanActorW3.WorldId)
                return Halt("home world", "the home world did not read back", candidate);
            if (!string.Equals(worlded.Name, name, StringComparison.Ordinal) || !worlded.NameTerminated)
                return Halt("home world", "the name did not read back after the home world", candidate);
            if (worlded.RawKind != PlayerKind)
                return Halt("home world", "the kind is no longer the player's after the home world", candidate);
            // The value the copies left is reported (r1c's diagnosis). Only 0 and AnyWorld are logged as numbers:
            // a real world, the player's home or current one included, is never named.
            var left = copied.HomeWorld switch
            {
                0 => "0",
                ushort.MaxValue => "65535",
                _ when copied.HomeWorld == backend.ReadFacts(player).HomeWorld => "the player's world",
                _ => "another world",
            };
            log(prefix + $"home world: after the copies {left}, written {HumanActorW3.WorldId} (W-3), read back {worlded.HomeWorld}.");
        }

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
                        var probe = glamourer is null ? null
                            : customizePlus is null
                                ? new Func<string?>(() => glamourer.EarlyProbe(current.GlobalIndex, GlamourerChecks(current)))
                                : new Func<string?>(() => customizePlus.Detach(current.GlobalIndex, GlamourerChecks(current)) ??
                                    glamourer.EarlyProbe(current.GlobalIndex, GlamourerChecks(current)));
                        if (penumbra.Apply(current.GlobalIndex, player, probe) is { } refused)
                        {
                            if (refused.StartsWith(CustomizePlusPrefix, StringComparison.Ordinal))
                                Halt("customize+", refused[CustomizePlusPrefix.Length..], current);
                            else if (refused.StartsWith(GlamourerPrefix, StringComparison.Ordinal))
                                Halt("glamourer", refused[GlamourerPrefix.Length..], current);
                            else
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
            case HumanActorPhase.Visible:
                // HV-3: once, on the first update after the actor is visible.
                if (glamourer is not null && !current.GlamourerTried)
                {
                    current.GlamourerTried = true;
                    if (glamourer.Apply(current.GlobalIndex, GlamourerChecks(current)) is { } refusedApply)
                        Halt("glamourer", refusedApply.StartsWith(GlamourerPrefix, StringComparison.Ordinal)
                            ? refusedApply[GlamourerPrefix.Length..] : refusedApply, current);
                }
                // HV-4: once, on the update after HV-3's state was applied and read back.
                else if (customizePlus is not null && !current.CustomizePlusTried && glamourer?.State == HumanGlamourerState.Applied)
                {
                    current.CustomizePlusTried = true;
                    if (customizePlus.Apply(current.GlobalIndex, CustomizePlusChecks(current)) is { } refusedProfile)
                        Halt("customize+", refusedProfile.StartsWith(CustomizePlusPrefix, StringComparison.Ordinal)
                            ? refusedProfile[CustomizePlusPrefix.Length..] : refusedProfile, current);
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
        (penumbra is null ? "." : $", temporary collection {(penumbra.Holding ? "held" : "none")}, collection residue {penumbra.Residue}" +
            (glamourer is null ? "." : $", {glamourer.Status()}" + (customizePlus is null ? "." : $", {customizePlus.Status()}.")));

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

    /// <summary>
    /// HV-3, read-only, immediately before every Glamourer call: checks 1–5,
    /// the name and the player's kind still read back, Penumbra parent −1 (an
    /// index in the cutscene range otherwise resolves to its parent), and no
    /// other object carries the name (Glamourer acts on every object with the
    /// actor's identity). Null when all hold.
    /// </summary>
    private string? GlamourerChecks(Actor owned)
    {
        if (OwnershipMismatch(owned) is { } lost)
            return lost;
        var facts = backend.ReadFacts(owned.Address);
        if (!string.Equals(facts.Name, owned.Name, StringComparison.Ordinal) || !facts.NameTerminated)
            return "the actor's name no longer reads back";
        if (facts.RawKind != PlayerKind)
            return "the actor's kind is no longer the player's";
        if (world is not null && HomeWorldRefusal(facts.HomeWorld) is { } wrongWorld)
            return wrongWorld;
        if (penumbra is null || !penumbra.ParentDetached(owned.GlobalIndex))
            return "the actor's Penumbra parent does not read -1";
        var length = backend.ObjectTableLength;
        for (var index = 0; index < length; index++)
        {
            if (index != owned.GlobalIndex && backend.ScanEntry(index) is { } entry &&
                string.Equals(entry.Name, owned.Name, StringComparison.OrdinalIgnoreCase))
                return "another object carries the actor's name";
        }
        return null;
    }

    /// <summary>
    /// HV-4, read-only, immediately before the Customize+ apply: HV-3's checks,
    /// then the actor's own Customize+ parent reads −1 (Customize+ keeps its own
    /// copy-parent table), its home world is neither 0 nor AnyWorld (65535,
    /// which matches every world), and its name is not the player's. Null when
    /// all hold.
    /// </summary>
    private string? CustomizePlusChecks(Actor owned)
    {
        if (GlamourerChecks(owned) is { } failed)
            return failed;
        if (customizePlus is null || !customizePlus.ParentDetached(owned.GlobalIndex))
            return "the actor's Customize+ parent does not read -1";
        var facts = backend.ReadFacts(owned.Address);
        // Defence in depth: GlamourerChecks (above, with the world source always present here) refuses these first.
        if (facts.HomeWorld is 0 or ushort.MaxValue)
            return $"the actor's home world reads {facts.HomeWorld}, which is not a real world";
        if (facts.HomeWorld != HumanActorW3.WorldId)
            return "the actor's home world is not the selected world";
        var player = backend.ReadFacts(backend.LocalPlayer);
        if (player.Name is null || string.Equals(player.Name, owned.Name, StringComparison.OrdinalIgnoreCase))
            return "the actor's name cannot be told apart from the player's";
        if (player.HomeWorld == facts.HomeWorld)
            return "the actor's home world is the player's";
        return null;
    }

    /// <summary>HV-4: the actor's home world must read W-3. Null when it does; 0 and AnyWorld are named, any other world is not.</summary>
    private static string? HomeWorldRefusal(ushort homeWorld) => homeWorld switch
    {
        0 or ushort.MaxValue => $"the actor's home world reads {homeWorld}, which is not a real world",
        HumanActorW3.WorldId => null,
        _ => "the actor's home world is not the selected world",
    };

    /// <summary>
    /// HV-4, read-only, before anything is created: W-3's row in English and in
    /// the client's language, then the player's home and current worlds. Null
    /// when all hold; a read that throws refuses.
    /// </summary>
    private string? WorldRefusal(IHumanActorWorldSource source, nint player)
    {
        try
        {
            return HumanActorW3.RowMismatch(source.EnglishRow(HumanActorW3.WorldId)) ??
                HumanActorW3.ClientRowMismatch(source.ClientRow(HumanActorW3.WorldId)) ??
                HumanActorW3.PlayerWorldMismatch(backend.ReadFacts(player).HomeWorld, source.PlayerCurrentWorld());
        }
        catch (Exception exception)
        {
            return $"the World row or the player's worlds could not be read ({exception.GetType().Name})";
        }
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
        // HV-4: the Customize+ profile goes first, by its receipt: last applied, first removed.
        RemoveCustomizePlus();
        // HV-3: the Glamourer state is reverted while the actor still exists, before the Penumbra release.
        if (glamourer?.Revert(owned.GlobalIndex, GlamourerChecks(owned)) is { } glamourerProblem)
            Stop ??= glamourerProblem;
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
                RemoveCustomizePlus();
                glamourer?.Abandon(lost);
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
        // HV-4: the receipt names RSC's profile, not the object, so it is still deleted by it.
        RemoveCustomizePlus();
        // HV-3: no Glamourer call: the index may now hold another object.
        glamourer?.Abandon(why);
        // The collection is RSC's by its id whatever became of the object; the object itself is not read.
        ReleasePenumbra(-1);
        log(prefix + $"STOP: the actor is no longer provably RSC's ({why}); it is not touched again.");
        return false;
    }

    /// <summary>HV-4: the stage's deletion by receipt; a problem latches a stop (the cleanup goes on).</summary>
    private void RemoveCustomizePlus()
    {
        if (customizePlus?.Remove() is { } problem)
            Stop ??= problem;
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
        public bool GlamourerTried { get; set; }
        public bool CustomizePlusTried { get; set; }
    }
}
#endif
