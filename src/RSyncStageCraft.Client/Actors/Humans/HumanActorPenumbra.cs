#if RSC_DEVELOPMENT
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// HV-2's whole Penumbra surface, through Penumbra's public IPC only. Every
/// member is called on the game thread and is total: a provider that does not
/// answer gives null or false. The only implementation is the plugin's
/// Debug-only HV-2 adapter. There is no forced assignment, no
/// <c>SetCollectionForObject</c>, no collection-wide write and no Penumbra
/// redraw: the actor is undrawn and drawn again by RSC itself.
/// </summary>
public interface IHumanActorPenumbra
{
    /// <summary>Penumbra answers a compatible API version and every bound subscriber is valid.</summary>
    bool Available { get; }

    /// <summary><c>Penumbra.GetModDirectory</c>: used only to keep the package to files beneath it.</summary>
    string? ModDirectory();

    /// <summary><c>Penumbra.GetGameObjectResourcePaths.V5</c> for exactly one index: each actual path with its game paths.</summary>
    IReadOnlyList<AppearanceResourceEntry>? ResourcePaths(int globalIndex);

    /// <summary><c>Penumbra.GetMetaManipulations</c>: the manipulations of the collection that applies to the object, as Penumbra encodes them.</summary>
    string? MetaManipulations(int globalIndex);

    int? GetParent(int globalIndex);

    bool SetParent(int globalIndex, int parent);

    HumanCollectionReading? CollectionFor(int globalIndex);

    /// <summary>A new temporary collection, or null when its id did not come back.</summary>
    Guid? CreateTemporaryCollection(string name);

    bool AddTemporaryMod(string tag, Guid collection, IReadOnlyDictionary<string, string> paths, string meta, int priority);

    /// <summary>Always non-forced: an existing temporary or individual collection for the actor is refused.</summary>
    HumanAssignmentResult Assign(Guid collection, int globalIndex);

    HumanModRemoval RemoveTemporaryMod(string tag, Guid collection, int priority);

    bool DeleteTemporaryCollection(Guid collection);
}

/// <summary>Penumbra's answer to removing the temporary mod.</summary>
public enum HumanModRemoval
{
    Removed,

    /// <summary>Penumbra reported nothing to remove (<c>NothingChanged</c>).</summary>
    NotPresent,

    Failed,
}

/// <summary>
/// The player's resolved resources as one ordinary temporary mod, built the
/// way the existing appearance capture reads them: Penumbra has already
/// resolved priorities, options and inheritance, so every redirect to a file
/// beneath the Penumbra mod directory is kept for every game path it serves,
/// whatever its file type (a custom skeleton package is no different from
/// any other mod), and every swap is kept as a swap. Vanilla paths need
/// nothing. A file outside the mod directory is left out and counted. Two
/// different sources for one game path refuse the whole package.
/// </summary>
public sealed record HumanActorPenumbraPackage(
    IReadOnlyDictionary<string, string> Paths, int Redirects, int Swaps, int Vanilla, int Outside)
{
    /// <summary>
    /// The package, or null with the reason. <paramref name="contained"/> is
    /// the capture's containment check (a provable file beneath the mod
    /// directory, or null).
    /// </summary>
    public static (HumanActorPenumbraPackage? Package, string? Refusal) Build(
        IReadOnlyList<AppearanceResourceEntry> resources, string? modDirectory, Func<string?, string, string?> contained)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(contained);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int redirects = 0, swaps = 0, vanilla = 0, outside = 0;
        foreach (var entry in resources)
        {
            if (entry?.ActualPath is not { Length: > 0 } actual || entry.GamePaths is null)
                return (null, "Penumbra reported an incomplete resource entry");
            var local = AppearanceCapturePlanner.IsLocalFile(actual);
            if (local && contained(modDirectory, actual) is null)
            {
                outside += entry.GamePaths.Count;
                continue;
            }
            foreach (var gamePath in entry.GamePaths)
            {
                if (string.IsNullOrEmpty(gamePath))
                    return (null, "Penumbra reported an empty game path");
                if (!local && string.Equals(gamePath, actual, StringComparison.OrdinalIgnoreCase))
                {
                    vanilla++;
                    continue;
                }
                if (paths.TryGetValue(gamePath, out var existing))
                {
                    if (!SameTarget(existing, actual))
                        return (null, "one game path has two different sources");
                    continue;
                }
                paths[gamePath] = actual;
                if (local)
                    redirects++;
                else
                    swaps++;
            }
        }
        return (new HumanActorPenumbraPackage(paths, redirects, swaps, vanilla, outside), null);
    }

    /// <summary>Two sources are the same file or game path (separators and case ignored, as Penumbra reports them on Windows).</summary>
    public static bool SameTarget(string left, string right) =>
        string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// HV-2 (Debug only): one actor-specific temporary collection for HV-1's
/// visible human, through Penumbra's ordinary pipeline. Called by
/// <see cref="HumanActorBaseline"/> only, on the game thread, after the
/// actor's first draw (still at alpha 0):
/// <list type="number">
/// <item>Penumbra's cutscene parent for the actor is set to −1 and read back,
/// so Penumbra identifies the actor by its own synthetic name, never as the
/// player it was copied from;</item>
/// <item>the player's collection reading is recorded, and the actor must
/// have no individual collection;</item>
/// <item>the Penumbra mod folder must be readable, and the package (the
/// player's resolved resources and manipulations,
/// <see cref="HumanActorPenumbraPackage"/>) must hold at least one redirect
/// to a file beneath it;</item>
/// <item>a temporary collection, the package as its one temporary mod, and a
/// non-forced assignment to the actor's index;</item>
/// <item>the actor's effective collection must be the new one and the
/// player's reading must be unchanged.</item>
/// </list>
/// The baseline then undraws the actor, sees no human draw object, and
/// draws it again, so the new draw object is created while the collection is
/// assigned. <see cref="Confirm"/> then requires the parent still −1, the
/// collection still effective, the player's reading unchanged, no packaged
/// game path resolved elsewhere, and every packaged redirect reported for the
/// actor. This is resolution evidence only: it shows which files Penumbra
/// reports for the actor, not how the game renders or deforms them.
/// <para>Release, before the actor is undrawn and deleted: the temporary mod
/// is removed (always attempted, whatever the add answered) and the
/// collection deleted (which removes its assignment); then the actor must no
/// longer resolve to it. A collection whose deletion was not confirmed is
/// counted as residue. Nothing is persisted: a temporary collection lives
/// only in Penumbra's memory, until Penumbra unloads.</para>
/// <para>HV-3 (<see cref="ReleaseByDeletion"/>): once an assignment was
/// attempted and the actor may still resolve to the collection, the mod is
/// not removed first. Removing a temporary mod from a collection an actor
/// resolves to makes Glamourer queue a reapply that keeps the actor's address
/// for later frames, while the actor is deleted in this update. Deleting the
/// assigned collection instead makes Penumbra (1.7.2.1) drop its temporary
/// mods with no mod-setting event. Without an assignment the mod is removed
/// first, as in HV-2: no actor resolves to the collection then.</para>
/// </summary>
public sealed class HumanActorPenumbraStage
{
    public const string Tag = "RSC HV-2";
    public const string CollectionName = "RSC HV-2";
    public const int Priority = 0;

    private readonly IHumanActorPenumbra penumbra;
    private readonly Action<string> log;
    private readonly string prefix;
    private Held? held;

    public HumanActorPenumbraStage(IHumanActorPenumbra penumbra, Action<string> log, string prefix)
    {
        this.penumbra = penumbra ?? throw new ArgumentNullException(nameof(penumbra));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
    }

    /// <summary>Test seam: the capture's containment check (production: <see cref="AppearanceCapturePlanner.ContainedFile"/>).</summary>
    internal Func<string?, string, string?> Contained { get; set; } = AppearanceCapturePlanner.ContainedFile;

    /// <summary>Test seam: the mod folder is a fully qualified folder that exists.</summary>
    internal Func<string?, bool> ModFolderReadable { get; set; } = FolderReadable;

    public bool Available => Safe(() => penumbra.Available, false);

    /// <summary>HV-3 only: the release described above. HV-2 leaves it false, so its release is unchanged.</summary>
    public bool ReleaseByDeletion { get; init; }

    /// <summary>HV-3: Penumbra's cutscene parent for the actor reads back as −1 (read only). False when unread.</summary>
    public bool ParentDetached(int actorIndex) => Safe(() => penumbra.GetParent(actorIndex), null) == -1;

    /// <summary>True while a temporary collection is held.</summary>
    public bool Holding => held is not null;

    /// <summary>Temporary collections whose deletion was not confirmed.</summary>
    public int Residue { get; private set; }

    /// <summary>
    /// Steps 1–5. Null when the collection is assigned to the actor alone;
    /// otherwise why not (anything made is then released). HV-3 passes
    /// <paramref name="beforeCollection"/>, run once the parent reads −1 and
    /// before anything is created: a non-null answer refuses with nothing made.
    /// </summary>
    public string? Apply(int actorIndex, int playerIndex, Func<string?>? beforeCollection = null)
    {
        if (held is not null)
            return "a temporary collection is already held";
        if (!Available)
            return "Penumbra is unavailable or incompatible";

        // 1. The actor is its own identity: no cutscene parent.
        var parent = Safe(() => penumbra.GetParent(actorIndex), null);
        if (parent is null)
            return "the actor's Penumbra parent could not be read";
        if (parent != -1)
        {
            if (!Safe(() => penumbra.SetParent(actorIndex, -1), false) || Safe(() => penumbra.GetParent(actorIndex), null) != -1)
                return $"the actor's Penumbra parent ({parent}) could not be cleared";
        }

        // 2. The player's reading, and no collection of the actor's own.
        if (Safe(() => penumbra.CollectionFor(playerIndex), null) is not { ObjectValid: true } player)
            return "the player's collection could not be read";
        if (Safe(() => penumbra.CollectionFor(actorIndex), null) is not { ObjectValid: true } before)
            return "Penumbra does not identify the actor";
        if (before.IndividualSet)
            return "the actor's identity already has an individual collection";
        if (beforeCollection?.Invoke() is { } refusedBefore)
            return refusedBefore;

        // 3. The package: the player's resolved resources and manipulations, from a readable mod folder.
        var modDirectory = Safe(() => penumbra.ModDirectory(), null);
        if (!Safe(() => ModFolderReadable(modDirectory), false))
            return "the Penumbra mod folder could not be read";
        if (Safe(() => penumbra.ResourcePaths(playerIndex), null) is not { } resources)
            return "the player's resource paths could not be read";
        if (Safe(() => penumbra.MetaManipulations(playerIndex), null) is not { } meta)
            return "the player's manipulations could not be read";
        var (package, refusal) = HumanActorPenumbraPackage.Build(resources, modDirectory, Contained);
        if (package is null)
            return refusal;
        if (package.Redirects == 0)
            return $"the package has no redirect to a file beneath the mod folder ({package.Outside} left out), so nothing could be confirmed";
        var paths = package.Paths;

        // 4. The collection, its one mod, and the non-forced assignment.
        if (Safe(() => penumbra.CreateTemporaryCollection(CollectionName), null) is not { } id || id == Guid.Empty)
            return "no temporary collection id came back";
        var holding = new Held(id, actorIndex, playerIndex, player, package);
        held = holding;
        if (!Safe(() => penumbra.AddTemporaryMod(Tag, id, paths, meta, Priority), false))
            return Undo("the temporary mod was refused");
        holding.ModAdded = true;
        holding.AssignAttempted = true;
        switch (Safe(() => penumbra.Assign(id, actorIndex), HumanAssignmentResult.Failed))
        {
            case HumanAssignmentResult.Success:
                break;
            case HumanAssignmentResult.Refused:
                return Undo("the assignment was refused: the actor's identity already has a collection");
            default:
                return Undo("the assignment failed");
        }

        // 5. Only this actor.
        if (Safe(() => penumbra.CollectionFor(playerIndex), null) != player)
            return Undo("the player's collection changed with the assignment");
        if (Safe(() => penumbra.CollectionFor(actorIndex), null) is not { ObjectValid: true } after || after.EffectiveCollection != id)
            return Undo("the actor does not resolve to the temporary collection");
        log(prefix + $"Penumbra: parent {(parent == -1 ? "-1" : $"{parent}, now -1")}, package {package.Redirects} redirect(s), " +
            $"{package.Swaps} swap(s), {package.Vanilla} vanilla, {package.Outside} outside the mod folder left out, manipulations " +
            $"{(meta.Length == 0 ? "none" : "yes")}; temporary collection assigned to this actor only, player's collection unchanged; " +
            "undrawing to rebuild under it.");
        return null;
    }

    /// <summary>
    /// After the rebuilt draw: null when the actor resolves through the
    /// package and the player is unchanged; otherwise why not.
    /// <paramref name="settling"/> is true when the only problem is packaged
    /// redirects not yet reported (the new draw object may still be loading
    /// them), so the caller may read again within a bounded window; every
    /// other refusal is final.
    /// </summary>
    public string? Confirm(out bool settling)
    {
        settling = false;
        if (held is not { } holding)
            return "no temporary collection is held";
        if (Safe(() => penumbra.GetParent(holding.ActorIndex), null) != -1)
            return "the actor's Penumbra parent is no longer -1";
        if (Safe(() => penumbra.CollectionFor(holding.ActorIndex), null) is not { ObjectValid: true } actor || actor.EffectiveCollection != holding.Collection)
            return "the actor no longer resolves to the temporary collection";
        if (Safe(() => penumbra.CollectionFor(holding.PlayerIndex), null) != holding.Player)
            return "the player's collection changed";
        if (Safe(() => penumbra.ResourcePaths(holding.ActorIndex), null) is not { } resources)
            return "the actor's resource paths could not be read";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int elsewhere = 0, other = 0;
        foreach (var entry in resources)
        {
            if (entry?.ActualPath is not { } actual || entry.GamePaths is null)
                return "Penumbra reported an incomplete resource entry for the actor";
            foreach (var gamePath in entry.GamePaths)
            {
                if (string.IsNullOrEmpty(gamePath))
                    continue;
                if (holding.Package.Paths.TryGetValue(gamePath, out var expected))
                {
                    if (HumanActorPenumbraPackage.SameTarget(expected, actual))
                        seen.Add(gamePath);
                    else
                        elsewhere++;
                }
                else if (!string.Equals(gamePath, actual, StringComparison.OrdinalIgnoreCase))
                {
                    other++;
                }
            }
        }
        int missingRedirects = 0, missingSwaps = 0;
        foreach (var (gamePath, source) in holding.Package.Paths)
        {
            if (seen.Contains(gamePath))
                continue;
            if (AppearanceCapturePlanner.IsLocalFile(source))
                missingRedirects++;
            else
                missingSwaps++;
        }
        if (elsewhere > 0)
            return $"{elsewhere} packaged game path(s) resolved elsewhere after the redraw";
        if (missingRedirects > 0)
        {
            settling = true;
            return $"{missingRedirects} of {holding.Package.Redirects} packaged redirect(s) were not reported for the actor after the redraw " +
                $"({missingSwaps} swap(s) not reported)";
        }
        log(prefix + $"Penumbra confirmed: {seen.Count} of {holding.Package.Paths.Count} packaged path(s) resolved to the package, 0 elsewhere, " +
            $"0 redirect(s) missing, {missingSwaps} swap(s) not reported, {other} other redirect(s); parent -1, temporary collection effective, " +
            "player's collection unchanged.");
        return null;
    }

    /// <summary>
    /// Removes the mod and deletes the collection (which removes its
    /// assignment), before the actor is undrawn and deleted. The removal is
    /// attempted even when the add was refused, since Penumbra may have added
    /// it anyway. <paramref name="actorIndex"/> is the actor's index while it
    /// is still provably RSC's, or −1 (then it is not read). Null when
    /// everything read back clean; otherwise what did not.
    /// </summary>
    public string? Release(int actorIndex)
    {
        if (held is not { } holding)
            return null;
        held = null;
        if (ReleaseByDeletion && holding.AssignAttempted && MayResolve(holding, actorIndex) is var assigned and not AssignmentReading.No)
            return ReleaseWithCollection(holding, actorIndex, assigned == AssignmentReading.Yes);
        var removal = Safe(() => penumbra.RemoveTemporaryMod(Tag, holding.Collection, Priority), HumanModRemoval.Failed);
        var modRemoved = removal == HumanModRemoval.Removed || (!holding.ModAdded && removal == HumanModRemoval.NotPresent);
        var deleted = Safe(() => penumbra.DeleteTemporaryCollection(holding.Collection), false);
        string actorOff = "not read";
        if (actorIndex >= 0)
        {
            actorOff = Safe(() => penumbra.CollectionFor(actorIndex), null) is not { } reading ? "no answer"
                : reading.EffectiveCollection != holding.Collection ? "yes" : "no";
        }
        var playerSame = Safe(() => penumbra.CollectionFor(holding.PlayerIndex), null) == holding.Player;
        if (!deleted)
            Residue++;
        log(prefix + $"Penumbra released: mod removed {(modRemoved ? removal == HumanModRemoval.Removed ? "yes" : "none present" : "no")}, " +
            $"collection deleted {(deleted ? "yes" : "no")}, actor off it {actorOff}, player's collection unchanged {(playerSame ? "yes" : "no")}.");
        if (!deleted)
            return "the temporary collection's deletion was not confirmed";
        if (!modRemoved)
            return "the temporary mod's removal was not confirmed";
        if (actorOff == "no")
            return "the actor still resolved to the temporary collection";
        if (actorOff == "no answer")
            return "the actor's collection could not be read after the deletion";
        return playerSame ? null : "the player's collection changed";
    }

    private enum AssignmentReading
    {
        No,
        Yes,
        Unknown,
    }

    /// <summary>Whether the actor may resolve to the collection: read when the actor is still provably RSC's, else unknown.</summary>
    private AssignmentReading MayResolve(Held holding, int actorIndex)
    {
        if (actorIndex < 0)
            return AssignmentReading.Unknown;
        return Safe(() => penumbra.CollectionFor(actorIndex), null) is not { ObjectValid: true } reading ? AssignmentReading.Unknown
            : reading.EffectiveCollection == holding.Collection ? AssignmentReading.Yes : AssignmentReading.No;
    }

    /// <summary>HV-3: the collection is deleted with the mod still in it, so no mod-setting event is raised for an actor that resolves to it.</summary>
    private string? ReleaseWithCollection(Held holding, int actorIndex, bool assignedSeen)
    {
        var deleted = Safe(() => penumbra.DeleteTemporaryCollection(holding.Collection), false);
        string actorOff = "not read";
        if (actorIndex >= 0)
        {
            actorOff = Safe(() => penumbra.CollectionFor(actorIndex), null) is not { } reading ? "no answer"
                : reading.EffectiveCollection != holding.Collection ? "yes" : "no";
        }
        var playerSame = Safe(() => penumbra.CollectionFor(holding.PlayerIndex), null) == holding.Player;
        if (!deleted)
            Residue++;
        var mod = !holding.ModAdded ? "none added" : !deleted ? "no" : assignedSeen ? "with the collection" : "with the collection only if it was still assigned (not provable)";
        log(prefix + $"Penumbra released: mod removed {mod}, collection deleted {(deleted ? "yes" : "no")}, actor off it {actorOff}, " +
            $"player's collection unchanged {(playerSame ? "yes" : "no")}.");
        if (!deleted)
            return "the temporary collection's deletion was not confirmed";
        if (holding.ModAdded && !assignedSeen)
            return "the temporary mod's removal could not be confirmed (the assignment could not be read)";
        if (actorOff == "no")
            return "the actor still resolved to the temporary collection";
        if (actorOff == "no answer")
            return "the actor's collection could not be read after the deletion";
        return playerSame ? null : "the player's collection changed";
    }

    private string Undo(string why)
    {
        var problem = Release(held?.ActorIndex ?? -1);
        return problem is null ? why : $"{why}; {problem}";
    }

    private static bool FolderReadable(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder) && Directory.Exists(folder);

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return fallback;
        }
    }

    private sealed class Held(Guid collection, int actorIndex, int playerIndex, HumanCollectionReading player, HumanActorPenumbraPackage package)
    {
        public Guid Collection { get; } = collection;
        public int ActorIndex { get; } = actorIndex;
        public int PlayerIndex { get; } = playerIndex;
        public HumanCollectionReading Player { get; } = player;
        public HumanActorPenumbraPackage Package { get; } = package;
        public bool ModAdded { get; set; }
        public bool AssignAttempted { get; set; }
    }
}
#endif
