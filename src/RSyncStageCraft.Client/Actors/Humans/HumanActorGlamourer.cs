#if RSC_DEVELOPMENT
using System.Text;

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
/// </summary>
public sealed class HumanActorGlamourerStage
{
    public const int RequiredApiMajor = 1;
    public const int MinimumApiMinor = 8;
    public const string RequiredGlamourerVersion = "1.7.1.3";
    public const string RequiredPenumbraVersion = "1.7.2.1";

    /// <summary><c>ApplyFlag.Equipment</c> alone, for the apply and the revert.</summary>
    public const ulong EquipmentOnly = 0x02;

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
                answer = glamourer.ApplyState(Payload, objectIndex, key, EquipmentOnly);
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
                answer = glamourer.RevertState(objectIndex, key, EquipmentOnly);
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
#endif
