using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RSyncStageCraft.Core.Appearance;

public enum AppearanceCacheState
{
    /// <summary>A verified copy is in the cache.</summary>
    Present,
    /// <summary>Not in the cache (or a corrupt copy was just removed).</summary>
    Missing,
    /// <summary>The cache could not be read or written.</summary>
    Unavailable,
}

public enum AppearanceCacheStoreStatus
{
    /// <summary>The verified content was published.</summary>
    Stored,
    /// <summary>A verified copy was already there; it was kept, not replaced.</summary>
    AlreadyPresent,
    /// <summary>The envelope or its content is not the expected blob; nothing was published.</summary>
    Invalid,
    /// <summary>The cache could not be written; nothing was published.</summary>
    Unavailable,
    /// <summary>
    /// There is no room within the cache's size limit: the entries in use
    /// and the space reserved for temporary files leave too little. Nothing was
    /// written.
    /// </summary>
    Full,
    /// <summary>
    /// The cache is over its selected limit (the limit was lowered below what is
    /// in use or reserved, or another program added files): every new file is
    /// refused until it is within the limit again. Nothing was written.
    /// </summary>
    OverLimit,
}

/// <summary>The outcome of a reservation of temporary space.</summary>
public enum AppearanceCacheReserveStatus
{
    Reserved,
    /// <summary>Entries in use, locked entries and other reservations leave too little room.</summary>
    Full,
    /// <summary>The cache is over its selected limit: every new reservation is refused until it is within it.</summary>
    OverLimit,
}

/// <summary>What the cache holds against its limit.</summary>
/// <param name="StoredBytes">Bytes of the stored entries (raw files), including any not recognised as a blob.</param>
/// <param name="InUseBytes">Bytes of the entries in use (pinned); these are never evicted.</param>
/// <param name="TemporaryBytes">Space reserved for temporary files being written (the bound of each, not its current length).</param>
public sealed record AppearanceCacheUsage(long LimitBytes, long StoredBytes, int Entries, long InUseBytes, int InUseEntries, long TemporaryBytes)
{
    public long TotalBytes => StoredBytes + TemporaryBytes;

    /// <summary>
    /// Over the selected limit: only because the limit was lowered below what
    /// is in use or reserved, or another program added files. New downloads are
    /// paused (every reservation is refused) until it is within the limit.
    /// </summary>
    public bool OverLimit => TotalBytes > LimitBytes;
}

/// <summary>What a trim or "Clear unused" did: entries removed, and entries kept because they were in use, locked or could not be deleted.</summary>
public sealed record AppearanceCacheTrim(int Removed, long RemovedBytes, int Kept, long KeptBytes);

/// <summary>
/// A verified cache entry held in use: it is never evicted or cleared
/// until the lease is disposed. Dispose it when the file is no longer needed.
/// </summary>
public sealed class AppearanceCacheLease : IDisposable
{
    private readonly AppearanceBlobCache cache;
    private int released;

    internal AppearanceCacheLease(AppearanceBlobCache cache, AppearanceBlobRef blob, string path)
    {
        this.cache = cache;
        Blob = blob;
        Path = path;
    }

    public AppearanceBlobRef Blob { get; }

    /// <summary>The verified raw file (local only; never logged or sent).</summary>
    public string Path { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
            cache.Unpin(Blob.Hash);
    }

    public override string ToString() => $"Cache lease {Blob.Hash.Value}";
}

/// <summary>
/// A verified cache entry held for as long as it is needed (a lifetime
/// lease). Like <see cref="AppearanceCacheLease"/> it is never evicted or
/// cleared while held; in addition the file stays open for reading with no
/// write or delete sharing, so on Windows no other program can change,
/// replace or delete it while it is held. (Elsewhere the sharing mode is only
/// advisory and does not stop other programs.) Its content was hashed through
/// this same handle when it was acquired, whatever the cache trusted before.
/// Dispose it when the file is no longer needed: the handle is closed first,
/// then the entry is released.
/// </summary>
public sealed class AppearanceCacheHeldLease : IDisposable
{
    private readonly AppearanceBlobCache cache;
    private FileStream? file;
    private byte[]? bytes;
    private int released;

    internal AppearanceCacheHeldLease(AppearanceBlobCache cache, AppearanceBlobRef blob, string path, FileStream file, byte[]? bytes)
    {
        this.cache = cache;
        this.file = file;
        this.bytes = bytes;
        Blob = blob;
        Path = path;
    }

    public AppearanceBlobRef Blob { get; }

    /// <summary>The held raw file (local only; never logged or sent).</summary>
    public string Path { get; }

    /// <summary>True until the lease is disposed.</summary>
    public bool IsHeld => Volatile.Read(ref released) == 0;

    /// <summary>
    /// The verified bytes, exactly those hashed through the held handle, when
    /// they were requested within the acquisition's bound; otherwise (and
    /// after disposal) null.
    /// </summary>
    public ReadOnlyMemory<byte>? Bytes
    {
        get
        {
            if (!IsHeld || Volatile.Read(ref bytes) is not { } content)
                return null;
            return content;
        }
    }

    /// <summary>Forgets the kept bytes (the handle and the pin stay); <see cref="Bytes"/> is then null.</summary>
    internal void DropBytes() => Volatile.Write(ref bytes, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
            return;
        Volatile.Write(ref bytes, null);
        cache.ReleaseHeld(Blob.Hash, Interlocked.Exchange(ref file, null));
    }

    public override string ToString() => $"Held cache lease {Blob.Hash.Value}";
}

/// <summary>
/// Space reserved within the cache limit for temporary files. Released
/// on dispose, or taken over by the entry it becomes when published.
/// </summary>
public sealed class AppearanceCacheReservation : IDisposable
{
    private readonly AppearanceBlobCache cache;
    private int released;

    internal AppearanceCacheReservation(AppearanceBlobCache cache, long bytes)
    {
        this.cache = cache;
        Bytes = bytes;
    }

    public long Bytes { get; }

    /// <summary>Marks it released; true only the first time.</summary>
    internal bool TryTake() => Interlocked.Exchange(ref released, 1) == 0;

    public void Dispose()
    {
        if (TryTake())
            cache.Release(Bytes);
    }
}

/// <summary>
/// The client's local content-addressed cache of raw appearance blobs.
/// <list type="bullet">
/// <item>A dedicated, versioned directory (<c>v1</c>) holding only disposable
/// copies: deleting it loses no settings, catalogue state or authored data.</item>
/// <item>Raw files at <c>v1/blobs/&lt;2 hex&gt;/&lt;raw SHA-256&gt;</c>; temporary
/// files in <c>v1/tmp</c> under the same root (same filesystem), so the final
/// publication is an atomic rename. Temporary files are removed at startup and
/// whenever an operation fails or is cancelled.</item>
/// <item>Every entry is fully re-hashed on its first use in a session. Only a
/// verified entry is never replaced; one proven corrupt is deleted under the
/// blob's lock so a verified download can take its place.</item>
/// <item>Validation, removal and publication of a hash are serialized by a
/// per-hash lock (one cache instance per folder).</item>
/// <item>Size limit: 10 GiB by default,
/// adjustable from 2 to 64 GiB. Stored entries plus the space reserved for
/// temporary files never exceed it: every temporary file is written only
/// within a reservation of its bound. To make room the least recently used
/// entries are evicted (an entry's modification time records its last
/// acquisition, so the order survives restarts). An entry in use (leased), an
/// entry whose hash lock is held, and a temporary file being written are never
/// evicted; when they leave too little room the new file is refused
/// (<see cref="AppearanceCacheStoreStatus.Full"/>). "Clear unused" obeys the
/// same rules.</item>
/// <item>RSC never causes growth beyond the selected limit. Usage may exceed it
/// only when the limit is lowered below what is in use or reserved, or another
/// program adds files: then every new reservation is refused
/// (<see cref="AppearanceCacheStoreStatus.OverLimit"/>) and eligible entries are
/// trimmed as they are released, until usage is within the limit. Links and
/// reparse points are never followed.</item>
/// </list>
/// </summary>
public sealed class AppearanceBlobCache
{
    public const string FormatFolder = "v1";
    public const string BlobsFolder = "blobs";
    public const string TemporaryFolder = "tmp";

    public const long DefaultLimitBytes = 10L * 1024 * 1024 * 1024;
    public const long MinimumLimitBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumLimitBytes = 64L * 1024 * 1024 * 1024;

    private const int BufferBytes = 81920;
    private readonly Dictionary<string, HashLock> locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> verified = new(StringComparer.Ordinal);

    // Size accounting. Lock order: an async hash lock (if any), then
    // `sync`, then the `locks` monitor; `sync` is never taken while holding
    // the `locks` monitor. Eviction runs under `evictionGate`, so it is the
    // only path that adds reserved bytes and space is never promised twice.
    private readonly object sync = new();
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly Dictionary<string, Entry> entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, int> pins = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim evictionGate = new(1, 1);
    private int heldHandles;
    private long storedBytes;
    private long reservedBytes;
    private long limitBytes;

    /// <summary>
    /// Opens (and creates) the cache under <paramref name="directory"/> with the
    /// default limit, removes temporary files a previous session left behind,
    /// and evicts the least recently used entries above the limit.
    /// </summary>
    public AppearanceBlobCache(string directory)
        : this(directory, DefaultLimitBytes)
    {
    }

    /// <summary>As above with <paramref name="limitBytes"/> (2 to 64 GiB).</summary>
    public AppearanceBlobCache(string directory, long limitBytes)
        : this(directory, IsValidLimit(limitBytes) ? limitBytes : throw new ArgumentOutOfRangeException(nameof(limitBytes)), validated: true)
    {
    }

    private AppearanceBlobCache(string directory, long limitBytes, bool validated)
    {
        _ = validated; // distinguishes the unchecked constructor (tests) from the public ones
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        Root = Path.Combine(Path.GetFullPath(directory), FormatFolder);
        BlobsDirectory = Path.Combine(Root, BlobsFolder);
        TemporaryDirectory = Path.Combine(Root, TemporaryFolder);
        Directory.CreateDirectory(BlobsDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
        // Links are never followed: a cache whose own folders are links is refused.
        if (!IsInsideWithoutLinks(BlobsDirectory) || !IsInsideWithoutLinks(TemporaryDirectory))
            throw new IOException("The appearance cache folder is a link.");
        RemoveTemporaryFiles();
        this.limitBytes = limitBytes;
        Scan(startup: true);
        StartupTrim = Evict(0, reserve: false, all: false).Trim;
    }

    /// <summary>Test seam: any positive limit, so tests can use small files.</summary>
    internal static AppearanceBlobCache CreateForTests(string directory, long limitBytes) => new(directory, limitBytes, validated: false);

    public static bool IsValidLimit(long limitBytes) => limitBytes is >= MinimumLimitBytes and <= MaximumLimitBytes;

    /// <summary>
    /// Space a blob upload reserves for its temporary files: the envelope (at
    /// most the header and the raw size) and the Brotli scratch file (at most
    /// the raw size plus Brotli's worst-case expansion of a few bytes per
    /// 16 KiB, with a wide margin).
    /// </summary>
    public static long UploadTemporaryBytes(long rawSize) =>
        AppearanceBlobEnvelopeHeader.Length + rawSize + rawSize + (rawSize / 1024) + (64 * 1024);

    public long LimitBytes
    {
        get
        {
            lock (sync)
                return limitBytes;
        }
    }

    /// <summary>What the startup trim removed (entries above the limit).</summary>
    public AppearanceCacheTrim StartupTrim { get; }

    public AppearanceCacheUsage Usage
    {
        get
        {
            lock (sync)
            {
                long inUse = 0;
                var inUseEntries = 0;
                foreach (var entry in entries.Values)
                {
                    if (entry.Hash is { } hash && pins.ContainsKey(hash.Value))
                    {
                        inUse += entry.Bytes;
                        inUseEntries++;
                    }
                }
                return new(limitBytes, storedBytes, entries.Count, inUse, inUseEntries, reservedBytes);
            }
        }
    }

    /// <summary>The versioned cache root (<c>.../v1</c>).</summary>
    public string Root { get; }
    public string BlobsDirectory { get; }
    public string TemporaryDirectory { get; }

    public string PathOf(ContentHash hash) => Path.Combine(BlobsDirectory, hash.Value[..2], hash.Value);

    /// <summary>Removes every temporary file (startup; nothing is in flight).</summary>
    public int RemoveTemporaryFiles()
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(TemporaryDirectory))
        {
            if (TryDelete(file))
                removed++;
        }
        return removed;
    }

    /// <summary>A new unique temporary file path in the cache's temporary folder.</summary>
    public string NewTemporaryPath(ContentHash hash, string kind)
    {
        // Recreated if the disposable cache was deleted while in use.
        Directory.CreateDirectory(TemporaryDirectory);
        if (!IsInsideWithoutLinks(TemporaryDirectory))
            throw new IOException("The appearance cache folder is a link.");
        return Path.Combine(TemporaryDirectory, $"{hash.Value}.{Guid.NewGuid():N}.{kind}");
    }

    /// <summary>
    /// The path of a verified copy of <paramref name="blob"/>. An entry not yet
    /// verified in this session is fully re-hashed first, under the blob's lock;
    /// a corrupt one is deleted and reported missing.
    /// </summary>
    public async Task<(AppearanceCacheState State, string? Path)> TryGetAsync(AppearanceBlobRef blob, CancellationToken cancellationToken = default)
    {
        if (blob is not { IsValid: true })
            return (AppearanceCacheState.Missing, null);
        await Hook("verify:before-lock", blob.Hash).ConfigureAwait(false);
        using var held = await LockAsync(blob.Hash, cancellationToken).ConfigureAwait(false);
        await Hook("verify:in-lock", blob.Hash).ConfigureAwait(false);
        var state = await EnsureVerifiedLockedAsync(blob, cancellationToken).ConfigureAwait(false);
        if (state != AppearanceCacheState.Present)
            return (state, null);
        Touch(blob.Hash);
        return (state, PathOf(blob.Hash));
    }

    /// <summary>
    /// Like <see cref="TryGetAsync"/>, and holds the verified entry in use: it
    /// is never evicted or cleared until the returned lease is disposed. A
    /// successful acquisition records the entry as most recently used (its
    /// modification time), once per acquisition.
    /// </summary>
    public async Task<(AppearanceCacheState State, AppearanceCacheLease? Lease)> AcquireAsync(AppearanceBlobRef blob, CancellationToken cancellationToken = default)
    {
        if (blob is not { IsValid: true })
            return (AppearanceCacheState.Missing, null);
        await Hook("verify:before-lock", blob.Hash).ConfigureAwait(false);
        using var held = await LockAsync(blob.Hash, cancellationToken).ConfigureAwait(false);
        await Hook("verify:in-lock", blob.Hash).ConfigureAwait(false);
        var state = await EnsureVerifiedLockedAsync(blob, cancellationToken).ConfigureAwait(false);
        if (state != AppearanceCacheState.Present)
            return (state, null);
        return (state, Acquired(blob));
    }

    /// <summary>
    /// A held, write-locked lease of a verified copy of <paramref name="blob"/>
    /// (a lifetime lease). Under the blob's lock the entry is opened for
    /// reading with no write or delete sharing, then read and hashed in full
    /// through that same handle: what this session trusted before is never
    /// relied on. A match is pinned, recorded as used and returned with the
    /// handle still open, and with the verified bytes when
    /// <paramref name="maximumBytes"/> admits them (0 keeps none). A mismatch
    /// is proven corrupt: the session's trust in the hash is withdrawn, the
    /// entry is removed and the result is <see cref="AppearanceCacheState.Missing"/>.
    /// A file that cannot be opened this way (another program has it open for
    /// writing) or read is <see cref="AppearanceCacheState.Unavailable"/>.
    /// On every failure or cancellation the handle is closed and nothing
    /// stays pinned.
    /// </summary>
    public async Task<(AppearanceCacheState State, AppearanceCacheHeldLease? Lease)> AcquireHeldAsync(
        AppearanceBlobRef blob, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (blob is not { IsValid: true })
            return (AppearanceCacheState.Missing, null);
        await Hook("held:before-lock", blob.Hash).ConfigureAwait(false);
        using var held = await LockAsync(blob.Hash, cancellationToken).ConfigureAwait(false);
        await Hook("held:in-lock", blob.Hash).ConfigureAwait(false);
        return await HoldLockedAsync(blob, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Withdraws this session's trust in <paramref name="hash"/>: its next use
    /// re-hashes the entry in full. Called when a read of a leased entry did
    /// not match. It only ever causes a re-hash, so it needs no lock.
    /// </summary>
    internal void Distrust(ContentHash hash) => verified.TryRemove(hash.Value, out _);

    /// <summary>Test seam: handles currently open for held leases (and for acquisitions in progress).</summary>
    internal int HeldHandleCount => Volatile.Read(ref heldHandles);

    /// <summary>
    /// Reserves <paramref name="bytes"/> within the limit for temporary files,
    /// evicting the least recently used unused entries if needed. Null when
    /// the entries in use, locked entries and other reservations leave too
    /// little room (nothing is evicted beyond what was needed).
    /// </summary>
    public async Task<AppearanceCacheReservation?> ReserveAsync(long bytes, CancellationToken cancellationToken = default) =>
        (await TryReserveAsync(bytes, cancellationToken).ConfigureAwait(false)).Reservation;

    /// <summary>
    /// As <see cref="ReserveAsync"/>, with the reason for a refusal. While the
    /// cache is over its selected limit every reservation is refused at once
    /// (<see cref="AppearanceCacheReserveStatus.OverLimit"/>), so RSC never
    /// causes further growth beyond the limit.
    /// </summary>
    public async Task<(AppearanceCacheReserveStatus Status, AppearanceCacheReservation? Reservation)> TryReserveAsync(
        long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        await evictionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = Evict(bytes, reserve: true, all: false).Status;
            return (status, status == AppearanceCacheReserveStatus.Reserved ? new AppearanceCacheReservation(this, bytes) : null);
        }
        finally
        {
            evictionGate.Release();
        }
    }

    /// <summary>
    /// Counts files another program added under the cache folder since the
    /// last count (startup, a limit change, "Clear unused" or a refresh) and
    /// trims unused entries above the limit. Such files are counted, never
    /// trusted: a blob is used only after its normal full verification.
    /// </summary>
    public async Task<AppearanceCacheUsage> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await evictionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Scan(startup: false);
            Evict(0, reserve: false, all: false);
        }
        finally
        {
            evictionGate.Release();
        }
        return Usage;
    }

    /// <summary>
    /// Sets the limit (2 to 64 GiB) and evicts the least recently used unused
    /// entries above it. Entries in use stay even if they exceed a lowered
    /// limit; no new file is admitted until there is room again.
    /// </summary>
    public Task<AppearanceCacheTrim> SetLimitAsync(long limitBytes, CancellationToken cancellationToken = default)
    {
        if (!IsValidLimit(limitBytes))
            throw new ArgumentOutOfRangeException(nameof(limitBytes));
        return SetLimitCoreAsync(limitBytes, cancellationToken);
    }

    /// <summary>Test seam: any positive limit.</summary>
    internal Task<AppearanceCacheTrim> SetLimitForTestsAsync(long limitBytes) => SetLimitCoreAsync(limitBytes, CancellationToken.None);

    private async Task<AppearanceCacheTrim> SetLimitCoreAsync(long limitBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        await evictionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (sync)
                this.limitBytes = limitBytes;
            // Never deletes an entry in use or cancels a reservation to meet a
            // lowered limit: those are trimmed as they are released.
            Scan(startup: false);
            return Evict(0, reserve: false, all: false).Trim;
        }
        finally
        {
            evictionGate.Release();
        }
    }

    /// <summary>
    /// "Clear unused": removes every entry that is not in use and whose hash
    /// lock is free. Entries in use, locked entries and temporary files being
    /// written are kept.
    /// </summary>
    public async Task<AppearanceCacheTrim> ClearUnusedAsync(CancellationToken cancellationToken = default)
    {
        await evictionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Scan(startup: false);
            return Evict(0, reserve: false, all: true).Trim;
        }
        finally
        {
            evictionGate.Release();
        }
    }

    /// <summary>
    /// Decodes one envelope from <paramref name="envelope"/> into a temporary
    /// file, checks it is exactly <paramref name="expected"/> (envelope header,
    /// codec, stored and raw sizes, nothing after it, raw SHA-256), and only then
    /// publishes it atomically under the blob's lock. A verified copy already
    /// there is kept; a corrupt one is replaced. On any failure or cancellation
    /// nothing is published and the temporary file is removed.
    /// </summary>
    public async Task<(AppearanceCacheStoreStatus Status, AppearanceEnvelopeStatus Envelope, AppearancePayloadStatus Payload)> StoreEnvelopeAsync(
        Stream envelope, AppearanceBlobRef expected, CancellationToken cancellationToken = default)
    {
        var (status, envelopeStatus, payloadStatus, lease) = await StoreCoreAsync(envelope, expected, pin: false, cancellationToken).ConfigureAwait(false);
        lease?.Dispose();
        return (status, envelopeStatus, payloadStatus);
    }

    /// <summary>
    /// As <see cref="StoreEnvelopeAsync"/>, and on success (stored or already
    /// present) also holds the entry in use, acquired under the same lock.
    /// </summary>
    public async Task<(AppearanceCacheStoreStatus Status, AppearanceCacheLease? Lease)> StoreAndAcquireEnvelopeAsync(
        Stream envelope, AppearanceBlobRef expected, CancellationToken cancellationToken = default)
    {
        var (status, _, _, lease) = await StoreCoreAsync(envelope, expected, pin: true, cancellationToken).ConfigureAwait(false);
        return (status, lease);
    }

    private async Task<(AppearanceCacheStoreStatus Status, AppearanceEnvelopeStatus Envelope, AppearancePayloadStatus Payload, AppearanceCacheLease? Lease)> StoreCoreAsync(
        Stream envelope, AppearanceBlobRef expected, bool pin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (expected is not { IsValid: true })
            throw new ArgumentException("The expected blob is not valid.", nameof(expected));
        // The decoded temporary file is at most the raw size (the decoder
        // stops beyond it); it is written only within a reservation of that.
        var (reserveStatus, reservation) = await TryReserveAsync(expected.Size, cancellationToken).ConfigureAwait(false);
        using var reserved = reservation;
        if (reservation is null)
        {
            // No room for a temporary file: an entry already there is still
            // found (nothing is read or written); otherwise it is refused.
            using var present = await LockAsync(expected.Hash, cancellationToken).ConfigureAwait(false);
            if (await EnsureVerifiedLockedAsync(expected, cancellationToken).ConfigureAwait(false) != AppearanceCacheState.Present)
                return (reserveStatus == AppearanceCacheReserveStatus.OverLimit ? AppearanceCacheStoreStatus.OverLimit : AppearanceCacheStoreStatus.Full,
                    AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid, null);
            return (AppearanceCacheStoreStatus.AlreadyPresent, AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid, pin ? Acquired(expected) : null);
        }
        string temporary;
        try
        {
            temporary = NewTemporaryPath(expected.Hash, "raw");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (AppearanceCacheStoreStatus.Unavailable, AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid, null);
        }
        try
        {
            AppearanceEnvelopeStatus envelopeStatus;
            AppearancePayloadStatus payloadStatus;
            try
            {
                await using var raw = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes);
                (envelopeStatus, payloadStatus) = await AppearanceBlobEnvelope.DecodeAsync(envelope, raw, expected, cancellationToken).ConfigureAwait(false);
                if (envelopeStatus != AppearanceEnvelopeStatus.Valid || payloadStatus != AppearancePayloadStatus.Valid)
                    return (AppearanceCacheStoreStatus.Invalid, envelopeStatus, payloadStatus, null);
                raw.Flush(flushToDisk: true);
            }
            // A failure of the source stream itself (the network) is not an
            // IOException here: the transfer client reports it as its own type.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (AppearanceCacheStoreStatus.Unavailable, AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid, null);
            }

            await Hook("publish:before-lock", expected.Hash).ConfigureAwait(false);
            using var held = await LockAsync(expected.Hash, cancellationToken).ConfigureAwait(false);
            try
            {
                await Hook("publish:in-lock", expected.Hash).ConfigureAwait(false);
                var present = await EnsureVerifiedLockedAsync(expected, cancellationToken).ConfigureAwait(false);
                if (present == AppearanceCacheState.Present)
                    return (AppearanceCacheStoreStatus.AlreadyPresent, AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid, pin ? Acquired(expected) : null);
                if (present == AppearanceCacheState.Unavailable)
                    return (AppearanceCacheStoreStatus.Unavailable, AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid, null);
                var destination = PathOf(expected.Hash);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Never published through a folder link.
                if (!IsInsideWithoutLinks(Path.GetDirectoryName(destination)!))
                    return (AppearanceCacheStoreStatus.Unavailable, AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid, null);
                File.Move(temporary, destination, overwrite: false);
                verified[expected.Hash.Value] = expected.Size;
                // The reservation becomes the entry: the total does not grow.
                AppearanceCacheLease? lease;
                lock (sync)
                {
                    IndexLocked(destination, expected.Hash, expected.Size, DateTime.UtcNow);
                    if (reservation.TryTake())
                        reservedBytes -= reservation.Bytes;
                    lease = pin ? PinLocked(expected) : null;
                    // Over a limit lowered meanwhile: an unused new entry is trimmed.
                    ScheduleTrimIfOverLimitLocked();
                }
                return (AppearanceCacheStoreStatus.Stored, AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid, lease);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (AppearanceCacheStoreStatus.Unavailable, AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid, null);
            }
        }
        finally
        {
            // A temporary file that could not be deleted keeps its space
            // reserved until the next startup removes it.
            if (!TryDelete(temporary))
                reservation.TryTake();
        }
    }

    /// <summary>Under the hash lock: holds a present entry in use and records the use.</summary>
    private AppearanceCacheLease Acquired(AppearanceBlobRef blob)
    {
        var lease = Pin(blob);
        Touch(blob.Hash);
        return lease;
    }

    /// <summary>
    /// Under the blob's lock: Present if a verified copy is there (fully
    /// re-hashed on its first use in this session), Missing if there is none
    /// or a corrupt one was just deleted, Unavailable if it cannot be read or
    /// a corrupt one cannot be removed.
    /// </summary>
    private async Task<AppearanceCacheState> EnsureVerifiedLockedAsync(AppearanceBlobRef blob, CancellationToken cancellationToken)
    {
        var path = PathOf(blob.Hash);
        try
        {
            // Never read through a folder link (its folder, blobs/ or the root).
            if (!IsInsideWithoutLinks(Path.GetDirectoryName(path)!))
                return AppearanceCacheState.Unavailable;
            if (Directory.Exists(path))
                return AppearanceCacheState.Unavailable;
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                // A link or reparse point is never followed or trusted: the link
                // itself (never its target) is removed like a corrupt entry.
                File.Delete(path);
                verified.TryRemove(blob.Hash.Value, out _);
                Unindex(path);
                return AppearanceCacheState.Missing;
            }
            if (!info.Exists)
            {
                verified.TryRemove(blob.Hash.Value, out _);
                Unindex(path);
                return AppearanceCacheState.Missing;
            }
            if (info.Length == blob.Size && verified.TryGetValue(blob.Hash.Value, out var size) && size == blob.Size)
            {
                Indexed(path, blob.Hash, info);
                return AppearanceCacheState.Present;
            }
            verified.TryRemove(blob.Hash.Value, out _);
            if (info.Length == blob.Size && await HashesToAsync(path, blob, cancellationToken).ConfigureAwait(false))
            {
                verified[blob.Hash.Value] = blob.Size;
                Indexed(path, blob.Hash, info);
                return AppearanceCacheState.Present;
            }
            // Proven corrupt: this exact entry is removed so a verified copy can replace it.
            File.Delete(path);
            Unindex(path);
            return AppearanceCacheState.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return AppearanceCacheState.Unavailable;
        }
    }

    /// <summary>Under the blob's lock: see <see cref="AcquireHeldAsync"/>.</summary>
    private async Task<(AppearanceCacheState State, AppearanceCacheHeldLease? Lease)> HoldLockedAsync(
        AppearanceBlobRef blob, long maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathOf(blob.Hash);
        FileStream? file = null;
        try
        {
            // Never read through a folder link (its folder, blobs/ or the root).
            if (!IsInsideWithoutLinks(Path.GetDirectoryName(path)!))
                return (AppearanceCacheState.Unavailable, null);
            if (Directory.Exists(path))
                return (AppearanceCacheState.Unavailable, null);
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                // A link or reparse point is never followed or trusted: the link
                // itself (never its target) is removed like a corrupt entry.
                File.Delete(path);
                Distrust(blob.Hash);
                Unindex(path);
                return (AppearanceCacheState.Missing, null);
            }
            if (!info.Exists)
            {
                Distrust(blob.Hash);
                Unindex(path);
                return (AppearanceCacheState.Missing, null);
            }
            try
            {
                // Read access, read sharing only: while open, no other handle can
                // write or delete it, and it cannot be opened at all while another
                // handle already has it open for writing.
                file = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                Distrust(blob.Hash);
                Unindex(path);
                return (AppearanceCacheState.Missing, null);
            }
            Interlocked.Increment(ref heldHandles);
            await Hook("held:opened", blob.Hash).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Until this re-hash completes nothing is trusted: a failed or
            // cancelled read leaves the next use to re-hash in full.
            Distrust(blob.Hash);
            var keep = blob.Size <= maximumBytes && blob.Size <= Array.MaxLength;
            var (matches, content) = await ReadHeldAsync(file, blob, keep, cancellationToken).ConfigureAwait(false);
            if (!matches)
            {
                // Proven corrupt through the held handle: the trust is withdrawn
                // and this exact entry is removed so a verified copy can replace it.
                CloseHeld(file);
                file = null;
                Distrust(blob.Hash);
                File.Delete(path);
                Unindex(path);
                return (AppearanceCacheState.Missing, null);
            }
            verified[blob.Hash.Value] = blob.Size;
            Indexed(path, blob.Hash, new FileInfo(path));
            lock (sync)
                pins[blob.Hash.Value] = pins.GetValueOrDefault(blob.Hash.Value) + 1;
            Touch(blob.Hash);
            var lease = new AppearanceCacheHeldLease(this, blob, path, file, content);
            file = null;
            return (AppearanceCacheState.Present, lease);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (AppearanceCacheState.Unavailable, null);
        }
        finally
        {
            if (file is not null)
                CloseHeld(file);
        }
    }

    /// <summary>
    /// Reads the whole held file through its own handle, hashing as it goes;
    /// keeps the bytes when asked. Content longer than the expected size is
    /// refused as soon as a read passes it, so at most one read buffer
    /// (BufferBytes, 81,920 bytes) beyond that size is consumed, never the
    /// rest of the file.
    /// </summary>
    private static async Task<(bool Matches, byte[]? Bytes)> ReadHeldAsync(
        FileStream file, AppearanceBlobRef blob, bool keep, CancellationToken cancellationToken)
    {
        var content = keep ? new byte[blob.Size] : null;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int read;
            while ((read = await file.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (total + read > blob.Size)
                    return (false, null);
                sha.AppendData(buffer, 0, read);
                if (content is not null)
                    buffer.AsSpan(0, read).CopyTo(content.AsSpan((int)total));
                total += read;
            }
            var matches = total == blob.Size &&
                string.Equals(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), blob.Hash.Value, StringComparison.Ordinal);
            return matches ? (true, content) : (false, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Closes a held handle and counts it closed.</summary>
    private void CloseHeld(FileStream file)
    {
        try
        {
            file.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only handle has nothing to flush; closing it cannot lose data.
        }
        Interlocked.Decrement(ref heldHandles);
    }

    /// <summary>A held lease was disposed: its handle is closed first, then the entry is released.</summary>
    internal void ReleaseHeld(ContentHash hash, FileStream? file)
    {
        if (file is not null)
            CloseHeld(file);
        Unpin(hash);
    }

    private static async Task<bool> HashesToAsync(string path, AppearanceBlobRef blob, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = 0;
            int read;
            while ((read = await file.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > blob.Size)
                    return false;
                sha.AppendData(buffer, 0, read);
            }
            return total == blob.Size &&
                string.Equals(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), blob.Hash.Value, StringComparison.Ordinal);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---- Size accounting ----------------------------------------------------

    private sealed class Entry(string path, ContentHash? hash, long bytes, DateTime lastUse)
    {
        public string Path { get; } = path;
        /// <summary>Null for a file that is not a blob (counted, evicted first by age, never locked or pinned).</summary>
        public ContentHash? Hash { get; } = hash;
        public long Bytes { get; set; } = bytes;
        public DateTime LastUse { get; set; } = lastUse;
        public bool Deleting { get; set; }
    }

    /// <summary>
    /// Counts every file under the cache root. A regular file is a blob entry
    /// only at its canonical path (and is still verified in full before use);
    /// any other file counts too and is evicted by age. A link or reparse point
    /// is never followed: a file link counts as an entry of no size (only the
    /// link is ever deleted) and a folder link is skipped. At startup,
    /// temporary files the cleanup could not remove stay reserved until a later
    /// startup removes them; later counts leave temporary files to their
    /// reservations. Files already counted are updated, never removed here.
    /// </summary>
    private void Scan(bool startup)
    {
        if (!IsInsideWithoutLinks(BlobsDirectory))
            return;
        lock (sync)
        {
            foreach (var file in FilesBelow(new DirectoryInfo(Root)))
            {
                var isLink = file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0;
                if (IsBelow(file.FullName, TemporaryDirectory))
                {
                    if (startup)
                        reservedBytes += isLink ? 0 : file.Length;
                    continue;
                }
                if (isLink)
                    IndexLocked(file.FullName, null, 0, file.LastWriteTimeUtc);
                else if (ContentHash.TryParse(file.Name.ToLowerInvariant(), out var hash) &&
                    string.Equals(PathOf(hash), file.FullName, PathComparison))
                    IndexLocked(PathOf(hash), hash, file.Length, file.LastWriteTimeUtc);
                else
                    IndexLocked(file.FullName, null, file.Length, file.LastWriteTimeUtc);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="folder"/> is the cache root or below it and
    /// neither it nor any folder up to the root is a link or reparse point.
    /// </summary>
    private bool IsInsideWithoutLinks(string folder)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Root);
            for (var current = new DirectoryInfo(Path.TrimEndingDirectorySeparator(folder)); current is not null; current = current.Parent)
            {
                if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                    return false;
                if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), root, PathComparison))
                    return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cannot be checked: treated as a link (fail closed).
            return false;
        }
    }

    private static IEnumerable<FileInfo> FilesBelow(DirectoryInfo folder)
    {
        foreach (var file in folder.EnumerateFiles())
            yield return file;
        foreach (var child in folder.EnumerateDirectories())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            foreach (var file in FilesBelow(child))
                yield return file;
        }
    }

    private static bool IsBelow(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    private void IndexLocked(string path, ContentHash? hash, long bytes, DateTime lastUse)
    {
        if (entries.TryGetValue(path, out var entry))
        {
            storedBytes += bytes - entry.Bytes;
            entry.Bytes = bytes;
            if (lastUse > entry.LastUse)
                entry.LastUse = lastUse;
            return;
        }
        entries[path] = new Entry(path, hash, bytes, lastUse);
        storedBytes += bytes;
    }

    /// <summary>Under the hash lock: the verified file is indexed with its size (its last use kept).</summary>
    private void Indexed(string path, ContentHash hash, FileInfo info)
    {
        lock (sync)
        {
            if (entries.TryGetValue(path, out var entry))
            {
                storedBytes += info.Length - entry.Bytes;
                entry.Bytes = info.Length;
                // Counted as a plain file at startup: it is this blob, so its
                // lease and lock now protect it.
                if (entry.Hash is null && !entry.Deleting)
                    entries[path] = new Entry(path, hash, info.Length, entry.LastUse);
            }
            else
            {
                IndexLocked(path, hash, info.Length, info.LastWriteTimeUtc);
            }
            // A file another program added can put the cache over its limit.
            ScheduleTrimIfOverLimitLocked();
        }
    }

    private void Unindex(string path)
    {
        lock (sync)
        {
            if (entries.Remove(path, out var entry))
                storedBytes -= entry.Bytes;
        }
    }

    /// <summary>Records a successful acquisition: the modification time (persisted) and the in-memory order.</summary>
    private void Touch(ContentHash hash)
    {
        var path = PathOf(hash);
        var now = DateTime.UtcNow;
        try
        {
            File.SetLastWriteTimeUtc(path, now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The order still holds for this session.
        }
        lock (sync)
        {
            if (entries.TryGetValue(path, out var entry))
                entry.LastUse = now;
        }
    }

    private AppearanceCacheLease Pin(AppearanceBlobRef blob)
    {
        lock (sync)
            return PinLocked(blob);
    }

    private AppearanceCacheLease PinLocked(AppearanceBlobRef blob)
    {
        pins[blob.Hash.Value] = pins.GetValueOrDefault(blob.Hash.Value) + 1;
        return new AppearanceCacheLease(this, blob, PathOf(blob.Hash));
    }

    internal void Unpin(ContentHash hash)
    {
        lock (sync)
        {
            if (pins.TryGetValue(hash.Value, out var count))
            {
                if (count <= 1)
                    pins.Remove(hash.Value);
                else
                    pins[hash.Value] = count - 1;
            }
            ScheduleTrimIfOverLimitLocked();
        }
    }

    internal void Release(long bytes)
    {
        lock (sync)
        {
            reservedBytes -= bytes;
            ScheduleTrimIfOverLimitLocked();
        }
    }

    private Task trimTask = Task.CompletedTask;
    private bool trimScheduled;

    /// <summary>
    /// Under <see cref="sync"/>: while over the limit, trims the entries that
    /// have become eligible (released, published or found) in the background,
    /// off the caller's thread (a lease may be released on the game's thread).
    /// </summary>
    private void ScheduleTrimIfOverLimit()
    {
        lock (sync)
            ScheduleTrimIfOverLimitLocked();
    }

    private void ScheduleTrimIfOverLimitLocked()
    {
        if (trimScheduled || storedBytes + reservedBytes <= limitBytes)
            return;
        trimScheduled = true;
        trimTask = Task.Run(async () =>
        {
            await evictionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (sync)
                    trimScheduled = false;
                Evict(0, reserve: false, all: false);
            }
            catch (Exception)
            {
                // Best effort: retried on the next release or refused reservation.
            }
            finally
            {
                evictionGate.Release();
            }
        });
    }

    /// <summary>Test seam: the latest background trim.</summary>
    internal Task TrimTask
    {
        get
        {
            lock (sync)
                return trimTask;
        }
    }

    /// <summary>
    /// Under <see cref="evictionGate"/>: evicts the least recently used unused
    /// entries until <paramref name="needed"/> more bytes fit (or, with
    /// <paramref name="all"/>, every unused entry), and reserves them when
    /// asked. An entry in use or whose hash lock is held is skipped, as is one
    /// that cannot be deleted. A reservation that cannot fit even after every
    /// entry not in use and not locked is gone evicts nothing. Bytes of an
    /// entry being deleted still count until it is gone, so the limit holds on
    /// disk at every moment. The order is taken once per call.
    /// </summary>
    private (AppearanceCacheReserveStatus Status, AppearanceCacheTrim Trim) Evict(long needed, bool reserve, bool all)
    {
        var removed = 0;
        long removedBytes = 0;
        List<Entry> order;
        lock (sync)
        {
            // Over the selected limit: every reservation is refused at once.
            if (reserve && storedBytes + reservedBytes > limitBytes)
            {
                // Self-healing: anything eligible that a missed event left is trimmed.
                ScheduleTrimIfOverLimitLocked();
                return (AppearanceCacheReserveStatus.OverLimit, TrimLocked(removed, removedBytes));
            }
            if (!all && storedBytes + reservedBytes + needed <= limitBytes)
            {
                if (reserve)
                    reservedBytes += needed;
                return (AppearanceCacheReserveStatus.Reserved, TrimLocked(removed, removedBytes));
            }
            if (reserve && HeldBytesLocked() + reservedBytes + needed > limitBytes)
                return (AppearanceCacheReserveStatus.Full, TrimLocked(removed, removedBytes));
            order = entries.Values.OrderBy(entry => entry.LastUse).ThenBy(entry => entry.Path, StringComparer.Ordinal).ToList();
        }
        foreach (var candidate in order)
        {
            HashLock? held = null;
            lock (sync)
            {
                if (!all && storedBytes + reservedBytes + needed <= limitBytes)
                {
                    if (reserve)
                        reservedBytes += needed;
                    return (AppearanceCacheReserveStatus.Reserved, TrimLocked(removed, removedBytes));
                }
                // Checked again now: still indexed, not in use, and its lock free.
                if (!entries.TryGetValue(candidate.Path, out var current) || !ReferenceEquals(current, candidate) ||
                    candidate.Deleting || IsPinnedLocked(candidate))
                    continue;
                if (candidate.Hash is { } hash && (held = TryTakeLock(hash)) is null)
                    continue;
                candidate.Deleting = true;
            }

            // Deleted outside `sync` but under the entry's hash lock; its bytes
            // still count until the file is gone.
            // Never deleted through a folder link (only files inside the cache).
            var deleted = IsInsideWithoutLinks(Path.GetDirectoryName(candidate.Path)!) && TryDelete(candidate.Path);
            lock (sync)
            {
                candidate.Deleting = false;
                if (deleted)
                {
                    if (entries.TryGetValue(candidate.Path, out var current) && ReferenceEquals(current, candidate))
                    {
                        entries.Remove(candidate.Path);
                        storedBytes -= candidate.Bytes;
                    }
                    removed++;
                    removedBytes += candidate.Bytes;
                }
            }
            if (candidate.Hash is { } candidateHash)
            {
                if (deleted)
                    verified.TryRemove(candidateHash.Value, out _);
                held!.Gate.Release();
                Leave(candidateHash.Value, held);
            }
        }
        lock (sync)
        {
            var fits = storedBytes + reservedBytes + needed <= limitBytes;
            if (!all && fits && reserve)
                reservedBytes += needed;
            return (all || fits ? AppearanceCacheReserveStatus.Reserved : AppearanceCacheReserveStatus.Full, TrimLocked(removed, removedBytes));
        }
    }

    private bool IsPinnedLocked(Entry entry) => entry.Hash is { } hash && pins.ContainsKey(hash.Value);

    /// <summary>Bytes that eviction cannot reclaim now: entries in use or whose hash lock is held.</summary>
    private long HeldBytesLocked()
    {
        long bytes = 0;
        foreach (var entry in entries.Values)
        {
            if (entry.Hash is not { } hash)
                continue;
            bool locked;
            lock (locks)
                locked = locks.ContainsKey(hash.Value);
            if (locked || pins.ContainsKey(hash.Value))
                bytes += entry.Bytes;
        }
        return bytes;
    }

    private AppearanceCacheTrim TrimLocked(int removed, long removedBytes) => new(removed, removedBytes, entries.Count, storedBytes);

    /// <summary>The hash lock if nobody holds or waits for it (never waits); null otherwise.</summary>
    private HashLock? TryTakeLock(ContentHash hash)
    {
        lock (locks)
        {
            if (locks.ContainsKey(hash.Value))
                return null;
            var entry = new HashLock { Users = 1 };
            locks[hash.Value] = entry;
            entry.Gate.Wait(0);
            return entry;
        }
    }

    /// <summary>Takes the lock of exactly this hash (created on demand, removed when no one holds or waits for it).</summary>
    private async Task<IDisposable> LockAsync(ContentHash hash, CancellationToken cancellationToken)
    {
        HashLock entry;
        lock (locks)
        {
            if (locks.TryGetValue(hash.Value, out var found))
                entry = found;
            else
                locks[hash.Value] = entry = new HashLock();
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(hash.Value, entry);
            throw;
        }
        return new Held(this, hash.Value, entry);
    }

    private void Leave(string hash, HashLock entry)
    {
        lock (locks)
        {
            if (--entry.Users == 0)
                locks.Remove(hash);
        }
    }

    private sealed class HashLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Held(AppearanceBlobCache cache, string hash, HashLock entry) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
                return;
            entry.Gate.Release();
            cache.Leave(hash, entry);
            // An entry over a lowered limit may have been skipped while locked.
            cache.ScheduleTrimIfOverLimit();
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Test seam: awaited at named points so a test can force an interleaving. Null in production.</summary>
    internal Func<string, ContentHash, Task>? TestHook { get; set; }

    private Task Hook(string point, ContentHash hash) => TestHook?.Invoke(point, hash) ?? Task.CompletedTask;
}
