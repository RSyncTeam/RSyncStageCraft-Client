#if RSC_DEVELOPMENT
using System.Text;
using System.Text.Json;

namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// HV-3's whole Glamourer surface (the pinned Glamourer.Api 2.8.2
/// subscribers), by object index only. There is no by-name call, no
/// <c>DeletePlayerState</c>, no lock, no design application and no event
/// subscription. Every call is made on the game thread. A provider that does
/// not answer throws; the stage treats every throw after an apply was
/// requested as an uncertain state. The only implementation is the plugin's
/// Debug-only HV-3 adapter.
/// </summary>
public interface IHumanActorGlamourer
{
    /// <summary><c>Glamourer.ApiVersion.V2</c>, or null.</summary>
    (int Major, int Minor)? ApiVersion { get; }

    /// <summary>The installed, loaded Glamourer plugin's version (read only), or null.</summary>
    string? GlamourerVersion { get; }

    /// <summary>The installed, loaded Penumbra plugin's version (read only), or null.</summary>
    string? PenumbraVersion { get; }

    /// <summary>Changes whenever any plugin loads or unloads.</summary>
    long Generation { get; }

    /// <summary><c>Glamourer.UnlockState(index, key)</c>: the answer code.</summary>
    int UnlockState(int objectIndex, uint key);

    /// <summary><c>Glamourer.ApplyState(base64, index, key, flags)</c>: the answer code.</summary>
    int ApplyState(string base64State, int objectIndex, uint key, ulong flags);

    /// <summary><c>Glamourer.GetState(index, key)</c>: the code, and each armour or accessory slot's item id (null when unanswered).</summary>
    (int Code, IReadOnlyDictionary<string, ulong>? Items) GetState(int objectIndex, uint key);

    /// <summary>
    /// HV-5 M2: the same <c>Glamourer.GetState(index, key)</c> read, as the
    /// whole state's compact JSON text (null unless Success), for the readback
    /// against a catalogue entry's stored reference. Read only.
    /// </summary>
    (int Code, string? Json) GetStateJson(int objectIndex, uint key);

    /// <summary><c>Glamourer.RevertState(index, key, flags)</c>: the answer code.</summary>
    int RevertState(int objectIndex, uint key, ulong flags);

    /// <summary><c>Glamourer.CanUnlock(index, key)</c>.</summary>
    (int Code, bool IsLocked, bool CanUnlock) CanUnlock(int objectIndex, uint key);
}

/// <summary>Where HV-3's one Glamourer state stands.</summary>
public enum HumanGlamourerState
{
    /// <summary>No Glamourer state was created by RSC.</summary>
    None,

    /// <summary>Applied and read back.</summary>
    Applied,

    /// <summary>An apply was requested and its outcome is not known: a state may exist.</summary>
    Uncertain,

    /// <summary>Reverted by index and read back as not locked: the reported residue of a clean run.</summary>
    Reverted,

    /// <summary>Applied or uncertain, then left without a revert (ownership, a changed provider, or a failed revert).</summary>
    Left,
}

/// <summary>
/// HV-3 (Debug only): one Glamourer state for HV-2's actor, equipment only,
/// by object index only. Called by <see cref="HumanActorBaseline"/> only.
/// <list type="number">
/// <item>The gate: Glamourer API (1, ≥8), Glamourer exactly 1.7.1.3 and
/// Penumbra exactly 1.7.2.1, the builds whose source this stage was checked
/// against; the plugin generation is recorded.</item>
/// <item>The early probe, before HV-2 adds its mod: <c>UnlockState</c> must
/// answer NothingDone (no state for the actor's identity). It changes nothing:
/// the key is this spawn's, and RSC never locks.</item>
/// <item>After HV-2 has shown the actor: the probe again, then
/// <c>ApplyState</c> (flags 0x02: equipment only, no lock, no Once), then
/// <c>GetState</c>, back to back. RSC makes no other call between them and
/// subscribes to nothing, so no RSC code runs in between; Glamourer's own
/// events and other plugins' handlers run inside these calls, and their effect
/// is caught by the readback.</item>
/// <item>Cleanup, before the Penumbra release: <c>RevertState</c> (0x02) and
/// <c>CanUnlock</c>. The reverted state is left in Glamourer's memory, unlocked
/// and with its equipment sources back to the game's; it is never deleted.</item>
/// </list>
/// Any state that is applied, uncertain or left is reported as a cleanup
/// failure, never a clean pass. Nothing here deletes, unlocks by name or
/// reloads anything.
/// <para>HV-5 M2 (BC-5, owner decisions D8 b, D10 and D12): with
/// <see cref="Entry"/> set, the apply sends the entry's stored state with its
/// flags (Equipment and Customization), still with no lock, and the readback
/// compares the actor's whole state, every top-level component, with the
/// entry's stored reference; the revert uses the same flags. Without it, HV-3
/// and HV-4 are unchanged.</para>
/// </summary>
public sealed class HumanActorGlamourerStage
{
    public const int RequiredApiMajor = 1;
    public const int MinimumApiMinor = 8;
    public const string RequiredGlamourerVersion = "1.7.1.3";
    public const string RequiredPenumbraVersion = "1.7.2.1";

    /// <summary><c>ApplyFlag.Equipment</c> alone, for the apply and the revert.</summary>
    public const ulong EquipmentOnly = 0x02;

    /// <summary>HV-5 (D10): <c>ApplyFlag.Equipment | ApplyFlag.Customization</c>, no lock and no Once.</summary>
    public const ulong EquipmentAndCustomization = 0x06;

    public const int Success = 0;
    public const int NothingDone = 1;
    public const int ActorNotFound = 2;
    public const int ActorNotHuman = 3;
    public const int InvalidKey = 6;
    public const int InvalidState = 7;

    /// <summary>
    /// The armour and accessory slots, each set to Glamourer's "Nothing" item:
    /// <c>uint.MaxValue − 128 − slot.ToSlot()</c>, where <c>ToSlot</c> maps the
    /// left ring to the right ring, so both rings share one id.
    /// </summary>
    public static readonly IReadOnlyList<(string Slot, ulong ItemId)> Slots =
    [
        ("Head", uint.MaxValue - 128u - 3u),
        ("Body", uint.MaxValue - 128u - 4u),
        ("Hands", uint.MaxValue - 128u - 5u),
        ("Legs", uint.MaxValue - 128u - 7u),
        ("Feet", uint.MaxValue - 128u - 8u),
        ("Ears", uint.MaxValue - 128u - 9u),
        ("Neck", uint.MaxValue - 128u - 10u),
        ("Wrists", uint.MaxValue - 128u - 11u),
        ("RFinger", uint.MaxValue - 128u - 12u),
        ("LFinger", uint.MaxValue - 128u - 12u),
    ];

    /// <summary>The fixed synthetic design: JSON text (a DesignBase: no Identifier), base64-encoded as UTF-8.</summary>
    public static string PayloadJson { get; } = BuildJson();

    public static string Payload { get; } = Convert.ToBase64String(Encoding.UTF8.GetBytes(PayloadJson));

    private readonly IHumanActorGlamourer glamourer;
    private readonly Action<string> log;
    private readonly string prefix;
    private long generation = long.MinValue;
    private uint key;
    private bool busy;

    public HumanActorGlamourerStage(IHumanActorGlamourer glamourer, Action<string> log, string prefix)
    {
        this.glamourer = glamourer ?? throw new ArgumentNullException(nameof(glamourer));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
    }

    /// <summary>Test seam: where the per-spawn key comes from (production: <see cref="HumanPuppetKey.Create()"/>).</summary>
    internal Func<uint> KeySource { get; set; } = HumanPuppetKey.Create;

    /// <summary>
    /// HV-5 M2: the catalogue entry's state, flags and readback reference,
    /// set by <see cref="UseEntry"/> before a spawn; null for HV-3 and HV-4.
    /// Read only by <see cref="Apply"/> and the cleanup.
    /// </summary>
    public HumanGlamourerPayload? Entry { get; private set; }

    /// <summary>
    /// HV-5 M2: sets <see cref="Entry"/> for the next spawn. Refused (with the
    /// reason) unless nothing is applied: the cleanup must use the flags the
    /// state was applied with.
    /// </summary>
    public string? UseEntry(HumanGlamourerPayload? entry)
    {
        if (busy)
            return "glamourer: the stage is in a Glamourer call";
        if (State is not (HumanGlamourerState.None or HumanGlamourerState.Reverted))
            return $"glamourer: the last spawn's state is {State}, not cleaned up";
        // The readback's applied scope is verified for 0x06 only (see HumanGlamourerComparison).
        if (entry is not null && entry.Flags != EquipmentAndCustomization)
            return $"glamourer: an entry is applied with flags 0x{EquipmentAndCustomization:X2} only, not 0x{entry.Flags:X2}";
        Entry = entry;
        return null;
    }

    /// <summary>The flags of the state applied or to be applied (and so of the cleanup).</summary>
    private ulong Flags => Entry?.Flags ?? EquipmentOnly;

    public HumanGlamourerState State { get; private set; }

    /// <summary>States this load reverted and read back as not locked: the accepted residue, one per clean spawn.</summary>
    public int RevertedStates { get; private set; }

    /// <summary>Cleanup problems: any state applied, uncertain or left, and every failed revert or lock check.</summary>
    public int CleanupFailures { get; private set; }

    /// <summary>
    /// The early probe (step 2), after the actor's Penumbra parent reads −1 and
    /// before any temporary mod: null when no Glamourer state exists for the
    /// actor's identity. <paramref name="unowned"/> is the baseline's checks
    /// (null while the actor is provably RSC's and alone with its name).
    /// </summary>
    public string? EarlyProbe(int objectIndex, string? unowned)
    {
        if (Enter() is { } reentered)
            return "glamourer: " + reentered;
        try
        {
            if (unowned is not null)
                return "glamourer: " + unowned;
            // A new spawn: only after nothing was applied or the last state was cleanly reverted.
            if (State is not (HumanGlamourerState.None or HumanGlamourerState.Reverted))
                return $"glamourer: the last spawn's state is {State}, not cleaned up";
            State = HumanGlamourerState.None;
            key = 0;
            if (Gate() is { } refused)
                return refused;
            var next = KeySource();
            if (!HumanPuppetKey.IsPermitted(next))
                return "glamourer: the key source returned a reserved key";
            key = next;
            return Probe(objectIndex, "before the Penumbra mod");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>Step 3: null when applied and read back; otherwise why not (the state is then None, Uncertain or Applied).</summary>
    public string? Apply(int objectIndex, string? unowned)
    {
        if (Enter() is { } reentered)
            return "glamourer: " + reentered;
        try
        {
            if (State != HumanGlamourerState.None || key == 0)
                return "glamourer: the apply is not in order";
            if (unowned is not null)
                return "glamourer: " + unowned;
            if (Changed() is { } changed)
                return changed;
            if (Probe(objectIndex, "before the apply") is { } probe)
                return probe;

            // From here a state may exist until the answer proves otherwise.
            State = HumanGlamourerState.Uncertain;
            int answer;
            try
            {
                answer = glamourer.ApplyState(Entry?.State ?? Payload, objectIndex, key, Flags);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return $"glamourer: the apply did not answer ({exception.GetType().Name}); a state may exist";
            }
            switch (answer)
            {
                case Success:
                    break;
                case InvalidState or ActorNotFound:
                    // Both answers come before Glamourer finds or creates the state.
                    State = HumanGlamourerState.None;
                    return $"glamourer: the apply was refused ({Describe(answer)}); no state was created";
                default:
                    // ActorNotHuman comes after the state was created; InvalidKey means a state appeared
                    // since the probe; anything else is unknown. The state stays uncertain.
                    return $"glamourer: the apply answered {Describe(answer)}; a state may exist";
            }

            if (Entry is { } entry)
                return EntryReadback(objectIndex, entry);
            (int Code, IReadOnlyDictionary<string, ulong>? Items) read;
            try
            {
                read = glamourer.GetState(objectIndex, key);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return $"glamourer: the readback did not answer ({exception.GetType().Name}); the state is uncertain";
            }
            if (read.Code != Success || read.Items is null)
                return $"glamourer: the readback answered {Describe(read.Code)}; the state is uncertain";
            var matching = Slots.Count(slot => read.Items.TryGetValue(slot.Slot, out var item) && item == slot.ItemId);
            if (matching != Slots.Count)
                return $"glamourer: the readback matched {matching} of {Slots.Count} slot(s); the state is uncertain";
            State = HumanGlamourerState.Applied;
            log(prefix + $"glamourer: API {glamourer.ApiVersion?.Major}.{glamourer.ApiVersion?.Minor}, Glamourer {RequiredGlamourerVersion}, " +
                $"Penumbra {RequiredPenumbraVersion}; parent -1; probe: no state (twice); apply Success (equipment only, unlocked); " +
                $"readback {matching} of {Slots.Count} slot(s).");
            return null;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>
    /// HV-5 M2 r2 (U-4): after a redraw seen while the actor was visible, the
    /// entry's state is read back again and compared with the stored reference
    /// as after the apply. Nothing is applied, reverted or unlocked: a
    /// difference is never repaired, it halts the actor. Null when equal;
    /// otherwise why not. The state stays applied either way, so the cleanup
    /// reverts it with the entry's flags.
    /// </summary>
    public string? Reread(int objectIndex, string? unowned)
    {
        if (Enter() is { } reentered)
            return "glamourer: " + reentered;
        try
        {
            if (State != HumanGlamourerState.Applied || Entry is not { } entry || key == 0)
                return "glamourer: the reread is not in order";
            if (unowned is not null)
                return "glamourer: " + unowned;
            if (Changed() is { } changed)
                return "glamourer: " + changed;
            return EntryReadback(objectIndex, entry, again: true);
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>
    /// Cleanup, before the Penumbra release, while the actor still exists.
    /// <paramref name="unowned"/> is the baseline's checks; when it is not null
    /// no call is made. Null when nothing was applied or the state was reverted
    /// and read back as not locked; otherwise the cleanup failure.
    /// </summary>
    public string? Revert(int objectIndex, string? unowned)
    {
        if (Enter() is { } reentered)
            return Fail(reentered);
        try
        {
            if (State is HumanGlamourerState.None)
            {
                if (key != 0)
                    log(prefix + "glamourer cleanup: nothing applied; no Glamourer state created by RSC.");
                return null;
            }
            if (State is not (HumanGlamourerState.Applied or HumanGlamourerState.Uncertain))
                return null;
            if (unowned is not null)
                return Leave("revert not called: " + unowned);
            if (Changed() is { } changed)
                return Leave("revert not called: " + changed);
            int answer;
            try
            {
                answer = glamourer.RevertState(objectIndex, key, Flags);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Leave($"the revert did not answer ({exception.GetType().Name})");
            }
            if (answer != Success)
                return Leave($"the revert answered {Describe(answer)}");
            (int Code, bool IsLocked, bool CanUnlock) locked;
            try
            {
                locked = glamourer.CanUnlock(objectIndex, 0);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Leave($"the lock check did not answer ({exception.GetType().Name})");
            }
            if (locked.Code != Success || locked.IsLocked)
                return Leave(locked.Code != Success ? $"the lock check answered {Describe(locked.Code)}" : "the state is still locked");
            var wasUncertain = State is HumanGlamourerState.Uncertain;
            State = HumanGlamourerState.Reverted;
            RevertedStates++;
            log(prefix + "glamourer cleanup: revert Success; locked no.");
            // The equipment sources are not readable through the API: "reverted" is RevertState's Success answer.
            log(prefix + "glamourer residue: 1 in-memory state for this spawn's synthetic name, unlocked, reverted (revert answered Success); " +
                $"{RevertedStates} this load; cleared when Glamourer unloads; nothing deleted.");
            if (wasUncertain)
                return Fail("the apply's outcome was uncertain; the state was reverted, but the run is not clean");
            return null;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>The actor is no longer provably RSC's: no call is made, and an applied or uncertain state is left and reported.</summary>
    public void Abandon(string why)
    {
        if (State is HumanGlamourerState.Applied or HumanGlamourerState.Uncertain)
            Leave("no call after the loss of ownership: " + why);
    }

    public string Status() => $"glamourer {State}, reverted states {RevertedStates}, cleanup failures {CleanupFailures}";

    /// <summary>HV-5: the readback of an entry's state, back to back with the apply. The state is Applied only when every component is equal.</summary>
    private string? EntryReadback(int objectIndex, HumanGlamourerPayload entry, bool again = false)
    {
        (int Code, string? Json) read;
        try
        {
            read = glamourer.GetStateJson(objectIndex, key);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"glamourer: the readback did not answer ({exception.GetType().Name}); the state is uncertain";
        }
        if (read.Code != Success || read.Json is null)
            return $"glamourer: the readback answered {Describe(read.Code)}; the state is uncertain";
        if (HumanGlamourerComparison.Compare(entry.Reference, read.Json) is not { } compared)
            return "glamourer: the readback or the stored reference is not a JSON object; the state is uncertain";
        if (compared.Differing.Count > 0)
            return $"glamourer: the readback differs from the stored reference in {string.Join(", ", compared.Differing)}; the state is uncertain";
        State = HumanGlamourerState.Applied;
        if (again)
        {
            log(prefix + $"glamourer: read back again after the redraw (no apply): equal to the stored reference in every applied field " +
                $"({HumanGlamourerComparison.Components(read.Json)}; {compared.Unapplied} field(s) the entry does not apply not compared).");
            return null;
        }
        log(prefix + $"glamourer: API {glamourer.ApiVersion?.Major}.{glamourer.ApiVersion?.Minor}, Glamourer {RequiredGlamourerVersion}, " +
            $"Penumbra {RequiredPenumbraVersion}; parent -1; probe: no state (twice); apply Success (flags 0x{entry.Flags:X2}, unlocked); " +
            $"readback equal to the stored reference in every applied field ({HumanGlamourerComparison.Components(read.Json)}; " +
            $"{compared.Unapplied} field(s) the entry does not apply not compared).");
        return null;
    }

    private string? Gate()
    {
        try
        {
            var api = glamourer.ApiVersion;
            if (api is null || api.Value.Major != RequiredApiMajor || api.Value.Minor < MinimumApiMinor)
                return $"glamourer: API {(api is { } a ? $"{a.Major}.{a.Minor}" : "unanswered")} is not {RequiredApiMajor}.{MinimumApiMinor} or later";
            if (!string.Equals(glamourer.GlamourerVersion, RequiredGlamourerVersion, StringComparison.Ordinal))
                return $"glamourer: Glamourer {glamourer.GlamourerVersion ?? "not loaded"} is not {RequiredGlamourerVersion}";
            if (!string.Equals(glamourer.PenumbraVersion, RequiredPenumbraVersion, StringComparison.Ordinal))
                return $"glamourer: Penumbra {glamourer.PenumbraVersion ?? "not loaded"} is not {RequiredPenumbraVersion}";
            generation = glamourer.Generation;
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"glamourer: the gate could not be read ({exception.GetType().Name})";
        }
    }

    private string? Changed()
    {
        try
        {
            return glamourer.Generation == generation ? null : "a plugin loaded or unloaded since the gate";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"the plugin generation could not be read ({exception.GetType().Name})";
        }
    }

    private string? Probe(int objectIndex, string when)
    {
        int answer;
        try
        {
            answer = glamourer.UnlockState(objectIndex, key);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"glamourer: the probe {when} did not answer ({exception.GetType().Name})";
        }
        return answer switch
        {
            NothingDone => null,
            Success => $"glamourer: a Glamourer state already exists for the actor ({when}); not touched",
            InvalidKey => $"glamourer: a Glamourer state locked by someone else exists for the actor ({when}); not touched",
            _ => $"glamourer: the probe {when} answered {Describe(answer)}",
        };
    }

    private string? Enter()
    {
        if (busy)
            return "the stage was entered again during a Glamourer call";
        busy = true;
        return null;
    }

    private string Leave(string why)
    {
        State = HumanGlamourerState.Left;
        log(prefix + "glamourer residue: an applied or uncertain state for this spawn's synthetic name is left in Glamourer's memory; " +
            "CLEANUP FAILURE.");
        return Fail(why);
    }

    private string Fail(string why)
    {
        CleanupFailures++;
        log(prefix + "glamourer cleanup failure: " + why + ".");
        return "glamourer cleanup: " + why;
    }

    private static string Describe(int code) => code switch
    {
        Success => "Success",
        NothingDone => "NothingDone",
        ActorNotFound => "ActorNotFound",
        ActorNotHuman => "ActorNotHuman",
        InvalidKey => "InvalidKey",
        InvalidState => "InvalidState",
        _ => $"code {code}",
    };

    private static string BuildJson()
    {
        var text = new StringBuilder("{\"FileVersion\":1,\"Equipment\":{");
        foreach (var (slot, item) in Slots)
            text.Append($"\"{slot}\":{{\"ItemId\":{item},\"Apply\":true,\"ApplyStain\":false,\"Stain\":0,\"Stain2\":0}},");
        text.Append("\"Hat\":{\"Show\":true,\"Apply\":false},\"VieraEars\":{\"Show\":true,\"Apply\":false},")
            .Append("\"Visor\":{\"IsToggled\":false,\"Apply\":false},\"Weapon\":{\"Show\":true,\"Apply\":false}},")
            // Race and clan are stated (Hyur 1, Midlander 1: what Glamourer 1.7.1.3 defaults Unknown to, with a
            // warning) and never applied; the equipment-only flags remove every customize application anyway.
            .Append("\"Customize\":{\"ModelId\":0,\"Race\":{\"Value\":1,\"Apply\":false},\"Clan\":{\"Value\":1,\"Apply\":false}}}");
        return text.ToString();
    }
}

/// <summary>
/// HV-5 M2: a catalogue entry's Glamourer state (opaque base64, as stored),
/// the flags it is applied with, and the stored structured reference the
/// readback is compared with (owner decision D8 option b).
/// </summary>
public sealed record HumanGlamourerPayload(string State, ulong Flags, string Reference)
{
    /// <summary>Never the stored data.</summary>
    public override string ToString() => $"Glamourer payload (flags 0x{Flags:X2})";
}

/// <summary>The readback comparison: the components that differ in an applied field, and the number of unapplied fields left out.</summary>
public sealed record HumanGlamourerComparisonResult(IReadOnlyList<string> Differing, int Unapplied);

/// <summary>
/// HV-5 M2 (owner decision D10, U-2): the readback compared with the stored
/// reference in every field it marks applied, and in no field it leaves unmarked.
/// Verified by reading Glamourer 1.7.1.3 (tag commit 82cb3b99) with its pinned
/// Glamourer.Api 2.8.2 (ea569211) and Penumbra.GameData (c4f068c4):
/// <list type="bullet">
/// <item>The stored state and reference are exported with
/// <c>ApplicationRules.All</c>; <c>ApplyState</c> with Equipment and
/// Customization (0x06) removes nothing from the stored design's own
/// application (the meta states, crests, stains, bonus items, parameters and
/// materials included). Glamourer may still apply a marked field differently:
/// a main or off hand incompatible with the copy's weapon type is not applied,
/// a highlight colour is replaced while highlights are off, and a
/// customization the design's fix-up drops stays marked. Each of these makes
/// the readback differ, so the spawn refuses hidden; it never shows a look
/// other than the stored one. A saved look whose weapons belong to another
/// job therefore always refuses at the readback.</item>
/// <item>The structured export marks each field's application next to it:
/// <c>Apply</c> (an item, bonus item, customization value, meta state or
/// parameter), <c>ApplyStain</c> (<c>Stain</c>, <c>Stain2</c>, …),
/// <c>ApplyCrest</c> (<c>Crest</c>), and a material's <c>Enabled</c>.</item>
/// <item>Not compared: the race customization. <c>AllRelevant</c> leaves it
/// unmarked, but Glamourer applies it together with the clan; the spawn's
/// race, clan, gender and body-type match and the whole-skeleton check after
/// Glamourer cover it. Crests outside the head, body and off-hand slots are
/// not applied.</item>
/// </list>
/// So: <c>FileVersion</c> and any component not listed here are compared
/// whole; in <c>Equipment</c>, <c>Bonus</c>, <c>Customize</c> and
/// <c>Parameters</c> each entry's application markers must be equal, and a
/// field is compared exactly when the reference marks it applied and left out
/// otherwise; in <c>Materials</c> the entries the reference enables must be
/// equal and the readback may enable no other. Values are never reported.
/// </summary>
public static class HumanGlamourerComparison
{
    private static readonly string[] GatedComponents = ["Equipment", "Bonus", "Customize", "Parameters"];
    private static readonly string[] Gates = ["Apply", "ApplyStain", "ApplyCrest"];

    /// <summary>The comparison, or null when either text is not a JSON object (or repeats a name).</summary>
    public static HumanGlamourerComparisonResult? Compare(string reference, string readback)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(readback);
        try
        {
            using var left = JsonDocument.Parse(reference, new JsonDocumentOptions { MaxDepth = 64 });
            using var right = JsonDocument.Parse(readback, new JsonDocumentOptions { MaxDepth = 64 });
            if (Properties(left.RootElement) is not { } mine || Properties(right.RootElement) is not { } theirs)
                return null;
            var differing = new List<string>();
            var unapplied = 0;
            foreach (var name in mine.Keys.Union(theirs.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var hasMine = mine.TryGetValue(name, out var a);
                var hasTheirs = theirs.TryGetValue(name, out var b);
                bool equal;
                if (!hasMine || !hasTheirs)
                    equal = name == "Materials" && Materials(hasMine ? a : null, hasTheirs ? b : null, ref unapplied) is true;
                else if (GatedComponents.Contains(name, StringComparer.Ordinal))
                    equal = Gated(a, b, ref unapplied) is true;
                else if (name == "Materials")
                    equal = Materials(a, b, ref unapplied) is true;
                else
                    equal = JsonElement.DeepEquals(a, b);
                if (!equal)
                    differing.Add(name);
            }
            return new HumanGlamourerComparisonResult(differing, unapplied);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The sorted names of the top-level components that differ in an applied
    /// field, or null when either text is not a JSON object.
    /// </summary>
    public static IReadOnlyList<string>? Differences(string reference, string readback) => Compare(reference, readback)?.Differing;

    /// <summary>A gated component: each entry's markers equal, then each field the reference applies equal.</summary>
    private static bool? Gated(JsonElement reference, JsonElement readback, ref int unapplied)
    {
        if (Properties(reference) is not { } mine || Properties(readback) is not { } theirs)
            return JsonElement.DeepEquals(reference, readback);
        foreach (var name in mine.Keys.Union(theirs.Keys, StringComparer.Ordinal))
        {
            if (!mine.TryGetValue(name, out var a) || !theirs.TryGetValue(name, out var b))
                return false;
            if (a.ValueKind != JsonValueKind.Object || b.ValueKind != JsonValueKind.Object)
            {
                if (!JsonElement.DeepEquals(a, b))
                    return false;
                continue;
            }
            if (Entry(a, b, ref unapplied) is not true)
                return false;
        }
        return true;
    }

    /// <summary>One entry of a gated component.</summary>
    private static bool? Entry(JsonElement reference, JsonElement readback, ref int unapplied)
    {
        if (Properties(reference) is not { } mine || Properties(readback) is not { } theirs)
            return false;
        foreach (var gate in Gates)
        {
            if (Flag(mine, gate) != Flag(theirs, gate))
                return false;
        }
        foreach (var field in mine.Keys.Union(theirs.Keys, StringComparer.Ordinal))
        {
            if (Gates.Contains(field, StringComparer.Ordinal))
                continue;
            if (!Flag(mine, GateOf(field)))
            {
                unapplied++;
                continue;
            }
            if (!mine.TryGetValue(field, out var a) || !theirs.TryGetValue(field, out var b) || !JsonElement.DeepEquals(a, b))
                return false;
        }
        return true;
    }

    /// <summary>The materials: every entry the reference enables equal; no other entry enabled in the readback.</summary>
    private static bool? Materials(JsonElement? reference, JsonElement? readback, ref int unapplied)
    {
        var mine = reference is { } r ? Properties(r) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var theirs = readback is { } b ? Properties(b) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (mine is null || theirs is null)
            return false;
        foreach (var name in mine.Keys.Union(theirs.Keys, StringComparer.Ordinal))
        {
            var applied = mine.TryGetValue(name, out var a) && Enabled(a);
            var present = theirs.TryGetValue(name, out var c);
            if (!applied)
            {
                if (present && Enabled(c))
                    return false;
                unapplied++;
                continue;
            }
            if (!present || !JsonElement.DeepEquals(a, c))
                return false;
        }
        return true;
    }

    private static bool Enabled(JsonElement material) =>
        material.ValueKind == JsonValueKind.Object && material.TryGetProperty("Enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;

    /// <summary>The marker that applies <paramref name="field"/>: ApplyCrest for Crest, ApplyStain for the stains, Apply otherwise.</summary>
    private static string GateOf(string field) =>
        field == "Crest" ? "ApplyCrest" : field.StartsWith("Stain", StringComparison.Ordinal) ? "ApplyStain" : "Apply";

    /// <summary>A marker's value; an absent marker is false (Glamourer writes only true ones).</summary>
    private static bool Flag(Dictionary<string, JsonElement> entry, string gate) =>
        entry.TryGetValue(gate, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The top-level component names, comma-separated (at most 16), or "none".</summary>
    public static string Components(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var names = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(property => property.Name).Take(16).ToList()
                : new List<string>();
            return names.Count == 0 ? "none" : string.Join(", ", names);
        }
        catch (JsonException)
        {
            return "none";
        }
    }

    /// <summary>An object's properties by name, or null when it is not an object or a name repeats.</summary>
    private static Dictionary<string, JsonElement>? Properties(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value.Clone()))
                return null;
        }
        return properties;
    }
}
#endif
