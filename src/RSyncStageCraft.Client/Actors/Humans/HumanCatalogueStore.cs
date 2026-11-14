#if RSC_DEVELOPMENT
using System.Buffers;
using System.Security.Cryptography;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>How importing one file into the store ended.</summary>
public enum HumanCatalogueImportStatus
{
    /// <summary>Copied, and the copy's hash and size are the expected ones.</summary>
    Stored,
    /// <summary>The same content was already stored, and was verified by a full re-hash.</summary>
    AlreadyStored,
    /// <summary>The source's content is not what the plan hashed (it changed, or it was replaced).</summary>
    SourceChanged,
    /// <summary>The source could not be read.</summary>
    SourceUnreadable,
    /// <summary>The store does not have room within its limit; nothing is evicted.</summary>
    Full,
    /// <summary>A stored file with this name does not verify; it is reported and kept, never replaced or deleted.</summary>
    StoredFileDamaged,
    /// <summary>The store could not be written.</summary>
    StoreUnavailable,
}

/// <summary>One entry as <see cref="HumanCatalogueStore.List"/> reports it: no path and no stored data.</summary>
public sealed record HumanCatalogueListing(
    string Label, string ShortId, DateTimeOffset CapturedUtc, int GamePaths, int Files, long Bytes, int Outside, bool Complete,
    HumanCatalogueSkeleton? Skeleton, HumanCatalogueVersions Versions, int? ProfileBones = null);

/// <summary>The catalogue as listed: its entries, entry files that could not be read, and stored files no entry names.</summary>
public sealed record HumanCatalogueList(IReadOnlyList<HumanCatalogueListing> Entries, int UnreadableEntries, int OrphanFiles, long UsedBytes, long LimitBytes);

/// <summary>
/// HV-5 M1 (Debug only): the local catalogue store. It is not the disposable
/// appearance cache and shares nothing with it.
/// <list type="bullet">
/// <item><c>blobs/&lt;sha-256&gt;.blob</c>: raw copies of the owner's mod
/// files, named by content. A file is copied to a temporary name while it is
/// hashed, then renamed; a copy whose hash or size is not the planned one is
/// removed and the capture is refused.</item>
/// <item><c>entries/&lt;entry id&gt;.json</c>: one immutable entry each,
/// written to a temporary name and renamed without overwriting.</item>
/// <item>Nothing is ever evicted, replaced or deleted, apart from this
/// store's own temporary files (removed at startup and on failure). A
/// capture that does not fit within the limit (owner decision D6: 2 GiB) is
/// refused. Files no entry names are only counted.</item>
/// <item>The folder and its two subfolders are never used through a link.</item>
/// </list>
/// One capture at a time: every write is serialized by the store.
/// </summary>
public sealed class HumanCatalogueStore
{
    /// <summary>Owner decision D6: 2 GiB, no eviction.</summary>
    public const long DefaultLimitBytes = 2L * 1024 * 1024 * 1024;

    private const string BlobExtension = ".blob";
    private const string EntryExtension = ".json";
    private const string TemporaryExtension = ".tmp";
    private const int BufferBytes = 81920;

    private readonly string blobs;
    private readonly string entries;
    private readonly SemaphoreSlim writer = new(1, 1);

    /// <summary>Creates the folders if needed and removes this store's leftover temporary files.</summary>
    /// <exception cref="IOException">The folder or a subfolder is a link, or cannot be prepared.</exception>
    public HumanCatalogueStore(string root, long limitBytes = DefaultLimitBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        Root = Path.GetFullPath(root);
        LimitBytes = limitBytes;
        blobs = Path.Combine(Root, "blobs");
        entries = Path.Combine(Root, "entries");
        foreach (var folder in new[] { Root, blobs, entries })
        {
            Directory.CreateDirectory(folder);
            RefuseLink(folder);
        }
        foreach (var folder in new[] { blobs, entries })
        {
            foreach (var leftover in Directory.EnumerateFiles(folder, "*" + TemporaryExtension))
                TryDelete(leftover);
        }
    }

    public string Root { get; }

    public long LimitBytes { get; }

    /// <summary>Every stored file and entry file, in bytes (temporary files excluded).</summary>
    public long UsedBytes() =>
        Directory.EnumerateFiles(blobs, "*" + BlobExtension).Concat(Directory.EnumerateFiles(entries, "*" + EntryExtension))
            .Sum(file => new FileInfo(file).Length);

    /// <summary>
    /// The bytes still to be copied for <paramref name="files"/> (those not
    /// already stored), and whether they and an entry of
    /// <paramref name="entryBytes"/> fit within the limit together. Checked
    /// before any copy, so a capture that cannot fit copies nothing (D6:
    /// refused, never evicted). Each import re-checks.
    /// </summary>
    public (bool Fits, long Missing) Room(IEnumerable<AppearanceBlobRef> files, long entryBytes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfNegative(entryBytes);
        long missing = 0;
        foreach (var file in files.DistinctBy(file => file.Hash))
        {
            if (!File.Exists(BlobFile(file.Hash)))
                missing += file.Size;
        }
        var used = UsedBytes();
        return (missing <= LimitBytes && used <= LimitBytes - missing && entryBytes <= LimitBytes - missing - used, missing);
    }

    /// <summary>
    /// Copies <paramref name="sourceFile"/> into the store as <paramref name="expected"/>.
    /// The source is read once, through one handle, and hashed while it is
    /// copied; only a copy with exactly the expected hash and size is kept.
    /// An existing stored file of that name is re-hashed in full and kept
    /// whatever the result.
    /// </summary>
    public async Task<HumanCatalogueImportStatus> ImportAsync(string sourceFile, AppearanceBlobRef expected, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.IsValid)
            throw new ArgumentException("not a valid blob", nameof(expected));
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var final = BlobFile(expected.Hash);
            if (File.Exists(final))
                return !IsLink(final) && await VerifyAsync(final, expected, cancellationToken).ConfigureAwait(false)
                    ? HumanCatalogueImportStatus.AlreadyStored
                    : HumanCatalogueImportStatus.StoredFileDamaged;
            long used;
            try
            {
                used = UsedBytes();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return HumanCatalogueImportStatus.StoreUnavailable;
            }
            if (used > LimitBytes - expected.Size)
                return HumanCatalogueImportStatus.Full;
            var temporary = Path.Combine(blobs, $"{expected.Hash.Value}.{Guid.NewGuid():N}{TemporaryExtension}");
            HumanCatalogueImportStatus status;
            try
            {
                status = await CopyAsync(sourceFile, temporary, expected, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Cancelled: no partial copy stays behind.
                TryDelete(temporary);
                throw;
            }
            if (status != HumanCatalogueImportStatus.Stored)
            {
                TryDelete(temporary);
                return status;
            }
            try
            {
                File.Move(temporary, final, overwrite: false);
                return HumanCatalogueImportStatus.Stored;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                return HumanCatalogueImportStatus.StoreUnavailable;
            }
        }
        finally
        {
            writer.Release();
        }
    }

    /// <summary>
    /// Writes <paramref name="entry"/> as a new, immutable entry file. Refused
    /// (false, with the reason) if its label is already used, if any file it
    /// names is not stored with its size, or if it does not fit within the limit.
    /// </summary>
    public async Task<(bool Written, string? Refusal)> WriteEntryAsync(HumanCatalogueEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var bytes = HumanCatalogueEntryCodec.Serialize(entry);
        if (bytes.Length > HumanCatalogueEntryCodec.MaximumEntryBytes)
            return (false, "the entry is too large");
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (existing, _) = ReadEntries();
            if (existing.Any(other => HumanCatalogueLabel.Same(other.Label, entry.Label)))
                return (false, "that label is already used");
            if (existing.Any(other => other.EntryId == entry.EntryId))
                return (false, "that entry id is already used");
            foreach (var blob in entry.Blobs)
            {
                var file = new FileInfo(BlobFile(Hash(blob.Hash)));
                if (!file.Exists || file.Length != blob.Size)
                    return (false, "a file of the entry is not stored");
            }
            if (UsedBytes() > LimitBytes - bytes.Length)
                return (false, "the catalogue is full");
            var final = Path.Combine(entries, entry.EntryId.ToString("N") + EntryExtension);
            var temporary = Path.Combine(entries, $"{entry.EntryId:N}.{Guid.NewGuid():N}{TemporaryExtension}");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, final, overwrite: false);
                return (true, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                return (false, "the entry could not be written");
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }
        finally
        {
            writer.Release();
        }
    }

    /// <summary>
    /// HV-5 M2: the one entry with <paramref name="label"/> (ignoring case),
    /// read and checked as <see cref="List"/> reads it, or null with the
    /// reason. Read only: nothing in the store is written, renamed or deleted.
    /// </summary>
    public HumanCatalogueEntry? FindEntry(string label, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(label);
        var (found, unreadable) = ReadEntries();
        var matches = found.Where(entry => HumanCatalogueLabel.Same(entry.Label, label)).ToList();
        refusal = matches.Count switch
        {
            1 => null,
            0 when unreadable > 0 => $"no readable entry has that label ({unreadable} entry file(s) could not be read)",
            0 => "no entry has that label",
            _ => "more than one entry has that label",
        };
        return refusal is null ? matches[0] : null;
    }

    /// <summary>
    /// HV-5 M2: one held lease on a stored file. The file is opened once,
    /// for reading, sharing only reading (so nothing can write, replace or
    /// delete it while held), re-hashed in full through that handle, and kept
    /// open. A missing, linked, resized or damaged file is reported, never
    /// replaced or deleted.
    /// </summary>
    public async Task<(HumanCatalogueHold? Hold, string? Problem)> HoldAsync(HumanCatalogueBlob blob, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (!ContentHash.TryParse(blob.Hash, out var hash))
            return (null, "has no valid hash");
        var file = BlobFile(hash);
        FileStream stream;
        try
        {
            if (!File.Exists(file))
                return (null, "is missing");
            if (IsLink(file))
                return (null, "is a link");
            stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, "could not be opened");
        }
        var kept = false;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            if (stream.Length != blob.Size)
                return (null, "has the wrong size");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
                sha.AppendData(buffer, 0, read);
            if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), hash.Value, StringComparison.Ordinal))
                return (null, "does not verify");
            stream.Position = 0;
            kept = true;
            return (new HumanCatalogueHold(hash.Value, blob.Size, file, stream), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, "could not be read");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!kept)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>True if an entry already uses <paramref name="label"/> (ignoring case).</summary>
    public bool HasLabel(string label) => ReadEntries().Entries.Any(entry => HumanCatalogueLabel.Same(entry.Label, label));

    /// <summary>
    /// Every entry, with completeness: each file it names is stored with its
    /// size (not re-hashed here; a spawn re-verifies). No path is reported.
    /// </summary>
    public HumanCatalogueList List()
    {
        var (found, unreadable) = ReadEntries();
        var stored = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(blobs, "*" + BlobExtension))
            stored[Path.GetFileNameWithoutExtension(file)] = new FileInfo(file).Length;
        var named = new HashSet<string>(StringComparer.Ordinal);
        var listings = new List<HumanCatalogueListing>(found.Count);
        foreach (var entry in found.OrderBy(entry => entry.CapturedUtc).ThenBy(entry => entry.EntryId))
        {
            var complete = true;
            foreach (var blob in entry.Blobs)
            {
                named.Add(blob.Hash);
                complete &= stored.TryGetValue(blob.Hash, out var size) && size == blob.Size;
            }
            listings.Add(new HumanCatalogueListing(entry.Label, entry.ShortId, entry.CapturedUtc, entry.Paths.Count, entry.Counts.Files,
                entry.Counts.Bytes, entry.Counts.Outside, complete, entry.Skeleton, entry.Versions, entry.ProfileCounts?.Bones));
        }
        return new HumanCatalogueList(listings, unreadable, stored.Keys.Count(hash => !named.Contains(hash)), UsedBytes(), LimitBytes);
    }

    private (List<HumanCatalogueEntry> Entries, int Unreadable) ReadEntries()
    {
        var found = new List<HumanCatalogueEntry>();
        var unreadable = 0;
        foreach (var file in Directory.EnumerateFiles(entries, "*" + EntryExtension))
        {
            HumanCatalogueEntry? entry = null;
            try
            {
                var info = new FileInfo(file);
                if (info.Length <= HumanCatalogueEntryCodec.MaximumEntryBytes)
                    entry = HumanCatalogueEntryCodec.TryParse(File.ReadAllBytes(file), out _);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            // The file name must be the entry's own id. HV-6: a received entry is
            // never written here, so one found in this folder is unreadable.
            if (entry is not null && entry.Format != HumanCatalogueEntry.SharedFormat &&
                Path.GetFileNameWithoutExtension(file) == entry.EntryId.ToString("N"))
                found.Add(entry);
            else
                unreadable++;
        }
        return (found, unreadable);
    }

    private string BlobFile(ContentHash hash) => Path.Combine(blobs, hash.Value + BlobExtension);

    private static ContentHash Hash(string text) =>
        ContentHash.TryParse(text, out var hash) ? hash : throw new ArgumentException("not a content hash", nameof(text));

    private static async Task<HumanCatalogueImportStatus> CopyAsync(string source, string temporary, AppearanceBlobRef expected, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            FileStream input;
            try
            {
                input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return HumanCatalogueImportStatus.SourceUnreadable;
            }
            await using (input)
            {
                FileStream output;
                try
                {
                    output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return HumanCatalogueImportStatus.StoreUnavailable;
                }
                await using (output)
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long total = 0;
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await input.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            return HumanCatalogueImportStatus.SourceUnreadable;
                        }
                        if (read == 0)
                            break;
                        total += read;
                        // Never more than the planned size: a grown file has changed.
                        if (total > expected.Size)
                            return HumanCatalogueImportStatus.SourceChanged;
                        sha.AppendData(buffer, 0, read);
                        try
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            return HumanCatalogueImportStatus.StoreUnavailable;
                        }
                    }
                    if (total != expected.Size ||
                        !string.Equals(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), expected.Hash.Value, StringComparison.Ordinal))
                        return HumanCatalogueImportStatus.SourceChanged;
                    try
                    {
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        return HumanCatalogueImportStatus.StoreUnavailable;
                    }
                }
            }
            return HumanCatalogueImportStatus.Stored;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A full re-hash of a stored file. Unreadable counts as not verified.</summary>
    private static async Task<bool> VerifyAsync(string file, AppearanceBlobRef expected, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expected.Size)
                return false;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
                sha.AppendData(buffer, 0, read);
            return string.Equals(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), expected.Hash.Value, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // A stored file that is a link is never followed: it counts as damaged (kept, not replaced).
    private static bool IsLink(string file)
    {
        var info = new FileInfo(file);
        return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static void RefuseLink(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A catalogue folder is a link.");
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
#endif
