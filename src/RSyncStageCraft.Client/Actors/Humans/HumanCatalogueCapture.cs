#if RSC_DEVELOPMENT
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// HV-5 M1: one read of the owner's own character, copied on the framework
/// thread into an RSC-owned snapshot (capture reads C1 to C7 and C9 of r1
/// §4.1; C8, the Customize+ profile, is never read in M1). Memory only:
/// <see cref="PlayerObject"/> and <see cref="ModDirectory"/> are used for the
/// re-check and containment and are never stored.
/// </summary>
/// <param name="ModDirectory">C1, Penumbra's mod root.</param>
/// <param name="Resources">C2, Penumbra's resolved resources for the player.</param>
/// <param name="GlamourerState">C4, Glamourer's state text with key 0 (opaque).</param>
/// <param name="GlamourerReference">C7, Glamourer's state as a JSON object with key 0 (the D8 (b) readback reference; not decoded).</param>
/// <param name="Manipulations">C3, Penumbra's manipulations for the player (opaque).</param>
/// <param name="Race">C5, the race, clan and gender bytes.</param>
/// <param name="BodyType">C5, the body-type byte.</param>
/// <param name="SkeletonSummary">The player's own skeleton summary (hashes only), as the T3 observation reads it.</param>
/// <param name="Versions">C9.</param>
/// <param name="PlayerObject">The exact player object (index, address, id), for the re-check only.</param>
public sealed record HumanCatalogueRead(
    string? ModDirectory,
    IReadOnlyList<AppearanceResourceEntry> Resources,
    string GlamourerState,
    string GlamourerReference,
    string Manipulations,
    AppearanceCharacterRace Race,
    byte BodyType,
    IReadOnlyList<string> SkeletonSummary,
    HumanCatalogueVersions Versions,
    string PlayerObject)
{
    /// <summary>Never the read data.</summary>
    public override string ToString() => $"catalogue read ({Resources.Count} resource entries)";
}

/// <summary>A read, or why there is none (no path or data in the reason).</summary>
public sealed record HumanCatalogueReadResult(HumanCatalogueRead? Read, string? Refusal)
{
    public static HumanCatalogueReadResult Refused(string refusal) => new(null, refusal);
}

/// <summary>A planned entry: the package, the files to store (with their local sources, memory only), the group and the counts.</summary>
public sealed record HumanCataloguePlan(
    IReadOnlyList<HumanCataloguePath> Paths,
    IReadOnlyList<(AppearanceBlobRef Blob, string Source)> Files,
    HumanCatalogueSkeleton? Skeleton,
    HumanCatalogueCounts Counts)
{
    /// <summary>Never the local sources.</summary>
    public override string ToString() => $"catalogue plan ({Paths.Count} game path(s), {Files.Count} file(s))";
}

/// <summary>
/// Plans an entry from one read, under the M1 decisions: D1 (every
/// resolved file beneath the mod root, any type, plus swaps and
/// manipulations, Debug-local only), D3-a (the complete skeleton group,
/// including a redirected <c>human.pbd</c>, through the existing group
/// checks), and D9 (at most 1,024 game paths). Nothing is ever dropped from
/// the skeleton group: if it is refused, the capture is refused.
/// </summary>
public static class HumanCataloguePlanner
{
    /// <summary>D3-a: skeleton groups and the deformer, for this local planner only.</summary>
    private static readonly AppearanceCaptureOptions GroupOptions = new(AllowSkeletonGroups: true, AllowDeformer: true);

    private const int BufferBytes = 81920;

    public static async Task<(HumanCataloguePlan? Plan, string? Refusal)> PlanAsync(HumanCatalogueRead read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read.Resources is null)
            return (null, "Penumbra reported no resources");
        foreach (var entry in read.Resources)
        {
            if (entry is null || string.IsNullOrEmpty(entry.ActualPath) || entry.GamePaths is null)
                return (null, "Penumbra reported an incomplete resource entry");
            if (entry.GamePaths.Any(string.IsNullOrEmpty))
                return (null, "Penumbra reported an empty game path");
        }
        // The one reported deformer spelling, mapped once before anything else (as the existing capture does).
        var resources = AppearanceCapturePlanner.CanonicalizeReported(read.Resources);

        // Every source of every game path (case ignored, so two spellings of one path are one path).
        var origins = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in resources)
        {
            foreach (var gamePath in entry.GamePaths)
            {
                if (!origins.TryGetValue(gamePath, out var actuals))
                    origins[gamePath] = actuals = new HashSet<string>(StringComparer.Ordinal);
                actuals.Add(entry.ActualPath);
            }
        }

        int vanilla = 0, outside = 0;
        var swaps = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var redirects = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var groupLocal = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var groupSwapped = new SortedSet<string>(StringComparer.Ordinal);
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);
        var canonicalOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashed = new Dictionary<string, (AppearanceBlobRef? Blob, AppearanceCaptureExclusion? Problem)>(StringComparer.Ordinal);

        foreach (var entry in resources.OrderBy(entry => entry.ActualPath, StringComparer.Ordinal))
        {
            var gamePaths = entry.GamePaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (!AppearanceCapturePlanner.IsLocalFile(entry.ActualPath))
            {
                foreach (var gamePath in gamePaths)
                {
                    if (string.Equals(gamePath, entry.ActualPath, StringComparison.OrdinalIgnoreCase))
                    {
                        vanilla++;
                        continue;
                    }
                    if (AppearanceCapturePlanner.IsGroupPath(gamePath, out var member))
                    {
                        (member ? groupSwapped : unsupported).Add(gamePath);
                        continue;
                    }
                    if (!HumanCatalogueEntryCodec.IsStoredGamePath(gamePath) || !HumanCatalogueEntryCodec.IsStoredGamePath(entry.ActualPath))
                        return (null, $"a swap cannot be stored ({Shown(gamePath)})");
                    swaps[gamePath] = entry.ActualPath;
                }
                continue;
            }
            // A redirect: only a file provably beneath the mod root is ever read (as the existing capture checks it).
            var canonical = AppearanceCapturePlanner.ContainedFile(read.ModDirectory, entry.ActualPath);
            foreach (var gamePath in gamePaths)
            {
                if (AppearanceCapturePlanner.IsGroupPath(gamePath, out var member))
                {
                    if (!member)
                    {
                        unsupported.Add(gamePath);
                        continue;
                    }
                    if (!groupLocal.TryGetValue(gamePath, out var actuals))
                        groupLocal[gamePath] = actuals = new SortedSet<string>(StringComparer.Ordinal);
                    actuals.Add(entry.ActualPath);
                    continue;
                }
                if (canonical is null)
                {
                    // Left out and counted, as HV-2 does: a file outside the mod root is never read.
                    outside++;
                    continue;
                }
                if (!string.Equals(Path.GetExtension(canonical), Path.GetExtension(gamePath), StringComparison.OrdinalIgnoreCase))
                    // Refused, never dropped: a file of the wrong type would change what the game loads.
                    return (null, $"a mod file's type does not match its game path ({Shown(gamePath)})");
                if (!HumanCatalogueEntryCodec.IsStoredGamePath(gamePath))
                    return (null, $"a game path cannot be stored ({Shown(gamePath)})");
                canonicalOf[entry.ActualPath] = canonical;
                if (!redirects.TryGetValue(gamePath, out var redirected))
                    redirects[gamePath] = redirected = new SortedSet<string>(StringComparer.Ordinal);
                redirected.Add(canonical);
            }
        }

        // Skeleton data that is not a recognised group member is never dropped: the capture is refused.
        if (unsupported.Count > 0)
            return (null, $"skeleton data that cannot be stored is redirected or swapped ({unsupported.Min})");

        // Two spellings of one game path (differing only in case) are one path to the game: refused, never merged.
        // (One spelling with two sources is a conflict, refused below or by the group checks.)
        var spellings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gamePath in redirects.Keys.Concat(swaps.Keys).Concat(groupLocal.Keys).Concat(groupSwapped).Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (!spellings.Add(gamePath))
                return (null, $"one game path is reported in two spellings ({Shown(gamePath)})");
        }

        // The skeleton group, all or nothing, through the existing group checks (structure, race, limits).
        var members = new List<(string GamePath, AppearanceBlobRef Blob, string Canonical)>();
        HumanCatalogueSkeleton? skeleton = null;
        if (groupLocal.Count > 0 || groupSwapped.Count > 0)
        {
            var groupRead = new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, read.GlamourerState, resources,
                PenumbraModDirectory: read.ModDirectory, CharacterRace: read.Race);
            var planned = await AppearanceCapturePlanner.PlanSkeletonGroupAsync(groupRead, GroupOptions, origins.Keys, groupLocal, groupSwapped,
                unsupported, canonicalOf, hashed, cancellationToken).ConfigureAwait(false);
            if (planned.Conflict is { } conflict)
                return (null, $"one game path has two different sources ({conflict})");
            if (planned.Outcome.State != AppearanceSkeletonGroupState.Captured || planned.Members is not { } groupMembers ||
                planned.Outcome.RaceCode is not { } raceCode)
                return (null, $"the skeleton group was refused: {planned.Outcome.Reason}, {AppearanceCaptureText.SkeletonGroupReason(planned.Outcome)}" +
                    (planned.Outcome.GamePath is { } refused ? $" ({refused})" : string.Empty));
            members.AddRange(groupMembers);
            var memberPaths = members.Select(member => member.GamePath).Order(StringComparer.Ordinal).ToList();
            skeleton = new HumanCatalogueSkeleton(raceCode, memberPaths,
                memberPaths.Contains(SkeletonResourcePaths.DeformerGamePath, StringComparer.Ordinal));
        }

        // Every other redirected file, hashed once. A file that cannot be read refuses the capture: nothing is dropped.
        var blobOf = new SortedDictionary<string, AppearanceBlobRef>(StringComparer.Ordinal);
        var sourceOf = new Dictionary<ContentHash, string>();
        foreach (var (gamePath, sources) in redirects)
        {
            AppearanceBlobRef? chosen = null;
            foreach (var file in sources)
            {
                if (!hashed.TryGetValue(file, out var result))
                {
                    result = await HashAsync(file, cancellationToken).ConfigureAwait(false);
                    hashed[file] = result;
                }
                if (result.Blob is not { } blob)
                    return (null, $"a mod file could not be stored ({Shown(gamePath)}: {result.Problem})");
                if (chosen is not null && chosen != blob)
                    return (null, $"one game path has two different sources ({Shown(gamePath)})");
                chosen = blob;
                sourceOf.TryAdd(blob.Hash, file);
            }
            blobOf[gamePath] = chosen!;
        }
        foreach (var (gamePath, blob, canonical) in members)
        {
            blobOf[gamePath] = blob;
            sourceOf.TryAdd(blob.Hash, canonical);
        }

        // A game path with more than one source: every source must be a stored file with the same content.
        foreach (var (gamePath, actuals) in origins.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (actuals.Count < 2)
                continue;
            var contents = new HashSet<ContentHash>();
            foreach (var actual in actuals)
            {
                if (!canonicalOf.TryGetValue(actual, out var canonical) || !hashed.TryGetValue(canonical, out var file) || file.Blob is not { } blob)
                    return (null, $"one game path has two different sources ({Shown(gamePath)})");
                contents.Add(blob.Hash);
            }
            if (contents.Count != 1)
                return (null, $"one game path has two different sources ({Shown(gamePath)})");
        }

        // One package: stored files and swaps, never both for one game path.
        var paths = new List<HumanCataloguePath>(blobOf.Count + swaps.Count);
        foreach (var (gamePath, blob) in blobOf)
            paths.Add(new HumanCataloguePath(gamePath, blob.Hash.Value, null));
        foreach (var (gamePath, target) in swaps)
        {
            if (blobOf.Keys.Any(other => string.Equals(other, gamePath, StringComparison.OrdinalIgnoreCase)))
                return (null, $"one game path has two different sources ({Shown(gamePath)})");
            paths.Add(new HumanCataloguePath(gamePath, null, target));
        }
        if (blobOf.Count == 0)
            return (null, $"no file beneath the mod folder is redirected ({outside} left out), so there is nothing to store");
        if (paths.Count > HumanCatalogueEntryCodec.MaximumGamePaths)
            return (null, $"the package has {paths.Count} game paths; at most {HumanCatalogueEntryCodec.MaximumGamePaths} may be stored");

        var toStore = blobOf.Values.DistinctBy(blob => blob.Hash).OrderBy(blob => blob.Hash.Value, StringComparer.Ordinal)
            .Select(blob => (Blob: blob, Source: sourceOf[blob.Hash])).ToList();
        int skeletonPaths = 0, physicsPaths = 0, deformerPaths = 0;
        foreach (var (gamePath, _, _) in members)
        {
            SkeletonResourcePaths.TryClassify(gamePath, out var classified);
            switch (classified.Role)
            {
                case SkeletonResourceRole.Skeleton:
                    skeletonPaths++;
                    break;
                case SkeletonResourceRole.Physics:
                    physicsPaths++;
                    break;
                case SkeletonResourceRole.Deformer:
                    deformerPaths++;
                    break;
            }
        }
        var counts = new HumanCatalogueCounts(blobOf.Count, swaps.Count, vanilla, outside, toStore.Count,
            toStore.Sum(file => file.Blob.Size), skeletonPaths, physicsPaths, deformerPaths, blobOf.Count - members.Count);
        return (new HumanCataloguePlan(paths, toStore, skeleton, counts), null);
    }

    /// <summary>
    /// A digest of everything a read holds, for the re-check: the player
    /// object, the mod root, the sorted resources, the Glamourer state and
    /// reference, the manipulations, the race and body bytes, the skeleton
    /// summary and the versions. Memory only.
    /// </summary>
    public static string Fingerprint(HumanCatalogueRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "\0");
            sha.AppendData(BitConverter.GetBytes(bytes.Length));
            sha.AppendData(bytes);
        }
        Add(read.PlayerObject);
        Add(read.ModDirectory);
        foreach (var entry in read.Resources.OrderBy(entry => entry.ActualPath, StringComparer.Ordinal))
        {
            Add(entry.ActualPath);
            foreach (var gamePath in entry.GamePaths.Order(StringComparer.Ordinal))
                Add(gamePath);
            Add("\n");
        }
        Add(read.GlamourerState);
        Add(read.GlamourerReference);
        Add(read.Manipulations);
        Add(FormattableString.Invariant($"{read.Race.Race}:{read.Race.Tribe}:{read.Race.Sex}:{read.BodyType}"));
        foreach (var line in read.SkeletonSummary)
            Add(line);
        Add(read.Versions.Game);
        Add(read.Versions.Penumbra);
        Add(read.Versions.Glamourer);
        Add(read.Versions.CustomizePlus);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>A game path for a log line: at most 120 characters.</summary>
    private static string Shown(string gamePath) => gamePath.Length <= 120 ? gamePath : gamePath[..120] + "…";

    /// <summary>Streams one file through SHA-256 within the blob limit; problems are reported without the path.</summary>
    private static async Task<(AppearanceBlobRef? Blob, AppearanceCaptureExclusion? Problem)> HashAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int read;
            while ((read = await file.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > AppearanceLimits.MaximumBlobBytes)
                    return (null, AppearanceCaptureExclusion.TooLarge);
                sha.AppendData(buffer, 0, read);
            }
            if (total == 0)
                return (null, AppearanceCaptureExclusion.Empty);
            ContentHash.TryParse(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), out var hash);
            return (new AppearanceBlobRef(hash, total), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, AppearanceCaptureExclusion.Unreadable);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>How one capture ended: the written entry, or the refusal; and the log lines (no local path, no stored data).</summary>
public sealed record HumanCatalogueCaptureResult(HumanCatalogueEntry? Entry, string? Refusal, IReadOnlyList<string> Lines);

/// <summary>
/// HV-5 M1: one capture, from the label to the written entry.
/// <list type="number">
/// <item>The label is checked, and refused if already used.</item>
/// <item>The first read (on the framework thread, by <c>read</c>).</item>
/// <item>The plan (off the framework thread): containment, the group checks, hashing.</item>
/// <item>Every file is copied into the store and its copy re-hashed against the plan.</item>
/// <item>The re-check: a second read, whose fingerprint must equal the first one's.</item>
/// <item>The entry is checked to hold no local path, then written once.</item>
/// </list>
/// Files copied before a refusal stay in the store as unreferenced files
/// (counted by <c>list</c>); nothing is deleted.
/// </summary>
public static class HumanCatalogueCapture
{
    public static async Task<HumanCatalogueCaptureResult> CaptureAsync(
        string? labelText,
        HumanCatalogueStore store,
        Func<CancellationToken, Task<HumanCatalogueReadResult>> read,
        Func<DateTimeOffset> now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(now);
        var lines = new List<string>();
        HumanCatalogueCaptureResult Refuse(string refusal) => new(null, refusal, lines);

        if (HumanCatalogueLabel.Normalize(labelText, out var labelRefusal) is not { } label)
            return Refuse(labelRefusal!);
        if (store.HasLabel(label))
            return Refuse("that label is already used");

        var first = await read(cancellationToken).ConfigureAwait(false);
        if (first.Read is not { } snapshot)
            return Refuse(first.Refusal ?? "the character could not be read");
        if (!GameAppearanceCaptureReader.IsCanonicalBase64(snapshot.GlamourerState))
            return Refuse("Glamourer's state is not valid");
        if (string.IsNullOrEmpty(snapshot.GlamourerReference))
            return Refuse("Glamourer's structured state is missing");
        if (snapshot.Manipulations is null)
            return Refuse("Penumbra's manipulations are missing");

        var (plan, planRefusal) = await HumanCataloguePlanner.PlanAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (plan is null)
            return Refuse(planRefusal!);

        // D6: the whole capture must fit before anything is copied. The entry's
        // size is estimated generously (every text's UTF-8 bytes twice over, for
        // escaping; 1 KiB a game path; 64 KiB besides); its write re-checks the real size.
        var entryEstimate = Math.Min(HumanCatalogueEntryCodec.MaximumEntryBytes,
            2L * (Encoding.UTF8.GetByteCount(snapshot.GlamourerState) + Encoding.UTF8.GetByteCount(snapshot.GlamourerReference) +
                Encoding.UTF8.GetByteCount(snapshot.Manipulations)) +
            1024L * plan.Paths.Count + 64 * 1024);
        var (fits, missing) = store.Room(plan.Files.Select(file => file.Blob), entryEstimate);
        if (!fits)
            return Refuse($"the catalogue is full: {missing} new byte(s) do not fit within {store.LimitBytes} bytes, and nothing is evicted");

        int stored = 0, already = 0;
        foreach (var (blob, source) in plan.Files)
        {
            var status = await store.ImportAsync(source, blob, cancellationToken).ConfigureAwait(false);
            switch (status)
            {
                case HumanCatalogueImportStatus.Stored:
                    stored++;
                    break;
                case HumanCatalogueImportStatus.AlreadyStored:
                    already++;
                    break;
                case HumanCatalogueImportStatus.Full:
                    return Refuse($"the catalogue is full ({store.LimitBytes} bytes, nothing is evicted)");
                case HumanCatalogueImportStatus.SourceChanged:
                    return Refuse("a mod file changed during the capture");
                case HumanCatalogueImportStatus.StoredFileDamaged:
                    return Refuse($"a stored file does not verify ({blob.Hash.Value[..8]}); it was kept, not replaced");
                default:
                    return Refuse($"a file could not be stored ({status})");
            }
        }

        // The re-check: everything read must be unchanged after the copies.
        var second = await read(cancellationToken).ConfigureAwait(false);
        if (second.Read is not { } again)
            return Refuse(second.Refusal ?? "the character could not be read again");
        if (!string.Equals(HumanCataloguePlanner.Fingerprint(again), HumanCataloguePlanner.Fingerprint(snapshot), StringComparison.Ordinal))
            return Refuse("the appearance changed during the capture");

        var entry = new HumanCatalogueEntry(
            HumanCatalogueEntry.CurrentFormat,
            Guid.NewGuid(),
            label,
            now().ToUniversalTime(),
            snapshot.Versions.Bounded(),
            snapshot.Race.Race,
            snapshot.Race.Tribe,
            snapshot.Race.Sex,
            snapshot.BodyType,
            snapshot.GlamourerState,
            HumanCatalogueEntry.HashOf(snapshot.GlamourerState),
            snapshot.GlamourerReference,
            HumanCatalogueEntry.HashOf(snapshot.GlamourerReference),
            snapshot.Manipulations,
            HumanCatalogueEntry.HashOf(snapshot.Manipulations),
            plan.Paths,
            plan.Files.Select(file => new HumanCatalogueBlob(file.Blob.Hash.Value, file.Blob.Size)).ToList(),
            plan.Skeleton,
            snapshot.SkeletonSummary.ToList(),
            plan.Counts,
            HumanCatalogueEntry.NoProfile);
        if (HumanCatalogueEntryCodec.Problem(entry) is { } problem)
            return Refuse($"the entry is not valid ({problem})");
        var locals = snapshot.Resources.Select(resource => resource.ActualPath).Where(AppearanceCapturePlanner.IsLocalFile).ToList();
        if (!string.IsNullOrEmpty(snapshot.ModDirectory))
            locals.Add(snapshot.ModDirectory);
        if (HoldsLocalText(HumanCatalogueEntryCodec.Serialize(entry), locals))
            return Refuse("the entry would hold a local path");

        var (written, writeRefusal) = await store.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!written)
            return Refuse(writeRefusal!);
        var counts = entry.Counts;
        lines.Add($"captured \"{entry.Label}\" as entry {entry.ShortId}: {entry.Paths.Count} game path(s) ({counts.Redirects} stored, {counts.Swaps} swapped), " +
            $"{counts.Vanilla} vanilla, {counts.Outside} outside the mod folder left out");
        lines.Add($"files {counts.Files} ({stored} new, {already} already stored), {counts.Bytes} byte(s); by role: skeleton {counts.Skeleton}, " +
            $"physics {counts.Physics}, deformer {counts.Deformer}, other {counts.Other}");
        lines.Add(entry.Skeleton is { } group
            ? $"skeleton group for race c{group.RaceCode:D4}: {group.Members.Count} member(s), deformer {(group.Deformer ? "included" : "not redirected")}"
            : "no skeleton group (no skeleton file is redirected)");
        lines.Add($"customize {entry.Race}/{entry.Tribe}/{entry.Sex}, body {entry.BodyType}; skeleton summary {entry.SkeletonSummary.Count} line(s); " +
            $"manipulations {entry.Manipulations.Length} character(s); Glamourer state {entry.GlamourerState.Length} character(s), " +
            $"reference keys {ReferenceKeys(entry.GlamourerReference)}");
        lines.Add($"versions: game {entry.Versions.Game ?? "unknown"}, Penumbra {entry.Versions.Penumbra ?? "unknown"}, " +
            $"Glamourer {entry.Versions.Glamourer ?? "unknown"}, Customize+ {entry.Versions.CustomizePlus ?? "not installed"}; profile source {entry.ProfileSource}");
        return new HumanCatalogueCaptureResult(entry, null, lines);
    }

    /// <summary>True if the UTF-8 entry text holds any local path, as written or JSON-escaped, with either separator.</summary>
    internal static bool HoldsLocalText(byte[] utf8, IEnumerable<string> locals)
    {
        var text = Encoding.UTF8.GetString(utf8);
        foreach (var local in locals)
        {
            foreach (var form in new[] { local, local.Replace("\\", "\\\\", StringComparison.Ordinal), local.Replace('\\', '/') })
            {
                if (form.Length > 0 && text.Contains(form, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>The structured reference's top-level property names (shown so the owner can see what it holds; no values).</summary>
    private static string ReferenceKeys(string reference)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(reference);
            return string.Join(", ", document.RootElement.EnumerateObject().Select(property => property.Name).Take(16));
        }
        catch (System.Text.Json.JsonException)
        {
            return "unreadable";
        }
    }
}
#endif
