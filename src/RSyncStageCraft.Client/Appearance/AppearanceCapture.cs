using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Appearance;

// An explicit, read-only capture of the owner's own
// appearance, planned locally and shown in a disclosure summary before
// anything about it is sent.

/// <summary>Where a capture's appearance came from.</summary>
public enum AppearanceCaptureSource
{
    /// <summary>The owner's current character: Glamourer state plus Penumbra's modded files.</summary>
    CurrentCharacter,
    /// <summary>A saved Glamourer design: its state only, with no captured Penumbra mod files.</summary>
    SavedDesign,
}

/// <summary>
/// One entry of Penumbra's resource list for an object: the actual path (a
/// fully qualified local file for a redirect; a game path for a vanilla or
/// swapped resource) and the game paths it serves.
/// </summary>
public sealed record AppearanceResourceEntry(string ActualPath, IReadOnlyCollection<string> GamePaths)
{
    /// <summary>Never the actual (possibly local) path.</summary>
    public override string ToString() => $"resource entry ({GamePaths.Count} game path(s))";
}

/// <summary>What the read-only adapters returned for one capture (an RSC-owned snapshot).</summary>
/// <param name="Source">The capture source.</param>
/// <param name="GlamourerState">Glamourer's state (or design) text, exactly as returned (opaque).</param>
/// <param name="Resources">Penumbra's resource list for the player; empty for a saved design.</param>
/// <param name="DesignName">The saved design's display name (selection data only; never in the bundle).</param>
/// <param name="PenumbraModDirectory">Penumbra's mod root: only files beneath it may be captured.</param>
/// <param name="Player">The exact local-player object the snapshot was read from.</param>
/// <param name="DesignId">The saved design's local id (selection data only; never in the bundle).</param>
public sealed record AppearanceCaptureRead(
    AppearanceCaptureSource Source,
    string GlamourerState,
    IReadOnlyList<AppearanceResourceEntry> Resources,
    string? DesignName = null,
    string? PenumbraModDirectory = null,
    AppearancePlayer? Player = null,
    Guid? DesignId = null)
{
    /// <summary>Never local paths, the mod directory or the Glamourer data.</summary>
    public override string ToString() => $"{Source} read ({Resources.Count} resource entries)";
}

/// <summary>Why an allowlisted-looking game path was not captured.</summary>
public enum AppearanceCaptureExclusion
{
    /// <summary>The file type is not on allowlist v1 (.mdl, .mtrl, .tex).</summary>
    TypeNotAllowed,
    /// <summary>The game path does not follow the contract (lowercase ASCII, folders, extension, length).</summary>
    InvalidGamePath,
    /// <summary>The local file could not be read.</summary>
    Unreadable,
    /// <summary>The local file is empty.</summary>
    Empty,
    /// <summary>The local file is larger than 64 MiB.</summary>
    TooLarge,
    /// <summary>The local file is not provably inside Penumbra's mod directory (or its path is malformed).</summary>
    OutsideModDirectory,
    /// <summary>The local file's extension is not the game path's extension.</summary>
    ExtensionMismatch,
}

/// <summary>Why a capture cannot be uploaded at all.</summary>
public enum AppearanceCaptureRefusal
{
    None,
    /// <summary>Glamourer returned no state.</summary>
    NoGlamourerState,
    /// <summary>The Glamourer state is larger than one blob may be.</summary>
    GlamourerStateTooLarge,
    /// <summary>One game path is served by two different files.</summary>
    ConflictingGamePath,
    /// <summary>More than 4096 files.</summary>
    TooManyFiles,
    /// <summary>More than 1 GiB of distinct content.</summary>
    BundleTooLarge,
    /// <summary>The manifest could not be built (for example an invalid owner).</summary>
    InvalidManifest,
    /// <summary>The capture's working file could not be written.</summary>
    WorkingFileUnavailable,
    /// <summary>The capture's working file does not fit within the cache's size limit.</summary>
    CacheFull,
}

/// <summary>An excluded game path and why. Game paths only: local disk paths are never part of a summary.</summary>
public sealed record AppearanceCaptureExcluded(string GamePath, AppearanceCaptureExclusion Reason);

/// <summary>
/// A planned capture: the manifest that would be uploaded and the disclosure
/// summary shown before the owner confirms. The local source paths stay in
/// memory only for this capture and are never shown, logged or sent; the
/// Glamourer state's working file is deleted on <see cref="Dispose"/>.
/// </summary>
public sealed class AppearanceCapturePlan : IDisposable
{
    private readonly Dictionary<ContentHash, string> sources;
    private readonly string? workingFile;
    private readonly IDisposable? workingReservation;
    private int disposed;

    internal AppearanceCapturePlan(
        AppearanceCaptureSource source,
        string? designName,
        AppearanceCaptureRefusal refusal,
        AppearanceBundleManifest? manifest,
        Dictionary<ContentHash, string> sources,
        string? workingFile,
        IReadOnlyList<AppearanceCaptureExcluded> excluded,
        int vanillaPaths,
        int swappedPaths,
        string? conflictingGamePath,
        AppearanceCaptureRead read,
        IDisposable? workingReservation = null)
    {
        this.workingReservation = workingReservation;
        Read = read;
        Fingerprint = AppearanceCapturePlanner.Fingerprint(read);
        Source = source;
        DesignName = designName;
        Refusal = refusal;
        Manifest = manifest;
        this.sources = sources;
        this.workingFile = workingFile;
        Excluded = excluded;
        VanillaPaths = vanillaPaths;
        SwappedPaths = swappedPaths;
        ConflictingGamePath = conflictingGamePath;
    }

    public AppearanceCaptureSource Source { get; }
    public string? DesignName { get; }
    public AppearanceCaptureRefusal Refusal { get; }

    /// <summary>The bundle to upload, or null when the capture is refused.</summary>
    public AppearanceBundleManifest? Manifest { get; }

    public bool CanUpload => Refusal == AppearanceCaptureRefusal.None && Manifest is not null;

    /// <summary>True when the bundle carries no Penumbra mod files (always for a saved design).</summary>
    public bool StateOnly => Manifest is { Files.Count: 0 } || Source == AppearanceCaptureSource.SavedDesign;

    public int FileCount => Manifest?.Files.Count ?? 0;

    /// <summary>Distinct files, counted once however many game paths share them (the Glamourer state excluded).</summary>
    public int DistinctFiles => Manifest is null ? 0 : Manifest.Files.Select(file => file.Content.Hash).Distinct().Count(hash => hash != Manifest.Glamourer.Hash);

    /// <summary>Total distinct content in bytes, including the Glamourer state (what the quota charges).</summary>
    public long TotalBytes => Manifest?.TotalBytes ?? 0;

    public long GlamourerStateBytes => Manifest?.Glamourer.Size ?? 0;

    /// <summary>Captured files by type (".mdl", ".mtrl", ".tex").</summary>
    public IReadOnlyDictionary<string, int> FilesByType =>
        Manifest is null
            ? new Dictionary<string, int>()
            : Manifest.Files.GroupBy(file => Path.GetExtension(file.GamePath), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>Game paths that were not captured, with the reason.</summary>
    public IReadOnlyList<AppearanceCaptureExcluded> Excluded { get; }

    /// <summary>Unmodified game files in use (the recipient has them).</summary>
    public int VanillaPaths { get; }

    /// <summary>Game paths Penumbra swaps to another game file; not reproduced.</summary>
    public int SwappedPaths { get; }

    /// <summary>The game path served by two different files, when that refused the capture.</summary>
    public string? ConflictingGamePath { get; }

    /// <summary>
    /// Penumbra meta changes are never captured (no reviewed schema);
    /// the summary always warns that this can make the appearance incomplete.
    /// </summary>
    public bool PenumbraMetaExcluded => Source == AppearanceCaptureSource.CurrentCharacter;

    /// <summary>The snapshot this plan was made from (memory only; used to re-check it before upload).</summary>
    internal AppearanceCaptureRead Read { get; }

    /// <summary>A canonical digest of the snapshot (player, mod root, mapping, state); memory only.</summary>
    internal string Fingerprint { get; }

    /// <summary>Local source file of each blob, for the upload of this capture only (never shown, logged or sent).</summary>
    internal IReadOnlyDictionary<ContentHash, string> Sources => sources;

    public override string ToString() =>
        $"{Source} capture: {(CanUpload ? "ready" : Refusal.ToString())}, {FileCount} file(s), {TotalBytes} byte(s), {Excluded.Count} excluded";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        sources.Clear();
        var gone = true;
        if (workingFile is not null)
        {
            try
            {
                File.Delete(workingFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                gone = false;
            }
        }
        // The space within the cache limit is released only once the file is
        // gone; one that could not be deleted keeps it until the next startup.
        if (gone)
            workingReservation?.Dispose();
    }
}

/// <summary>
/// Plans a capture from what the read-only adapters returned. Pure apart from
/// reading (and hashing) the local files Penumbra names, and writing the
/// Glamourer state to one working file. Nothing is sent.
/// </summary>
public static class AppearanceCapturePlanner
{
    private const int BufferBytes = 81920;

    /// <param name="reserve">
    /// Reserves the working file's size within the cache limit; null
    /// refuses the capture with <see cref="AppearanceCaptureRefusal.CacheFull"/>.
    /// The plan holds the reservation until it is disposed.
    /// </param>
    public static async Task<AppearanceCapturePlan> PlanAsync(
        RscUserId owner, AppearanceCaptureRead read, string workingDirectory, CancellationToken cancellationToken = default,
        Func<long, CancellationToken, Task<IDisposable?>>? reserve = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var excluded = new List<AppearanceCaptureExcluded>();
        var sources = new Dictionary<ContentHash, string>();
        int vanilla = 0, swapped = 0;

        AppearanceCapturePlan Refused(AppearanceCaptureRefusal refusal, string? conflict = null, string? working = null) =>
            new(read.Source, read.DesignName, refusal, null, new Dictionary<ContentHash, string>(), working, excluded, vanilla, swapped, conflict, read);

        var state = Encoding.UTF8.GetBytes(read.GlamourerState ?? string.Empty);
        if (state.Length == 0)
            return Refused(AppearanceCaptureRefusal.NoGlamourerState);
        if (state.Length > AppearanceLimits.MaximumBlobBytes)
            return Refused(AppearanceCaptureRefusal.GlamourerStateTooLarge);

        // Penumbra's files (current character only): a fully qualified local
        // path is a redirect to a file on disk and is captured for its
        // allowlisted, contract-valid game paths; a game path is a vanilla or
        // swapped resource and is not.
        var byGamePath = new Dictionary<string, AppearanceBlobRef>(StringComparer.Ordinal);
        var hashed = new Dictionary<string, (AppearanceBlobRef? Blob, AppearanceCaptureExclusion? Problem)>(StringComparer.Ordinal);
        var canonicalOf = new Dictionary<string, string>(StringComparer.Ordinal);
        // Every source each game path is served by, whatever happens to it below.
        var origins = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (read.Source == AppearanceCaptureSource.CurrentCharacter)
        {
            foreach (var entry in read.Resources)
            {
                foreach (var gamePath in entry.GamePaths.Where(path => path is not null))
                {
                    if (!origins.TryGetValue(gamePath, out var actuals))
                        origins[gamePath] = actuals = new HashSet<string>(StringComparer.Ordinal);
                    actuals.Add(entry.ActualPath);
                }
            }
        }
        if (read.Source == AppearanceCaptureSource.CurrentCharacter)
        {
            foreach (var entry in read.Resources.OrderBy(entry => entry.ActualPath, StringComparer.Ordinal))
            {
                var gamePaths = entry.GamePaths.Where(path => path is not null).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToList();
                if (!IsLocalFile(entry.ActualPath))
                {
                    foreach (var gamePath in gamePaths)
                    {
                        if (string.Equals(gamePath, entry.ActualPath, StringComparison.OrdinalIgnoreCase))
                            vanilla++;
                        else
                            swapped++;
                    }
                    continue;
                }
                var candidates = new List<string>();
                foreach (var gamePath in gamePaths)
                {
                    if (!AppearanceGamePath.IsValid(gamePath))
                        excluded.Add(new(gamePath, AppearanceCaptureExclusion.InvalidGamePath));
                    else if (!AppearanceFileTypes.IsAllowed(gamePath))
                        excluded.Add(new(gamePath, AppearanceCaptureExclusion.TypeNotAllowed));
                    else
                        candidates.Add(gamePath);
                }
                if (candidates.Count == 0)
                    continue;
                // Only a file provably beneath Penumbra's mod directory, after
                // canonicalization and with no link on the way, may be captured.
                if (ContainedFile(read.PenumbraModDirectory, entry.ActualPath) is not { } canonical)
                {
                    excluded.AddRange(candidates.Select(gamePath => new AppearanceCaptureExcluded(gamePath, AppearanceCaptureExclusion.OutsideModDirectory)));
                    continue;
                }
                canonicalOf[entry.ActualPath] = canonical;
                // The file's own extension must be the game path's (both allowlisted).
                var sourceExtension = Path.GetExtension(canonical);
                var agreeing = new List<string>();
                foreach (var gamePath in candidates)
                {
                    if (string.Equals(sourceExtension, Path.GetExtension(gamePath), StringComparison.OrdinalIgnoreCase))
                        agreeing.Add(gamePath);
                    else
                        excluded.Add(new(gamePath, AppearanceCaptureExclusion.ExtensionMismatch));
                }
                candidates = agreeing;
                if (candidates.Count == 0)
                    continue;
                if (!hashed.TryGetValue(canonical, out var file))
                {
                    file = await HashFileAsync(canonical, cancellationToken).ConfigureAwait(false);
                    hashed[canonical] = file;
                }
                if (file.Blob is not { } blob)
                {
                    excluded.AddRange(candidates.Select(gamePath => new AppearanceCaptureExcluded(gamePath, file.Problem!.Value)));
                    continue;
                }
                foreach (var gamePath in candidates)
                {
                    if (byGamePath.TryGetValue(gamePath, out var existing))
                    {
                        // The same content under one game path is one entry; two
                        // different files for one game path refuse the capture.
                        if (existing != blob)
                            return Refused(AppearanceCaptureRefusal.ConflictingGamePath, gamePath);
                        continue;
                    }
                    byGamePath[gamePath] = blob;
                    sources.TryAdd(blob.Hash, canonical);
                }
            }
        }

        // A game path served by more than one source is a conflicting mapping
        // unless every source is a captured local file with the same content
        // (identical files count once). A mix with a vanilla, swapped or
        // excluded source refuses the capture: never half of a mapping.
        foreach (var (gamePath, actuals) in origins.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (actuals.Count < 2)
                continue;
            var consistent = byGamePath.TryGetValue(gamePath, out var captured) && actuals.All(actual =>
                canonicalOf.TryGetValue(actual, out var canonical) &&
                hashed.TryGetValue(canonical, out var file) && file.Blob == captured);
            if (!consistent)
                return Refused(AppearanceCaptureRefusal.ConflictingGamePath, gamePath);
        }

        if (byGamePath.Count > AppearanceLimits.MaximumFiles)
            return Refused(AppearanceCaptureRefusal.TooManyFiles);
        var glamourer = new AppearanceBlobRef(ContentHash.Of(state), state.Length);
        var distinct = byGamePath.Values.Append(glamourer).DistinctBy(blob => blob.Hash).Sum(blob => blob.Size);
        if (distinct > AppearanceLimits.MaximumBundleBytes)
            return Refused(AppearanceCaptureRefusal.BundleTooLarge);

        var manifest = AppearanceBundleManifest.TryCreate(owner, glamourer, null, null,
            byGamePath.Select(pair => new AppearanceFileEntry(pair.Key, pair.Value)), out _);
        if (manifest is null)
            return Refused(AppearanceCaptureRefusal.InvalidManifest);

        // The state is uploaded from a working file (the encoder streams files),
        // written only within a reservation of its size in the cache limit.
        IDisposable? reservation = null;
        if (reserve is not null)
        {
            reservation = await reserve(state.Length, cancellationToken).ConfigureAwait(false);
            if (reservation is null)
                return Refused(AppearanceCaptureRefusal.CacheFull);
        }
        string? working = null;
        try
        {
            Directory.CreateDirectory(workingDirectory);
            // Never written through a folder link.
            var folder = new DirectoryInfo(workingDirectory);
            if (folder.LinkTarget is not null || (folder.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The working folder is a link.");
            working = Path.Combine(workingDirectory, $"capture.{Guid.NewGuid():N}.glamourer");
            await File.WriteAllBytesAsync(working, state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeleteWorkingFile(working);
            reservation?.Dispose();
            return Refused(AppearanceCaptureRefusal.WorkingFileUnavailable);
        }
        catch
        {
            // Cancelled: no partial working file stays behind its released reservation.
            DeleteWorkingFile(working);
            reservation?.Dispose();
            throw;
        }
        sources.TryAdd(glamourer.Hash, working);
        return new(read.Source, read.DesignName, AppearanceCaptureRefusal.None, manifest, sources, working, excluded, vanilla, swapped, null, read, reservation);
    }

    private static void DeleteWorkingFile(string? working)
    {
        if (working is null)
            return;
        try
        {
            File.Delete(working);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The canonical path of <paramref name="actualPath"/> if it is provably a
    /// file beneath <paramref name="modDirectory"/>: both are canonicalized,
    /// device and extended-length forms are refused, and no file or folder
    /// between the root and the file may be a link or reparse point (its
    /// target could lie elsewhere). Otherwise null.
    /// </summary>
    internal static string? ContainedFile(string? modDirectory, string actualPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modDirectory) || !Path.IsPathFullyQualified(modDirectory) || !IsLocalFile(actualPath) ||
                IsDevicePath(modDirectory) || IsDevicePath(actualPath))
                return null;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modDirectory));
            var file = Path.GetFullPath(actualPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (root.Length == 0 || !file.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
                Path.EndsInDirectorySeparator(file))
                return null;
            var info = new FileInfo(file);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            for (var folder = info.Directory; folder is not null; folder = folder.Parent)
            {
                if (string.Equals(Path.TrimEndingDirectorySeparator(folder.FullName), root, comparison))
                    return file;
                if (folder.LinkTarget is not null || (folder.Attributes & FileAttributes.ReparsePoint) != 0)
                    return null;
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        path.StartsWith("//?/", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal);

    /// <summary>
    /// A canonical digest of a snapshot: the player object, the mod root, the
    /// sorted mapping and the Glamourer data. Two captures with the same
    /// digest read the same appearance; memory only, never sent.
    /// </summary>
    internal static string Fingerprint(AppearanceCaptureRead read)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "\0");
            sha.AppendData(BitConverter.GetBytes(bytes.Length));
            sha.AppendData(bytes);
        }
        Add(read.Source.ToString());
        Add(read.Player is { } player ? $"{player.ObjectIndex}:{player.Address}:{player.GameObjectId}" : null);
        Add(read.DesignId?.ToString("N"));
        Add(read.PenumbraModDirectory);
        Add(read.GlamourerState);
        foreach (var entry in read.Resources.OrderBy(entry => entry.ActualPath, StringComparer.Ordinal))
        {
            Add(entry.ActualPath);
            foreach (var gamePath in entry.GamePaths.OrderBy(path => path, StringComparer.Ordinal))
                Add(gamePath);
            Add("\n");
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>A redirect target on this computer (Penumbra gives game paths for vanilla and swapped resources).</summary>
    internal static bool IsLocalFile(string? actualPath) =>
        !string.IsNullOrEmpty(actualPath) && Path.IsPathFullyQualified(actualPath);

    /// <summary>Streams one local file through SHA-256 (bounded memory); problems are reported without the path.</summary>
    private static async Task<(AppearanceBlobRef? Blob, AppearanceCaptureExclusion? Problem)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length == 0)
                return (null, AppearanceCaptureExclusion.Empty);
            if (file.Length > AppearanceLimits.MaximumBlobBytes)
                return (null, AppearanceCaptureExclusion.TooLarge);
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
