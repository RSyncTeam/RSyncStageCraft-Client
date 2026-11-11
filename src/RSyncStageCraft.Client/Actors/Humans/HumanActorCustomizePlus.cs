#if RSC_DEVELOPMENT
namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// HV-4's whole Customize+ surface (IPC API 6.4, by label), by object index
/// for the parent entry and the apply and by the provider's receipt for the
/// deletion. There is no deletion by object index, no persistent profile
/// call and no event subscription. Every call is made on the game thread. A
/// provider that does not answer throws; the stage treats every throw after
/// an apply was requested as an uncertain profile. The only implementation is
/// the plugin's Debug-only HV-4 adapter.
/// </summary>
public interface IHumanActorCustomizePlus
{
    /// <summary>The installed, loaded Customize+ plugin's version (read only); null only when none is loaded; throws when the list cannot be read.</summary>
    string? PluginVersion { get; }

    /// <summary><c>CustomizePlus.General.GetApiVersion</c>, or null.</summary>
    (int Major, int Minor)? ApiVersion { get; }

    /// <summary><c>CustomizePlus.General.IsValid</c>.</summary>
    bool IsValid { get; }

    /// <summary>Changes whenever any plugin loads or unloads.</summary>
    long Generation { get; }

    /// <summary><c>CustomizePlus.GameState.GetCutsceneParentIndex(index)</c>.</summary>
    int GetParent(int objectIndex);

    /// <summary><c>CustomizePlus.GameState.SetCutsceneParentIndex(index, parent)</c>: the answer code.</summary>
    int SetParent(int objectIndex, int parent);

    /// <summary><c>CustomizePlus.Profile.SetTemporaryProfileOnCharacter(index, json)</c>: the code and the provider's receipt.</summary>
    (int Code, Guid? Profile) SetTemporaryProfile(ushort objectIndex, string profileJson);

    /// <summary><c>CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId(receipt)</c>: the answer code.</summary>
    int DeleteTemporaryProfile(Guid profile);

    /// <summary>
    /// Read only, never logged in detail: the owner's (index 0) active normal
    /// profile and the normal profile list, as one comparable fingerprint.
    /// Neither read can see a temporary profile.
    /// </summary>
    string OwnerProfiles();
}

/// <summary>Where HV-4's one temporary profile stands.</summary>
public enum HumanCustomizePlusState
{
    /// <summary>No profile was requested by RSC.</summary>
    None,

    /// <summary>The apply answered Success with a new receipt.</summary>
    Applied,

    /// <summary>An apply was requested and its outcome is not known: a profile may exist, and RSC holds no receipt.</summary>
    Uncertain,

    /// <summary>Deleted by its receipt (Success).</summary>
    Removed,

    /// <summary>Not found by its receipt, or ended with an unloaded Customize+: nothing of RSC's remains in Customize+.</summary>
    Gone,

    /// <summary>Applied or uncertain, then left without a successful deletion.</summary>
    Left,
}

/// <summary>
/// HV-4 (Debug only): one Customize+ temporary profile for HV-3's actor,
/// called by <see cref="HumanActorBaseline"/> only.
/// <list type="number">
/// <item>The gate: Customize+ exactly 2.2.1.3 (the installed build whose
/// tagged source this stage was checked against), IPC API exactly (6, 4),
/// <c>IsValid</c>; the plugin generation is recorded.</item>
/// <item>The detach, once the Penumbra parent reads −1 and before the
/// collection: Customize+ keeps its own copy-parent table, which resolves an
/// object to its copy source. The actor's entry is set to −1 and read back.</item>
/// <item>On the update after HV-3's readback: the checks, the generation, the
/// parent again, the owner fingerprint, then one
/// <c>SetTemporaryProfileOnCharacter</c> with the fixed payload.</item>
/// <item>Cleanup, first, before the Glamourer revert: deletion by the receipt
/// only. It names RSC's profile and no object, so it is not gated on the
/// object. An uncertain apply holds no receipt and is never cleaned by
/// object index (owner decision D4): it is reported as uncertain.</item>
/// </list>
/// The owner fingerprint cannot detect temporary profiles; it shows only that
/// the owner's normal profiles did not change.
/// </summary>
public sealed class HumanActorCustomizePlusStage
{
    public const string RequiredVersion = "2.2.1.3";
    public const int RequiredApiMajor = 6;
    public const int RequiredApiMinor = 4;

    public const int Success = 0;
    public const int InvalidCharacter = 1;
    public const int CorruptedProfile = 2;
    public const int ProfileNotFound = 3;
    public const int InvalidArgument = 4;
    public const int UnknownError = 255;

    /// <summary>The fixed synthetic profile: the whole model drawn 1.15 times larger through the root bone, every field explicit.</summary>
    public const string PayloadJson =
        "{\"Bones\":{\"n_root\":{\"Translation\":{\"X\":0.0,\"Y\":0.0,\"Z\":0.0},\"Rotation\":{\"X\":0.0,\"Y\":0.0,\"Z\":0.0}," +
        "\"Scaling\":{\"X\":1.15,\"Y\":1.15,\"Z\":1.15},\"ChildScaling\":{\"X\":1.0,\"Y\":1.0,\"Z\":1.0}," +
        "\"PropagateTranslation\":false,\"PropagateRotation\":false,\"PropagateScale\":false,\"ChildScaleIndependent\":false}}}";

    private readonly IHumanActorCustomizePlus customizePlus;
    private readonly Action<string> log;
    private readonly string prefix;
    private readonly HashSet<Guid> seen = [];
    private long generation = long.MinValue;
    private bool detached;
    private Guid receipt;
    private string? ownerBefore;
    private bool busy;

    public HumanActorCustomizePlusStage(IHumanActorCustomizePlus customizePlus, Action<string> log, string prefix)
    {
        this.customizePlus = customizePlus ?? throw new ArgumentNullException(nameof(customizePlus));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
    }

    public HumanCustomizePlusState State { get; private set; }

    /// <summary>Profiles this load deleted by their receipt.</summary>
    public int RemovedProfiles { get; private set; }

    /// <summary>Cleanup problems: any profile applied, uncertain or left, and every failed deletion.</summary>
    public int CleanupFailures { get; private set; }

    /// <summary>
    /// Step 2. Null when the gate passed and the actor's Customize+ parent
    /// reads −1; otherwise why not. <paramref name="unowned"/> is the
    /// baseline's checks.
    /// </summary>
    public string? Detach(int objectIndex, string? unowned)
    {
        if (Enter() is { } reentered)
            return "customize+: " + reentered;
        try
        {
            if (unowned is not null)
                return "customize+: " + unowned;
            if (State is not (HumanCustomizePlusState.None or HumanCustomizePlusState.Removed or HumanCustomizePlusState.Gone))
                return $"customize+: the last spawn's profile is {State}, not cleaned up";
            State = HumanCustomizePlusState.None;
            detached = false;
            receipt = Guid.Empty;
            ownerBefore = null;
            if (Gate() is { } refused)
                return "customize+: " + refused;
            int parent;
            try
            {
                parent = customizePlus.GetParent(objectIndex);
                if (parent != -1)
                {
                    var answer = customizePlus.SetParent(objectIndex, -1);
                    var after = customizePlus.GetParent(objectIndex);
                    if (answer != Success || after != -1)
                        return $"customize+: the actor's Customize+ parent ({parent}) could not be cleared ({Describe(answer)}, now {after})";
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return $"customize+: the parent could not be read or cleared ({exception.GetType().Name})";
            }
            detached = true;
            log(prefix + $"customize+: Customize+ {RequiredVersion}, API {RequiredApiMajor}.{RequiredApiMinor}, valid; " +
                $"parent {(parent == -1 ? "-1" : $"{parent}, now -1")}.");
            return null;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>True when the actor's Customize+ parent reads −1 (read only, total).</summary>
    public bool ParentDetached(int objectIndex)
    {
        try
        {
            return customizePlus.GetParent(objectIndex) == -1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Step 3: null when applied with a new receipt; otherwise why not (the state is then None, Uncertain or Applied).</summary>
    public string? Apply(int objectIndex, string? unowned)
    {
        if (Enter() is { } reentered)
            return "customize+: " + reentered;
        try
        {
            if (State != HumanCustomizePlusState.None || !detached || objectIndex is < 0 or > ushort.MaxValue)
                return "customize+: the apply is not in order";
            if (unowned is not null)
                return "customize+: " + unowned;
            if (Changed() is { } changed)
                return "customize+: " + changed;
            try
            {
                ownerBefore = customizePlus.OwnerProfiles();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return $"customize+: the owner's profiles could not be read ({exception.GetType().Name})";
            }

            // From here a profile may exist until the answer proves otherwise.
            State = HumanCustomizePlusState.Uncertain;
            (int Code, Guid? Profile) answer;
            try
            {
                answer = customizePlus.SetTemporaryProfile((ushort)objectIndex, PayloadJson);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return $"customize+: the apply did not answer ({exception.GetType().Name}); a profile may exist; CLEANUP UNCERTAIN";
            }
            if (answer.Code is InvalidCharacter or CorruptedProfile)
            {
                // Both answers come before Customize+ builds or adds a profile.
                State = HumanCustomizePlusState.None;
                return $"customize+: the apply was refused ({Describe(answer.Code)}); no profile was created";
            }
            if (answer.Code != Success || answer.Profile is not { } profile || profile == Guid.Empty || !seen.Add(profile))
            {
                // UnknownError can follow the insertion; a missing or reused receipt cannot be trusted.
                return $"customize+: the apply answered {Describe(answer.Code)}{(answer.Code == Success ? " without a new receipt" : "")}; " +
                    "a profile may exist; CLEANUP UNCERTAIN";
            }
            receipt = profile;
            State = HumanCustomizePlusState.Applied;
            if (!ParentDetached(objectIndex))
                return "customize+: the actor's Customize+ parent no longer reads -1 after the apply";
            log(prefix + $"customize+: temporary profile Success, receipt {Tag(receipt)}; parent -1; " +
                "owner's normal profiles read (temporary profiles cannot be read).");
            return null;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>
    /// Cleanup, first, while the actor may still exist: deletion by the
    /// receipt only, never by object index. Null when nothing was applied or
    /// nothing of RSC's remains; otherwise the cleanup failure or finding.
    /// </summary>
    public string? Remove()
    {
        if (Enter() is { } reentered)
            return Fail(reentered);
        try
        {
            switch (State)
            {
                case HumanCustomizePlusState.None:
                    if (detached)
                        log(prefix + "customize+ cleanup: nothing applied; no Customize+ profile requested by RSC.");
                    detached = false;
                    return null;
                case HumanCustomizePlusState.Uncertain:
                    // Owner decision D4: owning the actor does not prove owning whichever profile targets it.
                    return Leave("the apply was ambiguous and RSC holds no receipt; no deletion by object index", "CLEANUP UNCERTAIN");
                case HumanCustomizePlusState.Applied:
                    break;
                default:
                    return null;
            }
            if (Changed() is not null)
            {
                // A plugin loaded or unloaded: Customize+ is gated again before the receipt is used (D3).
                string? version;
                try
                {
                    version = customizePlus.PluginVersion;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    return Leave($"the plugin list could not be read after a plugin change ({exception.GetType().Name})", "CLEANUP FAILURE");
                }
                if (version is null)
                {
                    State = HumanCustomizePlusState.Gone;
                    detached = false;
                    log(prefix + $"customize+ cleanup: Customize+ not loaded; its in-memory temporary profile (receipt {Tag(receipt)}) ended with it; " +
                        "no call made.");
                    return null;
                }
                if (Gate() is { } refused)
                    return Leave("no deletion after a plugin change: " + refused, "CLEANUP FAILURE");
                log(prefix + "customize+ cleanup: a plugin loaded or unloaded since the gate; Customize+ gated again.");
            }
            int answer;
            try
            {
                answer = customizePlus.DeleteTemporaryProfile(receipt);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Leave($"the deletion did not answer ({exception.GetType().Name})", "CLEANUP FAILURE");
            }
            detached = false;
            if (answer == Success)
            {
                State = HumanCustomizePlusState.Removed;
                RemovedProfiles++;
                log(prefix + $"customize+ cleanup: removed by receipt {Tag(receipt)} (Success).");
            }
            else if (answer == ProfileNotFound)
            {
                // Replaced, reloaded or removed by Customize+ itself: nothing of RSC's remains, and nothing else is touched.
                State = HumanCustomizePlusState.Gone;
                log(prefix + $"customize+ cleanup: receipt {Tag(receipt)} already gone (ProfileNotFound); no other call.");
            }
            else
            {
                return Leave($"the deletion answered {Describe(answer)}", "CLEANUP FAILURE");
            }
            return OwnerUnchanged();
        }
        finally
        {
            busy = false;
        }
    }

    public string Status() => $"customize+ {State}, removed profiles {RemovedProfiles}, cleanup failures {CleanupFailures}";

    private string? OwnerUnchanged()
    {
        string after;
        try
        {
            after = customizePlus.OwnerProfiles();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log(prefix + $"customize+ finding: the owner's normal profiles could not be read after the removal ({exception.GetType().Name}).");
            return "customize+ finding: the owner's normal profiles could not be read after the removal";
        }
        if (string.Equals(after, ownerBefore, StringComparison.Ordinal))
        {
            log(prefix + "customize+: owner's normal profiles unchanged (temporary profiles cannot be read).");
            return null;
        }
        log(prefix + "customize+ finding: the owner's normal profiles changed between the apply and the removal; nothing corrected.");
        return "customize+ finding: the owner's normal profiles changed";
    }

    private string? Gate()
    {
        try
        {
            if (!string.Equals(customizePlus.PluginVersion, RequiredVersion, StringComparison.Ordinal))
                return $"Customize+ {customizePlus.PluginVersion ?? "not loaded"} is not {RequiredVersion}";
            var api = customizePlus.ApiVersion;
            if (api is not (RequiredApiMajor, RequiredApiMinor))
                return $"API {(api is { } a ? $"{a.Major}.{a.Minor}" : "unanswered")} is not {RequiredApiMajor}.{RequiredApiMinor}";
            if (!customizePlus.IsValid)
                return "Customize+ does not report itself valid";
            generation = customizePlus.Generation;
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"the gate could not be read ({exception.GetType().Name})";
        }
    }

    private string? Changed()
    {
        try
        {
            return customizePlus.Generation == generation ? null : "a plugin loaded or unloaded since the gate";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"the plugin generation could not be read ({exception.GetType().Name})";
        }
    }

    private string? Enter()
    {
        if (busy)
            return "the stage was entered again during a Customize+ call";
        busy = true;
        return null;
    }

    private string Leave(string why, string verdict)
    {
        State = HumanCustomizePlusState.Left;
        detached = false;
        log(prefix + "customize+ residue: an applied or uncertain temporary profile for this spawn's synthetic name may remain in " +
            $"Customize+'s memory until Customize+ drops it or unloads; {verdict}.");
        return Fail(why);
    }

    private string Fail(string why)
    {
        CleanupFailures++;
        log(prefix + "customize+ cleanup failure: " + why + ".");
        return "customize+ cleanup: " + why;
    }

    /// <summary>A correlation tag: the receipt's first 8 hex digits, never the whole receipt.</summary>
    private static string Tag(Guid profile) => profile.ToString("N")[..8];

    private static string Describe(int code) => code switch
    {
        Success => "Success",
        InvalidCharacter => "InvalidCharacter",
        CorruptedProfile => "CorruptedProfile",
        ProfileNotFound => "ProfileNotFound",
        InvalidArgument => "InvalidArgument",
        UnknownError => "UnknownError",
        _ => $"code {code}",
    };
}
#endif
