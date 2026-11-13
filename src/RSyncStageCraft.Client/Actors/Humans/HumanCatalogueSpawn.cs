#if RSC_DEVELOPMENT
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Core.Actors.Humans;

// HV-5 M2 (Debug only): a spawn from one local catalogue entry. The entry is
// read and never modified; its stored files are held open for the actor's
// whole life and released last, only once the actor's deletion and the
// temporary collection's removal are both confirmed (amendment 1 §3).

/// <summary>
/// One held lease on a stored file: opened for reading, sharing only reading,
/// re-hashed through this handle, and kept open. Never names its path in text.
/// </summary>
public sealed class HumanCatalogueHold : IDisposable
{
    internal HumanCatalogueHold(string hash, long size, string filePath, FileStream stream)
    {
        Hash = hash;
        Size = size;
        FilePath = filePath;
        Stream = stream;
    }

    public string Hash { get; }

    public long Size { get; }

    /// <summary>The stored file's full path: given to Penumbra as a redirect target, never logged.</summary>
    internal string FilePath { get; }

    internal FileStream Stream { get; }

    /// <summary>The 8-character hash used in log lines.</summary>
    public string ShortHash => Hash[..8];

    public void Dispose() => Stream.Dispose();

    /// <summary>Never the path.</summary>
    public override string ToString() => $"held file {ShortHash}";
}

/// <summary>The game process, as the retention journal records it: its id and its start time (UTC ticks).</summary>
public readonly record struct HumanCatalogueProcess(int Id, long StartTicksUtc);

/// <summary>What the retention journal said at load.</summary>
public enum HumanCatalogueJournalState
{
    /// <summary>No journal.</summary>
    None,

    /// <summary>A journal of another game process: cleared.</summary>
    Cleared,

    /// <summary>A journal of this game process: HV-5 spawn and capture are refused until the game restarts.</summary>
    SameProcess,

    /// <summary>A journal that could not be read or cleared: refused as for <see cref="SameProcess"/> (fail closed).</summary>
    Unreadable,
}

/// <summary>
/// The retention journal, <c>retained.json</c> in the catalogue folder: the
/// hashes of stored files whose holds outlived a failed or unconfirmed step,
/// and the game process's id and start time. No path, name or label. Written
/// atomically (a temporary file, flushed, then renamed over the journal).
/// </summary>
public static class HumanCatalogueJournal
{
    public const string FileName = "retained.json";

    private const string Format = "hv5-retained-1";
    private const int MaximumBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    private sealed record JournalFile(string Format, int ProcessId, long ProcessStartTicksUtc, IReadOnlyList<string> Blobs);

    /// <summary>True when the journal now lists exactly <paramref name="hashes"/> for <paramref name="process"/>.</summary>
    public static bool TryWrite(string root, HumanCatalogueProcess process, IEnumerable<string> hashes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(hashes);
        var listed = hashes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (listed.Any(hash => !ContentHash.TryParse(hash, out _)))
            return false;
        var final = Path.Combine(root, FileName);
        var temporary = Path.Combine(root, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new JournalFile(Format, process.Id, process.StartTicksUtc, listed), Options);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, final, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return false;
        }
    }

    /// <summary>
    /// At load: no journal; a journal of another game process, which is then
    /// deleted; a journal of this one; or one that cannot be read or deleted.
    /// <paramref name="listed"/> is the number of hashes a readable journal lists.
    /// </summary>
    public static HumanCatalogueJournalState AtLoad(string root, HumanCatalogueProcess process, out int listed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        listed = 0;
        var file = Path.Combine(root, FileName);
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
                return HumanCatalogueJournalState.None;
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is 0 or > MaximumBytes)
                return HumanCatalogueJournalState.Unreadable;
            var journal = JsonSerializer.Deserialize<JournalFile>(File.ReadAllBytes(file), Options);
            if (journal is null || journal.Format != Format || journal.Blobs is null ||
                journal.Blobs.Any(hash => !ContentHash.TryParse(hash, out _)))
                return HumanCatalogueJournalState.Unreadable;
            listed = journal.Blobs.Count;
            if (journal.ProcessId == process.Id && journal.ProcessStartTicksUtc == process.StartTicksUtc)
                return HumanCatalogueJournalState.SameProcess;
            File.Delete(file);
            return HumanCatalogueJournalState.Cleared;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return HumanCatalogueJournalState.Unreadable;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// HV-5 M2's held leases (amendment 1 §3, owner decision D13 a). Thread safe.
/// <list type="bullet">
/// <item><see cref="ReleaseConfirmed"/>: the caller has confirmed both the
/// actor's deletion and the temporary collection's removal; the holds are
/// closed, last.</item>
/// <item><see cref="Retain"/>: either was not confirmed. The journal is
/// written first; then the holds stay open in the retained set, and no new
/// spawn is allowed. Nothing is retried.</item>
/// <item><see cref="DetachAll"/>: unload with holds still held. Each OS
/// handle is detached from its managed owner, so it stays open until the
/// game exits; a detach that fails is reported as "hold lost, file
/// unprotected" with the 8-character hash.</item>
/// </list>
/// There is never a journal-only fallback chosen here (D13: that is the owner's).
/// </summary>
public sealed class HumanCatalogueHoldSet
{
    private readonly object gate = new();
    private readonly List<HumanCatalogueHold> active = [];
    private readonly List<HumanCatalogueHold> retained = [];
    private readonly string root;
    private readonly HumanCatalogueProcess process;
    private readonly Action<string> log;
    private readonly string prefix;

    public HumanCatalogueHoldSet(string root, HumanCatalogueProcess process, Action<string> log, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = root;
        this.process = process;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
    }

    /// <summary>Test seam: detaches one handle so that disposal no longer closes it (production: <see cref="DetachHandle"/>).</summary>
    internal Func<FileStream, bool> Detacher { get; set; } = DetachHandle;

    /// <summary>Test seam: the same-process marker (production: <see cref="HumanCatalogueRetentionMarker.TrySet"/>).</summary>
    internal Func<HumanCatalogueProcess, int, bool> MarkerWriter { get; set; } = HumanCatalogueRetentionMarker.TrySet;

    /// <summary>Test seam: the journal write (production: <see cref="HumanCatalogueJournal.TryWrite"/>).</summary>
    internal Func<string, HumanCatalogueProcess, IEnumerable<string>, bool> JournalWriter { get; set; } = HumanCatalogueJournal.TryWrite;

    /// <summary>Holds of the actor that exists or is being spawned.</summary>
    public int Active
    {
        get
        {
            lock (gate)
                return active.Count;
        }
    }

    /// <summary>Holds kept past a failed or unconfirmed step.</summary>
    public int Retained
    {
        get
        {
            lock (gate)
                return retained.Count;
        }
    }

    /// <summary>Takes the holds of a new spawn. Refused (false) while any hold is active or retained.</summary>
    public bool Take(IReadOnlyList<HumanCatalogueHold> holds)
    {
        ArgumentNullException.ThrowIfNull(holds);
        lock (gate)
        {
            if (active.Count > 0 || retained.Count > 0)
                return false;
            active.AddRange(holds);
            return true;
        }
    }

    /// <summary>Both confirmations hold: the active holds are closed, last.</summary>
    public void ReleaseConfirmed()
    {
        lock (gate)
        {
            if (active.Count == 0)
                return;
            var count = active.Count;
            foreach (var hold in active)
                Close(hold);
            active.Clear();
            log(prefix + $"holds released last: {count} file(s), after the actor's deletion and the collection's removal were confirmed.");
        }
    }

    /// <summary>A confirmation is missing: the journal first, then the active holds stay open in the retained set.</summary>
    public void Retain(string why)
    {
        ArgumentNullException.ThrowIfNull(why);
        lock (gate)
        {
            if (active.Count == 0)
                return;
            var marked = Mark(active.Count + retained.Count);
            var written = Write(active.Concat(retained));
            retained.AddRange(active);
            active.Clear();
            log(prefix + $"holds retained: {retained.Count} file(s) stay open and protected ({why}); retention journal " +
                $"{(written ? "written" : "NOT WRITTEN")}; same-process marker {(marked ? "set" : "NOT SET")}; no HV-5 spawn until unload; " +
                "nothing is retried.");
        }
    }

    /// <summary>Unload with holds still held: the journal again, then every handle detached and left open until the game exits.</summary>
    public void DetachAll()
    {
        lock (gate)
        {
            var all = retained.Concat(active).ToList();
            if (all.Count == 0)
                return;
            var marked = Mark(all.Count);
            var written = Write(all);
            var kept = 0;
            var lost = new List<string>();
            foreach (var hold in all)
            {
                bool detached;
                try
                {
                    detached = Detacher(hold.Stream);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    detached = false;
                }
                if (detached)
                {
                    kept++;
                    continue;
                }
                lost.Add(hold.ShortHash);
                Close(hold);
            }
            retained.Clear();
            active.Clear();
            log(prefix + $"unload: {kept} held file(s) detached and left open until the game exits; retention journal " +
                $"{(written ? "written" : "NOT WRITTEN")}; same-process marker {(marked ? "set" : "NOT SET")}; " +
                (written || marked ? "the next load in this game process refuses HV-5 spawn and capture."
                    : "NEITHER RECORD WAS KEPT: a reload in this game process would not refuse; restart the game before any HV-5 step."));
            foreach (var hash in lost)
                log(prefix + $"hold lost, file unprotected ({hash}).");
        }
    }

    /// <summary>
    /// Marks the stream's OS handle as no longer owned, so neither disposal
    /// nor finalization closes it: the operating system keeps it open until
    /// the game process exits. False when the handle was already closed.
    /// </summary>
    public static bool DetachHandle(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var handle = stream.SafeFileHandle;
        if (handle.IsInvalid || handle.IsClosed)
            return false;
        handle.SetHandleAsInvalid();
        return handle.IsClosed;
    }

    private bool Mark(int count)
    {
        try
        {
            return MarkerWriter(process, count);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private bool Write(IEnumerable<HumanCatalogueHold> holds)
    {
        try
        {
            return JournalWriter(root, process, holds.Select(hold => hold.Hash).ToList());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static void Close(HumanCatalogueHold hold)
    {
        try
        {
            hold.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// HV-5 M2: one spawn's holds tied to the actor's cleanup (amendment 1 §3).
/// <see cref="Start"/> takes the prepared holds and spawns with the plan;
/// <see cref="Settle"/>, after every step, releases the holds last once the
/// actor is gone, if its deletion and the temporary collection's removal by
/// its receipt were both confirmed, and otherwise retains them (with the
/// journal). Game thread.
/// </summary>
public sealed class HumanCatalogueSpawnSession
{
    private readonly HumanActorBaseline baseline;
    private readonly HumanActorPenumbraStage penumbra;
    private readonly HumanCatalogueHoldSet holds;
    private (int Residue, int Unconfirmed, int CollectionResidue) atSpawn;

    public HumanCatalogueSpawnSession(HumanActorBaseline baseline, HumanActorPenumbraStage penumbra, HumanCatalogueHoldSet holds)
    {
        this.baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        this.penumbra = penumbra ?? throw new ArgumentNullException(nameof(penumbra));
        this.holds = holds ?? throw new ArgumentNullException(nameof(holds));
    }

    /// <summary>The spawn whose holds are active, or null.</summary>
    public HumanCatalogueSpawnReady? Current { get; private set; }

    /// <summary>
    /// Takes <paramref name="ready"/>'s holds and spawns with
    /// <paramref name="plan"/>. False, with every hold of
    /// <paramref name="ready"/> closed, when an actor exists or any hold is
    /// active or retained; otherwise the baseline's answer (the holds then
    /// follow <see cref="Settle"/>).
    /// </summary>
    public bool Start(HumanCatalogueSpawnReady ready, HumanActorSpawnPlan plan)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(plan);
        if (Current is not null || baseline.LiveCount != 0 || !holds.Take(ready.Holds))
        {
            ready.Abandon();
            return false;
        }
        Current = ready;
        atSpawn = (baseline.Residue, baseline.UnconfirmedDeletions, penumbra.Residue);
        var spawned = baseline.Spawn(plan);
        Settle();
        return spawned;
    }

    /// <summary>
    /// After every step: nothing while the actor exists. Once it is gone, the
    /// holds are released if no new residue and no unconfirmed deletion were
    /// recorded (the actor's deletion is confirmed) and no collection is held
    /// or was left unconfirmed (its removal by receipt answered success);
    /// otherwise they are retained.
    /// </summary>
    public void Settle()
    {
        if (Current is null || baseline.LiveCount != 0)
            return;
        Current = null;
        var actorDeleted = baseline.Residue == atSpawn.Residue && baseline.UnconfirmedDeletions == atSpawn.Unconfirmed;
        var collectionRemoved = !penumbra.Holding && penumbra.Residue == atSpawn.CollectionResidue;
        if (actorDeleted && collectionRemoved)
            holds.ReleaseConfirmed();
        else
            holds.Retain(!actorDeleted ? "the actor's deletion was not confirmed" : "the collection's removal was not confirmed");
    }

    /// <summary>The package of the spawn in progress, for the Penumbra stage's <see cref="HumanActorPenumbraStage.PackageSource"/> (BC-3).</summary>
    public (HumanActorPenumbraPackage? Package, string? Meta, string? Refusal) CurrentPackage() =>
        Current is { } spawn ? (spawn.Package, spawn.Entry.Manipulations, null) : (null, null, "no entry is being spawned");
}

/// <summary>What a spawn reads at its start, on the framework thread: the versions now, and S-1 (the player's resolved resources and the mod root).</summary>
public sealed record HumanCatalogueSpawnRead(HumanCatalogueVersions Versions, string? ModDirectory, IReadOnlyList<AppearanceResourceEntry>? PlayerResources)
{
    /// <summary>Never the paths.</summary>
    public override string ToString() => $"spawn read ({PlayerResources?.Count ?? 0} resource entries)";
}

/// <summary>A prepared spawn: the entry, its holds, the package built from them, phase 1's required paths and whether the deformer is packaged.</summary>
public sealed record HumanCatalogueSpawnReady(
    HumanCatalogueEntry Entry,
    IReadOnlyList<HumanCatalogueHold> Holds,
    HumanActorPenumbraPackage Package,
    IReadOnlySet<string> PhaseOneRequired,
    bool Deformer)
{
    /// <summary>Never the paths.</summary>
    public override string ToString() => $"prepared spawn of entry {Entry.ShortId} ({Holds.Count} held file(s))";

    /// <summary>Closes every hold (a spawn that never started).</summary>
    public void Abandon()
    {
        foreach (var hold in Holds)
        {
            try
            {
                hold.Dispose();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }
    }
}

/// <summary>
/// HV-5 M2: everything a spawn needs before anything is created, off the
/// framework thread (r1 §7.2 with amendment 1):
/// <list type="number">
/// <item>the label, and the one entry with it, read only;</item>
/// <item>owner decision D7: any recorded version that differs from the
/// running game's or a provider's refuses;</item>
/// <item>HV-5 M3: an entry with its own Customize+ profile refuses the test
/// scale (D-M3-7) and needs Customize+ loaded;</item>
/// <item>the entry's skeleton summary must be comparable;</item>
/// <item>D3-a and D15 (S-1): the deformer the player's collection resolves
/// now must have the stored deformer's content hash, or both must have none;
/// otherwise "deformer differs from the current one (GAP-K4)";</item>
/// <item>one held lease per stored file, each re-hashed;</item>
/// <item>the package: each stored game path to its held file, each swap as stored.</item>
/// </list>
/// Any refusal closes every hold taken, and nothing is created.
/// </summary>
public static class HumanCatalogueSpawnPreparation
{
    private const int BufferBytes = 81920;

    public static async Task<(HumanCatalogueSpawnReady? Ready, string? Refusal)> PrepareAsync(
        string? labelText, bool testScale, HumanCatalogueStore store, HumanCatalogueSpawnRead current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(current);
        if (HumanCatalogueLabel.Normalize(labelText, out var labelRefusal) is not { } label)
            return (null, labelRefusal);
        if (store.FindEntry(label, out var unknown) is not { } entry)
            return (null, "the label is unknown: " + unknown);
        if (VersionMismatch(entry.Versions, current.Versions.Bounded()) is { } stale)
            return (null, stale);
        if (entry.Profile is not null && testScale)
            return (null, "the test scale and the entry's own Customize+ profile are mutually exclusive; spawn it without the test scale");
        if (entry.Profile is not null && current.Versions.CustomizePlus is null)
            return (null, "the entry's Customize+ profile needs Customize+ loaded");
        if (testScale && current.Versions.CustomizePlus is null)
            return (null, "the test scale needs Customize+ loaded");
        if (!HumanCatalogueSkeletonSummary.Comparable(entry.SkeletonSummary))
            return (null, "the entry's skeleton summary cannot be compared");
        if (await DeformerMismatchAsync(entry, current, cancellationToken).ConfigureAwait(false) is { } deformer)
            return (null, deformer);

        var holds = new List<HumanCatalogueHold>(entry.Blobs.Count);
        var complete = false;
        try
        {
            foreach (var blob in entry.Blobs)
            {
                var (hold, problem) = await store.HoldAsync(blob, cancellationToken).ConfigureAwait(false);
                if (hold is null)
                    return (null, $"a stored file of the entry {problem} ({blob.Hash[..8]}); it was kept as it is, and nothing was created");
                holds.Add(hold);
            }
            var byHash = holds.ToDictionary(hold => hold.Hash, StringComparer.Ordinal);
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int redirects = 0, swaps = 0;
            foreach (var path in entry.Paths)
            {
                if (path.Blob is { } hash)
                {
                    paths.Add(path.GamePath, byHash[hash].FilePath);
                    redirects++;
                }
                else
                {
                    paths.Add(path.GamePath, path.Swap!);
                    swaps++;
                }
            }
            var required = PhaseOneMembers(entry);
            var ready = new HumanCatalogueSpawnReady(entry, holds, new HumanActorPenumbraPackage(paths, redirects, swaps, 0, 0), required,
                entry.Skeleton?.Deformer ?? false);
            complete = true;
            return (ready, null);
        }
        finally
        {
            if (!complete)
            {
                foreach (var hold in holds)
                    hold.Dispose();
            }
        }
    }

    /// <summary>
    /// HV-5 M3 (BC-6), on the game thread just before the plan is made: the
    /// entry's own profile as the payload, re-checked against its stored hash
    /// and counts; null with no refusal for an entry without a profile, and
    /// null with the refusal when the profile does not check or the test
    /// scale was also asked for.
    /// </summary>
    public static HumanCustomizePlusPayload? ProfilePayload(HumanCatalogueEntry entry, bool testScale, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(entry);
        refusal = null;
        if (entry.Profile is not { } profile)
            return null;
        if (testScale)
            refusal = "the test scale and the entry's own Customize+ profile are mutually exclusive";
        else if (entry.ProfileCounts is not { } counts || entry.ProfileHash is null ||
                 !string.Equals(HumanCatalogueEntry.HashOf(profile), entry.ProfileHash, StringComparison.Ordinal))
            refusal = "the entry's Customize+ profile does not match its stored hash";
        else
            return new HumanCustomizePlusPayload(profile, counts.Describe());
        return null;
    }

    /// <summary>
    /// U-1: phase 1's required redirects, the stored skeleton-group members
    /// the copy loads before Glamourer whatever it wears: the base partial
    /// skeleton's files and the deformer. Face, hair and gear skeletons are
    /// the saved look's, confirmed in phase 2.
    /// </summary>
    public static IReadOnlySet<string> PhaseOneMembers(HumanCatalogueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in entry.Skeleton?.Members ?? [])
        {
            if (SkeletonResourcePaths.TryClassify(member, out var path) &&
                (path.Role == SkeletonResourceRole.Deformer || path.Slot == SkeletonPartialSlot.Base))
                required.Add(member);
        }
        return required;
    }

    /// <summary>D7: null when every recorded version equals the running one (none recorded equals none running); otherwise the refusal.</summary>
    public static string? VersionMismatch(HumanCatalogueVersions recorded, HumanCatalogueVersions now)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(now);
        foreach (var (name, then, running) in new[]
                 {
                     ("game", recorded.Game, now.Game), ("Penumbra", recorded.Penumbra, now.Penumbra),
                     ("Glamourer", recorded.Glamourer, now.Glamourer), ("Customize+", recorded.CustomizePlus, now.CustomizePlus),
                 })
        {
            if (!string.Equals(then, running, StringComparison.Ordinal))
                return $"the recorded {name} version differs ({then ?? "none"} recorded, {running ?? "none"} now); an entry is spawned only with the versions it was captured with";
        }
        return null;
    }

    /// <summary>D3-a and D15: null when the player's current deformer and the stored one have the same content, or neither exists.</summary>
    internal static async Task<string?> DeformerMismatchAsync(HumanCatalogueEntry entry, HumanCatalogueSpawnRead current, CancellationToken cancellationToken)
    {
        const string differs = "deformer differs from the current one (GAP-K4)";
        var stored = entry.Paths.FirstOrDefault(path => path.GamePath == SkeletonResourcePaths.DeformerGamePath)?.Blob;
        if (current.PlayerResources is not { } resources)
            return "the player's resource paths could not be read, so the deformer cannot be compared (GAP-K4)";
        if (resources.Any(resource => resource?.ActualPath is not { Length: > 0 } || resource.GamePaths is null))
            return "the player's resource paths are incomplete, so the deformer cannot be compared (GAP-K4)";
        var actuals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in AppearanceCapturePlanner.CanonicalizeReported(resources))
        {
            if (resource.GamePaths.Any(gamePath => string.Equals(gamePath, SkeletonResourcePaths.DeformerGamePath, StringComparison.OrdinalIgnoreCase)))
                actuals.Add(resource.ActualPath);
        }
        string? playing = null;
        if (actuals.Count > 1)
            return "the player's deformer has more than one source, so it cannot be compared (GAP-K4)";
        if (actuals.Count == 1)
        {
            var actual = actuals.First();
            if (!AppearanceCapturePlanner.IsLocalFile(actual))
            {
                if (!string.Equals(actual, SkeletonResourcePaths.DeformerGamePath, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(actual, SkeletonResourcePaths.ReportedDeformerGamePath, StringComparison.OrdinalIgnoreCase))
                    return "the player's deformer is swapped to another game path, so it cannot be compared (GAP-K4)";
            }
            else
            {
                if (AppearanceCapturePlanner.ContainedFile(current.ModDirectory, actual) is not { } contained)
                    return "the player's deformer is not an existing file inside the mod folder, so it cannot be compared (GAP-K4)";
                playing = await HashAsync(contained, cancellationToken).ConfigureAwait(false);
                if (playing is null)
                    return "the player's deformer could not be read, so it cannot be compared (GAP-K4)";
            }
        }
        return string.Equals(stored, playing, StringComparison.Ordinal) ? null : differs;
    }

    /// <summary>The file's SHA-256 (lower-case hex) within the blob limit, or null.</summary>
    private static async Task<string?> HashAsync(string file, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > AppearanceLimits.MaximumBlobBytes)
                    return null;
                sha.AppendData(buffer, 0, read);
            }
            return total == 0 ? null : Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// HV-5 M2: the actor's skeleton summary against the entry's (amendment 1
/// §1.2, spawn item 2). Both sides are normalized as the capture stored the
/// player's: the reader's prefix dropped and the hash of the resource file
/// name removed (it can name a local path), leaving per partial the bone
/// count and the hashes of the bone names, parents and reference pose.
/// Compared exactly; a difference is described as
/// <see cref="HumanPuppetT3Observations.CompareSkeletons"/> describes it
/// (field names only, no values).
/// </summary>
public static class HumanCatalogueSkeletonSummary
{
    /// <summary>One reader's facts (<c>"&lt;who&gt; skeleton …"</c>) as stored lines (<c>"skeleton …: …"</c>).</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<KeyValuePair<string, string>> facts, string who)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(who);
        var lead = who + " ";
        return facts.Select(fact => (fact.Key.StartsWith(lead, StringComparison.Ordinal) ? fact.Key[lead.Length..] : fact.Key) + ": " +
            HumanCatalogueEntryCodec.WithoutFileHash(fact.Value)).ToList();
    }

    /// <summary>The reader's value for a partial slot that has no skeleton resource.</summary>
    private const string NoResource = "no resource";

    /// <summary>
    /// True when the summary is exactly the reader's complete shape: the line
    /// <c>skeleton partials: N</c> (N at least one), then one line per slot in
    /// slot order, <c>skeleton 0: bones …</c> for the base slot and, for every
    /// later slot, <c>skeleton i: bones …</c> or exactly
    /// <c>skeleton i: no resource</c> (a slot the game left empty), and
    /// nothing else. A missing, repeated or out-of-order slot, a base slot
    /// with no resource, any other value, or any <c>skeleton: …</c> line (no
    /// draw object, none, unreadable) is not comparable.
    /// </summary>
    public static bool Comparable(IReadOnlyList<string> summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        const string countLead = "skeleton partials: ";
        if (summary.Count < 2 || summary[0] is not { } first || !first.StartsWith(countLead, StringComparison.Ordinal) ||
            Count(first.AsSpan(countLead.Length)) is not { } count || summary.Count != count + 1)
            return false;
        for (var index = 0; index < count; index++)
        {
            var lead = $"skeleton {index}: ";
            if (summary[index + 1] is not { } line || !line.StartsWith(lead, StringComparison.Ordinal))
                return false;
            var value = line.AsSpan(lead.Length);
            if (Bones(value))
                continue;
            if (index == 0 || !value.Equals(NoResource, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>One to four ASCII digits with no leading zero, as a count of at least one; otherwise null.</summary>
    private static int? Count(ReadOnlySpan<char> text)
    {
        if (text.Length is < 1 or > 4 || text[0] == '0')
            return null;
        var count = 0;
        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
                return null;
            count = count * 10 + (character - '0');
        }
        return count;
    }

    /// <summary><c>bones B</c> (B one or more ASCII digits), alone or followed by <c>", "</c> and the hashed fields.</summary>
    private static bool Bones(ReadOnlySpan<char> value)
    {
        const string lead = "bones ";
        if (!value.StartsWith(lead, StringComparison.Ordinal))
            return false;
        var rest = value[lead.Length..];
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            digits++;
        return digits > 0 && (digits == rest.Length || rest[digits..].StartsWith(", ", StringComparison.Ordinal));
    }

    /// <summary>Null when the actor's lines equal the entry's; otherwise why not.</summary>
    public static string? Mismatch(IReadOnlyList<string> stored, IReadOnlyList<string> actor)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(actor);
        if (stored.SequenceEqual(actor, StringComparer.Ordinal))
            return null;
        return "the actor's skeleton summary differs from the entry's (" +
            HumanPuppetT3Observations.CompareSkeletons(Facts(actor, "puppet"), Facts(stored, "player")).TrimEnd('.') + ")";
    }

    /// <summary>
    /// U-1, phase 1: null when the actor's base partial skeleton (partial 0,
    /// the game's base skeleton) equals the entry's; otherwise why not. The
    /// partial count and the other partials (face, hair, gear) are the saved
    /// look's and are compared whole after Glamourer by <see cref="Mismatch"/>.
    /// </summary>
    public static string? BaseMismatch(IReadOnlyList<string> stored, IReadOnlyList<string> actor)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(actor);
        const string lead = "skeleton 0: ";
        var mine = stored.FirstOrDefault(line => line.StartsWith(lead, StringComparison.Ordinal));
        if (mine is null)
            return "the entry has no base skeleton line";
        var theirs = actor.FirstOrDefault(line => line.StartsWith(lead, StringComparison.Ordinal));
        if (theirs is null)
            return "the actor's base skeleton is not read yet";
        if (string.Equals(mine, theirs, StringComparison.Ordinal))
            return null;
        return "the actor's base skeleton differs from the entry's (" +
            HumanPuppetT3Observations.CompareSkeletons(Facts(["skeleton partials: 1", theirs], "puppet"), Facts(["skeleton partials: 1", mine], "player")).TrimEnd('.') + ")";
    }

    private static List<KeyValuePair<string, string>> Facts(IReadOnlyList<string> lines, string who) =>
        lines.Select(line =>
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            return colon < 0 ? new KeyValuePair<string, string>(who + " " + line, string.Empty)
                : new KeyValuePair<string, string>(who + " " + line[..colon], line[(colon + 2)..]);
        }).ToList();
}
/// <summary>
/// HV-5 M2 (U-5): the deformer comparison of the preparation, again, between
/// the Glamourer apply and the show. <see cref="Begin"/> (game thread) reads
/// the player's resources once and starts the content comparison off the
/// framework thread; <see cref="Poll"/> (game thread, before the show) answers
/// it, then reads the resources again and refuses if the deformer's source
/// (its path, size and write time) changed since <see cref="Begin"/>. A
/// refusal halts the actor while it is hidden. Nothing here is logged with a
/// path.
/// </summary>
public sealed class HumanCatalogueDeformerRecheck : IHumanActorShowCheck
{
    private const string Changed = "the player's deformer changed after the re-check began (GAP-K4)";

    private readonly HumanCatalogueEntry entry;
    private readonly Func<HumanCatalogueSpawnRead?> readNow;
    private readonly CancellationToken cancellationToken;
    private Task<string?>? comparison;
    private string? source;

    public HumanCatalogueDeformerRecheck(HumanCatalogueEntry entry, Func<HumanCatalogueSpawnRead?> readNow, CancellationToken cancellationToken)
    {
        this.entry = entry ?? throw new ArgumentNullException(nameof(entry));
        this.readNow = readNow ?? throw new ArgumentNullException(nameof(readNow));
        this.cancellationToken = cancellationToken;
    }

    /// <summary>Starts a new comparison (r2: again after a redraw seen while visible); <see cref="Poll"/> answers the latest one.</summary>
    public void Begin()
    {
        source = null;
        if (readNow() is not { } read)
        {
            comparison = Task.FromResult<string?>("the player's resources could not be read again, so the deformer cannot be compared (GAP-K4)");
            return;
        }
        source = Source(read);
        var token = cancellationToken;
        comparison = Task.Run(() => HumanCatalogueSpawnPreparation.DeformerMismatchAsync(entry, read, token), token);
    }

    public string? Poll(out bool pending)
    {
        pending = false;
        if (comparison is not { } task)
            return "the deformer re-check was not begun";
        if (!task.IsCompleted)
        {
            pending = true;
            return null;
        }
        if (task.IsCanceled)
            return "the deformer re-check was cancelled";
        if (task.IsFaulted)
            return $"the deformer re-check failed ({task.Exception?.InnerException?.GetType().Name ?? "unknown"})";
        if (task.Result is { } refusal)
            return refusal;
        return readNow() is { } again && string.Equals(Source(again), source, StringComparison.Ordinal) ? null : Changed;
    }

    /// <summary>The deformer's sources as reported, each with its size and write time when it is a local file; memory only.</summary>
    private static string? Source(HumanCatalogueSpawnRead read)
    {
        if (read.PlayerResources is not { } resources || resources.Any(resource => resource?.ActualPath is null || resource.GamePaths is null))
            return null;
        var parts = new List<string>();
        foreach (var resource in AppearanceCapturePlanner.CanonicalizeReported(resources))
        {
            if (!resource.GamePaths.Any(gamePath => string.Equals(gamePath, SkeletonResourcePaths.DeformerGamePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            var part = resource.ActualPath;
            if (AppearanceCapturePlanner.IsLocalFile(part))
            {
                try
                {
                    var info = new FileInfo(part);
                    part += info.Exists ? $"|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "|missing";
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    part += "|unreadable";
                }
            }
            parts.Add(part);
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }
}
/// <summary>
/// HV-5 M2 (U-6): the spawn steps. The test scale is its own step,
/// <c>spawn-test-scale &lt;label&gt;</c>, never a suffix, so every valid label
/// (including one that ends in " test-scale") stays addressable by
/// <c>spawn &lt;label&gt;</c>; the label rule is unchanged.
/// </summary>
public static class HumanCatalogueSpawnCommand
{
    public const string Spawn = "spawn";
    public const string SpawnTestScale = "spawn-test-scale";

    /// <summary>True for either spawn step with a non-empty rest; <paramref name="label"/> is the rest as typed (the label rule trims it).</summary>
    public static bool TryParse(string? step, out string label, out bool testScale)
    {
        label = string.Empty;
        testScale = false;
        if (step is null)
            return false;
        foreach (var (verb, scaled) in new[] { (SpawnTestScale, true), (Spawn, false) })
        {
            var lead = verb + " ";
            if (step.StartsWith(lead, StringComparison.Ordinal) && step.Length > lead.Length)
            {
                label = step[lead.Length..];
                testScale = scaled;
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// HV-5 M2 (item 7): the same-process record of retained holds, independent of
/// the disk. An environment variable of the game process (never written to
/// the system or user environment) holds the process's id and start time and
/// the number of holds retained; it outlives a plugin unload and reload in
/// that process and ends with it. It is set before the journal is written, so
/// a reload in the same process refuses spawn and capture even when both
/// journal writes fail. No path, name or hash.
/// </summary>
public static class HumanCatalogueRetentionMarker
{
    public const string Variable = "RSC_HV5_RETAINED";
    private const string Format = "hv5-retained-1";

    /// <summary>True when the marker now names <paramref name="process"/> (with at least <paramref name="count"/> holds).</summary>
    public static bool TrySet(HumanCatalogueProcess process, int count)
    {
        try
        {
            var total = IsSet(process, out var earlier) ? Math.Max(earlier, count) : count;
            Environment.SetEnvironmentVariable(Variable, $"{Format} {process.Id} {process.StartTicksUtc} {total}", EnvironmentVariableTarget.Process);
            return IsSet(process, out _);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>True when the marker names this game process; <paramref name="count"/> is the number it records.</summary>
    public static bool IsSet(HumanCatalogueProcess process, out int count)
    {
        count = 0;
        var value = Environment.GetEnvironmentVariable(Variable, EnvironmentVariableTarget.Process);
        if (value is null)
            return false;
        var parts = value.Split(' ');
        if (parts.Length != 4 || parts[0] != Format || !int.TryParse(parts[1], out var id) || !long.TryParse(parts[2], out var start) ||
            !int.TryParse(parts[3], out count))
        {
            // A marker this format does not read: fail closed for this process.
            count = 0;
            return true;
        }
        return id == process.Id && start == process.StartTicksUtc;
    }
}
#endif
