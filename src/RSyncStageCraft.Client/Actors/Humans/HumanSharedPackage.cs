#if RSC_DEVELOPMENT
using System.Text;
using System.Text.Json;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Actors.Humans;

// HV-6 (Debug only): the shared human package. A sender builds a format-3
// appearance bundle from one of its own local catalogue entries (read only);
// a receiver rebuilds an in-memory entry of format hv6-shared-1 from the
// bundle's verified blobs and spawns it through HV-5's unchanged sequence.
// Nothing here touches the game, a provider or the network.

/// <summary>
/// The human facts blob (<c>rsc-human-facts-1</c>): what a spawn needs and
/// the bundle's other members do not carry. It holds no label, name, world,
/// path, index, collection, receipt or key. Read only in its exact canonical
/// form; the receiver checks every field, the server only its declared size.
/// </summary>
public sealed record HumanSharedFacts(
    byte Race,
    byte Tribe,
    byte Sex,
    byte BodyType,
    HumanCatalogueVersions Versions,
    IReadOnlyList<string> SkeletonSummary,
    HumanCatalogueProfileCounts? ProfileCounts)
{
    public const string Format = "rsc-human-facts-1";

    /// <summary>The canonical UTF-8 JSON (the only accepted form).</summary>
    public byte[] ToCanonicalBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("race", Race);
            writer.WriteNumber("tribe", Tribe);
            writer.WriteNumber("sex", Sex);
            writer.WriteNumber("bodyType", BodyType);
            writer.WriteStartObject("versions");
            WriteOptional(writer, "game", Versions.Game);
            WriteOptional(writer, "penumbra", Versions.Penumbra);
            WriteOptional(writer, "glamourer", Versions.Glamourer);
            WriteOptional(writer, "customizePlus", Versions.CustomizePlus);
            writer.WriteEndObject();
            writer.WriteStartArray("skeletonSummary");
            foreach (var line in SkeletonSummary)
                writer.WriteStringValue(line);
            writer.WriteEndArray();
            if (ProfileCounts is { } counts)
            {
                writer.WriteStartObject("profileCounts");
                writer.WriteNumber("bones", counts.Bones);
                writer.WriteNumber("base", counts.Base);
                writer.WriteNumber("other", counts.Other);
                writer.WriteNumber("absent", counts.Absent);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("profileCounts");
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>The facts, or null with the reason. Any other form than the canonical one is refused.</summary>
    public static HumanSharedFacts? TryParse(ReadOnlySpan<byte> utf8, out string? problem)
    {
        problem = "the human facts are not valid";
        if (utf8.Length is 0 || utf8.Length > AppearanceLimits.MaximumHumanFactsBytes)
            return null;
        HumanSharedFacts facts;
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("format").GetString() != Format)
                return null;
            var versions = root.GetProperty("versions");
            var summary = new List<string>();
            foreach (var line in root.GetProperty("skeletonSummary").EnumerateArray())
                summary.Add(line.GetString() ?? throw new FormatException());
            var countsElement = root.GetProperty("profileCounts");
            HumanCatalogueProfileCounts? counts = countsElement.ValueKind == JsonValueKind.Null
                ? null
                : new HumanCatalogueProfileCounts(countsElement.GetProperty("bones").GetInt32(), countsElement.GetProperty("base").GetInt32(),
                    countsElement.GetProperty("other").GetInt32(), countsElement.GetProperty("absent").GetInt32());
            facts = new HumanSharedFacts(root.GetProperty("race").GetByte(), root.GetProperty("tribe").GetByte(), root.GetProperty("sex").GetByte(),
                root.GetProperty("bodyType").GetByte(),
                new HumanCatalogueVersions(ReadOptional(versions, "game"), ReadOptional(versions, "penumbra"), ReadOptional(versions, "glamourer"),
                    ReadOptional(versions, "customizePlus")),
                summary, counts);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return null;
        }
        if (facts.Versions != facts.Versions.Bounded() || facts.SkeletonSummary.Count > 64 ||
            facts.SkeletonSummary.Any(line => line.Length > 512))
            return null;
        // Canonical only: the same facts written again must be exactly these bytes.
        if (!utf8.SequenceEqual(facts.ToCanonicalBytes()))
        {
            problem = "the human facts are not in canonical form";
            return null;
        }
        problem = null;
        return facts;
    }

    /// <summary>Never the facts.</summary>
    public override string ToString() => "shared human facts";

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static string? ReadOptional(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString() ?? throw new FormatException();
    }
}

/// <summary>A built package: its manifest, the bytes of its member blobs that are not stored files, and the stored files it redirects.</summary>
public sealed record HumanSharedPackage(
    AppearanceBundleManifest Manifest,
    IReadOnlyDictionary<ContentHash, byte[]> MemberBytes,
    IReadOnlyList<HumanCatalogueBlob> StoredFiles,
    bool ProfileIncluded)
{
    /// <summary>The 8-character bundle id used in log lines.</summary>
    public string ShortId => Manifest.BundleId.Value[..8];

    /// <summary>Never the content.</summary>
    public override string ToString() => $"shared package {ShortId}";
}

/// <summary>
/// HV-6, the sender's share check and package build (r1 §3–§4). Fail closed:
/// a refusal builds nothing and nothing is uploaded.
/// </summary>
public static class HumanSharedPackageBuilder
{
    /// <summary>
    /// The package for <paramref name="entry"/>, owned by <paramref name="owner"/>
    /// (the signed-in user), or null with the refusal:
    /// <list type="bullet">
    /// <item>D7: every recorded version must equal the running one;</item>
    /// <item>every game path must be a canonical appearance game path (counted, never printed);</item>
    /// <item>every redirected file must be on allowlist v3 (the v2 set: models,
    /// materials, textures and skeleton resources on recognised paths);
    /// nothing is dropped silently;</item>
    /// <item>no swap (SD-3);</item>
    /// <item>an entry with a Customize+ profile needs <paramref name="withProfile"/>;
    /// the flag on an entry without one refuses;</item>
    /// <item>at most 1,024 game paths (D9), and the format-3 bounds.</item>
    /// </list>
    /// </summary>
    public static HumanSharedPackage? Build(
        HumanCatalogueEntry entry, RscUserId owner, bool withProfile, HumanCatalogueVersions running, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(running);
        refusal = null;
        if (entry.Format == HumanCatalogueEntry.SharedFormat || HumanCatalogueEntryCodec.Problem(entry) is not null)
            return Refuse("the entry is not a valid local entry", out refusal);
        if (!owner.IsValid)
            return Refuse("sign in once first", out refusal);
        if (HumanCatalogueSpawnPreparation.VersionMismatch(entry.Versions, running.Bounded()) is { } stale)
            return Refuse(stale, out refusal);
        if (entry.Paths.Count > HumanCatalogueEntryCodec.MaximumGamePaths)
            return Refuse($"the entry has more than {HumanCatalogueEntryCodec.MaximumGamePaths} game paths", out refusal);
        var swaps = entry.Paths.Count(path => path.Swap is not null);
        if (swaps > 0)
            return Refuse($"{swaps} swap(s); a shared package carries no swaps in this candidate", out refusal);
        var invalid = entry.Paths.Count(path => !AppearanceGamePath.IsValid(path.GamePath));
        if (invalid > 0)
            return Refuse($"{invalid} game path(s) are not canonical appearance game paths", out refusal);
        var outside = entry.Paths
            .Where(path => !AppearanceFileTypes.IsAllowed(path.GamePath, AppearanceLimits.HumanPackageFormatVersion))
            .GroupBy(path => Extension(path.GamePath), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key} ×{group.Count()}")
            .ToArray();
        if (outside.Length > 0)
            return Refuse($"{outside.Sum(text => int.Parse(text[(text.IndexOf('×') + 1)..], System.Globalization.CultureInfo.InvariantCulture))} " +
                $"file(s) outside the shareable types ({string.Join(", ", outside)})", out refusal);
        if (entry.Profile is not null && !withProfile)
            return Refuse("the entry has a Customize+ profile; share it with with-customize+ (the profile is never dropped silently)", out refusal);
        if (entry.Profile is null && withProfile)
            return Refuse("with-customize+ was given, but the entry has no Customize+ profile", out refusal);

        var members = new Dictionary<ContentHash, byte[]>();
        AppearanceBlobRef Member(byte[] bytes)
        {
            var hash = ContentHash.Of(bytes);
            members[hash] = bytes;
            return new AppearanceBlobRef(hash, bytes.LongLength);
        }
        var glamourer = Member(Encoding.UTF8.GetBytes(entry.GlamourerState));
        // Empty manipulations map to no slot (the receiver reads no slot as empty).
        var meta = entry.Manipulations.Length == 0 ? null : Member(Encoding.UTF8.GetBytes(entry.Manipulations));
        var profile = entry.Profile is { } text ? Member(Encoding.UTF8.GetBytes(text)) : null;
        var facts = new HumanSharedFacts(entry.Race, entry.Tribe, entry.Sex, entry.BodyType, entry.Versions, entry.SkeletonSummary,
            entry.ProfileCounts);
        var human = new AppearanceHumanPackage(Member(facts.ToCanonicalBytes()), Member(Encoding.UTF8.GetBytes(entry.GlamourerReference)));
        var sizes = entry.Blobs.ToDictionary(blob => blob.Hash, blob => blob.Size, StringComparer.Ordinal);
        var files = new List<AppearanceFileEntry>(entry.Paths.Count);
        foreach (var path in entry.Paths)
        {
            if (!ContentHash.TryParse(path.Blob, out var hash))
                return Refuse("a stored file has no valid hash", out refusal);
            files.Add(new AppearanceFileEntry(path.GamePath, new AppearanceBlobRef(hash, sizes[path.Blob!])));
        }
        var manifest = AppearanceBundleManifest.TryCreateHumanPackage(owner, glamourer, meta, profile, files, entry.Skeleton?.RaceCode, human,
            out var error);
        if (manifest is null)
            return Refuse($"the package is invalid ({error})", out refusal);
        return new HumanSharedPackage(manifest, members, entry.Blobs, profile is not null);
    }

    private static string Extension(string gamePath)
    {
        var file = gamePath[(gamePath.LastIndexOf('/') + 1)..];
        var dot = file.LastIndexOf('.');
        return dot <= 0 ? "(none)" : file[dot..];
    }

    private static HumanSharedPackage? Refuse(string reason, out string? refusal)
    {
        refusal = reason;
        return null;
    }
}

/// <summary>
/// HV-6, the receiver's entry: built in memory from a verified format-3
/// bundle, of format <see cref="HumanCatalogueEntry.SharedFormat"/> with the
/// fixed label <c>shared YYYYYYYY</c> (never the server's free-text name),
/// held to the same codec checks as a local entry, and never written to
/// this client's catalogue.
/// </summary>
public static class HumanSharedEntry
{
    /// <summary>The fixed label of a received entry.</summary>
    public static string LabelOf(ContentHash bundleId) => "shared " + bundleId.Value[..8];

    /// <summary>A stable entry id derived from the bundle id (its first 16 bytes).</summary>
    public static Guid EntryIdOf(ContentHash bundleId) => new(Convert.FromHexString(bundleId.Value[..32]));

    /// <summary>The entry, or null with the refusal. <paramref name="bytes"/> answers a member blob's verified bytes, or null.</summary>
    public static HumanCatalogueEntry? Build(AppearanceBundleManifest manifest, Func<ContentHash, byte[]?> bytes, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(bytes);
        refusal = null;
        if (manifest.FormatVersion != AppearanceLimits.HumanPackageFormatVersion || manifest.Human is not { } human)
            return Refuse("the bundle is not a human package (format 3)", out refusal);
        if (bytes(human.Facts.Hash) is not { } factBytes)
            return Refuse("the human facts are missing", out refusal);
        if (HumanSharedFacts.TryParse(factBytes, out var factsProblem) is not { } facts)
            return Refuse($"the human facts are not valid ({factsProblem})", out refusal);
        if (Text(bytes, manifest.Glamourer) is not { } glamourer)
            return Refuse("the Glamourer state is missing or not text", out refusal);
        if (Text(bytes, human.GlamourerReference) is not { } reference)
            return Refuse("the Glamourer reference is missing or not text", out refusal);
        // No meta slot reads as empty manipulations (the sender maps empty to no slot).
        var manipulations = string.Empty;
        if (manifest.PenumbraMeta is { } meta)
        {
            if (Text(bytes, meta) is not { } metaText || metaText.Length == 0)
                return Refuse("the manipulations are missing, empty or not text", out refusal);
            manipulations = metaText;
        }
        string? profile = null;
        if (manifest.CustomizePlus is { } profileBlob)
        {
            profile = Text(bytes, profileBlob);
            if (profile is null)
                return Refuse("the Customize+ profile is missing or not text", out refusal);
        }
        if ((profile is null) != (facts.ProfileCounts is null))
            return Refuse("the profile and its counts do not agree", out refusal);

        var paths = new List<HumanCataloguePath>(manifest.Files.Count);
        var blobs = new List<HumanCatalogueBlob>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int skeleton = 0, physics = 0, deformer = 0, other = 0;
        foreach (var file in manifest.Files)
        {
            paths.Add(new HumanCataloguePath(file.GamePath, file.Content.Hash.Value, null));
            if (seen.Add(file.Content.Hash.Value))
                blobs.Add(new HumanCatalogueBlob(file.Content.Hash.Value, file.Content.Size));
            if (!SkeletonResourcePaths.TryClassify(file.GamePath, out var classified))
                other++;
            else if (classified.Role == SkeletonResourceRole.Skeleton)
                skeleton++;
            else if (classified.Role == SkeletonResourceRole.Physics)
                physics++;
            else
                deformer++;
        }
        HumanCatalogueSkeleton? group = null;
        if (manifest.SkeletonGroup is { } declared)
        {
            var members = declared.Members.Select(member => member.Path.GamePath).ToArray();
            group = new HumanCatalogueSkeleton(declared.RaceCode, members,
                members.Contains(SkeletonResourcePaths.DeformerGamePath, StringComparer.Ordinal));
        }
        var counts = new HumanCatalogueCounts(paths.Count, 0, 0, 0, blobs.Count, blobs.Sum(blob => blob.Size), skeleton, physics, deformer, other);
        var entry = new HumanCatalogueEntry(
            HumanCatalogueEntry.SharedFormat,
            EntryIdOf(manifest.BundleId),
            LabelOf(manifest.BundleId),
            DateTimeOffset.UnixEpoch,
            facts.Versions,
            facts.Race,
            facts.Tribe,
            facts.Sex,
            facts.BodyType,
            glamourer,
            HumanCatalogueEntry.HashOf(glamourer),
            reference,
            HumanCatalogueEntry.HashOf(reference),
            manipulations,
            HumanCatalogueEntry.HashOf(manipulations),
            paths,
            blobs,
            group,
            facts.SkeletonSummary,
            counts,
            profile is null ? HumanCatalogueEntry.NoProfile : HumanCatalogueEntry.OwnerApproved,
            profile,
            profile is null ? null : HumanCatalogueEntry.HashOf(profile),
            facts.ProfileCounts);
        // The same codec checks as a local entry (r1 §6, shared-check item 2).
        if (HumanCatalogueEntryCodec.Problem(entry) is { } problem)
            return Refuse($"the received entry does not check ({problem})", out refusal);
        return entry;
    }

    private static readonly UTF8Encoding Strict = new(false, true);

    private static string? Text(Func<ContentHash, byte[]?> bytes, AppearanceBlobRef blob)
    {
        if (bytes(blob.Hash) is not { } content || content.LongLength != blob.Size)
            return null;
        try
        {
            return Strict.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static HumanCatalogueEntry? Refuse(string reason, out string? refusal)
    {
        refusal = reason;
        return null;
    }
}
/// <summary>
/// HV-6, the receiver's preparation, off the framework thread, after the
/// bundle was downloaded and verified into the appearance cache. It holds
/// every blob of the bundle through the cache (each re-hashed through its
/// held handle), rebuilds the in-memory entry from the member blobs' kept
/// bytes, and then makes HV-5's own checks in HV-5's order: D7, Customize+
/// loaded for a profile, a comparable skeleton summary, and the deformer
/// against the player's current one (GAP-K4). The package maps each game
/// path to its held cache file. Member-only blobs are released once read;
/// file blobs stay held for the actor's life. Any refusal closes every
/// hold taken, and nothing is created.
/// </summary>
public static class HumanSharedSpawnPreparation
{
    public static async Task<(HumanCatalogueSpawnReady? Ready, string? Refusal)> PrepareAsync(
        AppearanceBundleManifest manifest, AppearanceBlobCache cache, HumanCatalogueSpawnRead current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(current);
        if (manifest.FormatVersion != AppearanceLimits.HumanPackageFormatVersion || manifest.Human is not { } human)
            return (null, "the bundle is not a human package (format 3)");

        var members = new HashSet<string>(StringComparer.Ordinal) { manifest.Glamourer.Hash.Value, human.Facts.Hash.Value, human.GlamourerReference.Hash.Value };
        if (manifest.PenumbraMeta is { } meta)
            members.Add(meta.Hash.Value);
        if (manifest.CustomizePlus is { } profile)
            members.Add(profile.Hash.Value);
        var fileBlobs = manifest.Files.Select(file => file.Content.Hash.Value).ToHashSet(StringComparer.Ordinal);

        var leases = new Dictionary<string, AppearanceCacheHeldLease>(StringComparer.Ordinal);
        var holds = new List<HumanCatalogueHold>();
        var complete = false;
        try
        {
            foreach (var blob in manifest.Blobs)
            {
                var keep = members.Contains(blob.Hash.Value) ? blob.Size : 0;
                var (state, lease) = await cache.AcquireHeldAsync(blob, keep, cancellationToken).ConfigureAwait(false);
                if (lease is null)
                    return (null, $"a file of the bundle is {(state == AppearanceCacheState.Missing ? "missing from" : "unavailable in")} the cache ({blob.Hash.Value[..8]}); nothing was created");
                leases.Add(blob.Hash.Value, lease);
            }
            byte[]? Bytes(ContentHash hash) =>
                leases.TryGetValue(hash.Value, out var lease) && lease.Bytes is { } content ? content.ToArray() : null;
            if (HumanSharedEntry.Build(manifest, Bytes, out var entryRefusal) is not { } entry)
                return (null, entryRefusal);
            if (HumanCatalogueSpawnPreparation.VersionMismatch(entry.Versions, current.Versions.Bounded()) is { } stale)
                return (null, stale);
            if (entry.Profile is not null && current.Versions.CustomizePlus is null)
                return (null, "the shared human's Customize+ profile needs Customize+ loaded");
            if (!HumanCatalogueSkeletonSummary.Comparable(entry.SkeletonSummary))
                return (null, "the shared human's skeleton summary cannot be compared");
            if (await HumanCatalogueSpawnPreparation.DeformerMismatchAsync(entry, current, cancellationToken).ConfigureAwait(false) is { } deformer)
                return (null, deformer);

            // Member-only blobs are read; release them now. File blobs become the spawn's holds.
            foreach (var (hash, lease) in leases.ToArray())
            {
                if (fileBlobs.Contains(hash))
                {
                    lease.DropBytes();
                    holds.Add(HumanCatalogueHold.FromCache(lease));
                }
                else
                {
                    lease.Dispose();
                }
                leases.Remove(hash);
            }
            var byHash = holds.ToDictionary(hold => hold.Hash, StringComparer.Ordinal);
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in entry.Paths)
                paths.Add(path.GamePath, byHash[path.Blob!].FilePath);
            var ready = new HumanCatalogueSpawnReady(entry, holds, new HumanActorPenumbraPackage(paths, paths.Count, 0, 0, 0),
                HumanCatalogueSpawnPreparation.PhaseOneMembers(entry), entry.Skeleton?.Deformer ?? false);
            complete = true;
            return (ready, null);
        }
        finally
        {
            if (!complete)
            {
                foreach (var lease in leases.Values)
                    lease.Dispose();
                foreach (var hold in holds)
                    hold.Dispose();
            }
        }
    }
}
/// <summary>
/// HV-6: the owner's share and unshare of a built package over the
/// appearance transfer client, outside the plugin so the synthetic
/// end-to-end test runs exactly this code. Each answer is one log line that
/// names no path, label or content. Every temporary member file and store
/// hold it takes is released before it returns.
/// </summary>
public static class HumanSharedPublisher
{
    /// <summary>Uploads the package's members (temporary cache files) and files (store holds), then publishes it to the group.</summary>
    public static async Task<(bool Shared, string Line)> ShareAsync(HumanSharedPackage package, HumanCatalogueStore store, AppearanceBlobCache cache,
        AppearanceTransferClient transfer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(transfer);
        var temporary = new List<string>();
        var stored = new List<HumanCatalogueHold>();
        try
        {
            var sources = new Dictionary<ContentHash, string>();
            foreach (var (hash, bytes) in package.MemberBytes)
            {
                var path = cache.NewTemporaryPath(hash, "hv6");
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                temporary.Add(path);
                sources[hash] = path;
            }
            foreach (var blob in package.StoredFiles)
            {
                var (hold, problem) = await store.HoldAsync(blob, cancellationToken).ConfigureAwait(false);
                if (hold is null)
                    return (false, $"share refused: a stored file of the entry {problem} ({blob.Hash[..8]}). Nothing was uploaded.");
                stored.Add(hold);
                if (ContentHash.TryParse(hold.Hash, out var hash))
                    sources[hash] = hold.FilePath;
            }
            var missing = -1;
            var upload = await transfer.UploadBundleAsync(package.Manifest, sources, cancellationToken: cancellationToken,
                missing: count => missing = count).ConfigureAwait(false);
            if (upload.Status != AppearanceTransferStatus.Succeeded)
                return (false, $"share failed: the upload ended {upload.Status}. Nothing was published.");
            var published = await transfer.UpdateBundleAsync(package.Manifest.BundleId, new AppearanceBundleEdit(Published: true), cancellationToken)
                .ConfigureAwait(false);
            var id = package.ShortId;
            return published.Status == AppearanceTransferStatus.Succeeded
                ? (true, $"shared as bundle {id} (format 3, {package.Manifest.Blobs.Count} blob(s), {Math.Max(missing, 0)} uploaded, " +
                    $"Customize+ {(package.ProfileIncluded ? "included" : "none")}); published to the group.")
                : (false, $"bundle {id} was uploaded but not published ({published.Status}); share again to retry the publish.");
        }
        finally
        {
            foreach (var hold in stored)
                hold.Dispose();
            foreach (var path in temporary)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The cache removes its temporary files at the next start.
                }
            }
        }
    }

    /// <summary>Unpublishes, then deletes, the package's bundle; refused while a shared human still uses it.</summary>
    public static async Task<(bool Unshared, string Line)> UnshareAsync(ContentHash bundleId, AppearanceTransferClient transfer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var id = bundleId.Value[..8];
        var unpublished = await transfer.UpdateBundleAsync(bundleId, new AppearanceBundleEdit(Published: false), cancellationToken).ConfigureAwait(false);
        if (unpublished.Status == AppearanceTransferStatus.StillReferenced)
            return (false, $"unshare refused: a shared human still uses bundle {id}; remove-shared first.");
        if (unpublished.Status != AppearanceTransferStatus.Succeeded)
            return (false, $"unshare failed: the unpublish ended {unpublished.Status}.");
        var deleted = await transfer.DeleteBundleAsync(bundleId, cancellationToken).ConfigureAwait(false);
        return deleted.Status == AppearanceTransferStatus.Succeeded
            ? (true, $"bundle {id} unpublished and deleted.")
            : (false, $"bundle {id} unpublished; the delete ended {deleted.Status}.");
    }
}

/// <summary>HV-6's steps inside <c>/rschv5</c>.</summary>
public enum HumanSharedStep
{
    None,
    Share,
    Unshare,
    Place,
    Move,
    Remove,
    Status,
}

/// <summary>
/// HV-6: the steps typed after <c>/rschv5</c>. <c>share</c>, <c>unshare</c>
/// and <c>place-shared</c> name one of the sender's own local entries by its
/// label (the bundle id is derived from it, never typed), with the optional
/// <c> with-customize+</c> suffix; <c>move-shared</c>, <c>remove-shared</c> and
/// <c>shared-status</c> take nothing.
/// </summary>
public static class HumanSharedCommand
{
    public const string ShareVerb = "share";
    public const string UnshareVerb = "unshare";
    public const string PlaceVerb = "place-shared";
    public const string MoveVerb = "move-shared";
    public const string RemoveVerb = "remove-shared";
    public const string StatusVerb = "shared-status";

    public static HumanSharedStep TryParse(string? step, out string label, out bool withProfile)
    {
        label = string.Empty;
        withProfile = false;
        switch (step)
        {
            case null:
                return HumanSharedStep.None;
            case MoveVerb:
                return HumanSharedStep.Move;
            case RemoveVerb:
                return HumanSharedStep.Remove;
            case StatusVerb:
                return HumanSharedStep.Status;
        }
        foreach (var (verb, kind) in new[] { (ShareVerb, HumanSharedStep.Share), (UnshareVerb, HumanSharedStep.Unshare), (PlaceVerb, HumanSharedStep.Place) })
        {
            var lead = verb + " ";
            if (!step.StartsWith(lead, StringComparison.Ordinal))
                continue;
            var rest = step[lead.Length..];
            if (rest.EndsWith(HumanCatalogueCaptureCommand.WithProfile, StringComparison.Ordinal))
            {
                withProfile = true;
                rest = rest[..^HumanCatalogueCaptureCommand.WithProfile.Length];
            }
            if (rest.Trim().Length == 0)
            {
                withProfile = false;
                return HumanSharedStep.None;
            }
            label = rest;
            return kind;
        }
        return HumanSharedStep.None;
    }
}
#endif
