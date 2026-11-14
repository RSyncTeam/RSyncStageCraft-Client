using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RSyncStageCraft.Core.Connection;

namespace RSyncStageCraft.Core.Appearance;

/// <summary>Timeouts for appearance transfers.</summary>
public sealed class AppearanceTransferOptions
{
    /// <summary>Establishing a connection (TCP and TLS).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The longest time without progress: waiting for response headers after
    /// the request was sent, or between body bytes in either direction. There
    /// is no absolute limit, so a slow but progressing 64 MiB transfer completes.
    /// </summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>A downloaded bundle: its exact canonical manifest bytes, with every blob verified in the cache.</summary>
public sealed record AppearanceBundleDownload(AppearanceTransferResult Result, AppearanceBundleManifest? Manifest = null, byte[]? ManifestBytes = null)
{
    /// <summary>HV-6: how many of the bundle's blobs this download fetched (the rest were already verified in the cache).</summary>
    public int DownloadedBlobs { get; init; }
}

/// <summary>Where an upload is: only file uploads can be cancelled; a commit request, once sent, cannot.</summary>
public enum AppearanceUploadPhase
{
    /// <summary>The first commit request is in flight (it may already commit when nothing is missing).</summary>
    CheckingServer,
    /// <summary>Missing files are being encoded and uploaded; cancellable.</summary>
    UploadingFiles,
    /// <summary>The final commit request is in flight; no longer cancellable.</summary>
    Finishing,
}

/// <summary>A verified manifest only (exact canonical bytes whose SHA-256 is the bundle id); no blobs.</summary>
public sealed record AppearanceManifestRead(AppearanceTransferResult Result, AppearanceBundleManifest? Manifest = null, byte[]? ManifestBytes = null);

/// <summary>The account's own storage use and limits.</summary>
public sealed record AppearanceUsage(long UsedBytes, long QuotaBytes, int Entries, int EntryLimit);

public sealed record AppearanceUsageRead(AppearanceTransferResult Result, AppearanceUsage? Usage = null);

/// <summary>
/// One catalogue entry the account may see: its own, or another group
/// member's published one. Name, description, folder and the published state
/// are mutable metadata outside the immutable bundle id.
/// </summary>
public sealed record AppearanceBundleSummary(
    ContentHash BundleId, string Owner, long TotalBytes, long StoredBytes, DateTimeOffset CreatedAt,
    bool Published, string Name, string Description, string Folder);

public sealed record AppearanceBundleList(AppearanceTransferResult Result, IReadOnlyList<AppearanceBundleSummary> Bundles);

/// <summary>One catalogue entry as the server answers it now (the final authorization check).</summary>
public sealed record AppearanceBundleRead(AppearanceTransferResult Result, AppearanceBundleSummary? Bundle = null);

/// <summary>
/// A bundle's Glamourer blob only, read from a verified cache entry and
/// re-hashed: its exact bytes, or why not. <paramref name="TooLarge"/>: the
/// manifest declares it larger than asked for, so nothing was downloaded.
/// </summary>
public sealed record AppearanceGlamourerRead(AppearanceTransferResult Result, ContentHash? Blob = null, byte[]? Bytes = null, bool TooLarge = false);

/// <summary>
/// A metadata change: only the members that are set are sent. There is no
/// named-user sharing in the first release:
/// "published" means published to this private group.
/// </summary>
public sealed record AppearanceBundleEdit(string? Name = null, string? Description = null, string? Folder = null, bool? Published = null);

/// <summary>
/// Uploads and downloads appearance bundles over HTTPS to the RSC
/// server's appearance routes. Not wired to any UI or game call yet.
/// <list type="bullet">
/// <item>The address is derived from the control endpoint
/// (<see cref="AppearanceEndpoint"/>); the bearer key is attached only after
/// that and the key's format check pass. Redirects are never followed.</item>
/// <item>Encoding, upload, download, decoding and hashing stream with bounded
/// memory; downloads are verified locally (envelope, sizes, raw SHA-256)
/// before anything is published to the cache.</item>
/// <item>Bounded connect timeout plus an inactivity timeout; no absolute limit.</item>
/// <item>No automatic retries, and a request body is never sent twice:
/// 503, interruptions and timeouts return typed retryable results.</item>
/// <item>Results never carry exception text or server free text.</item>
/// </list>
/// </summary>
public sealed class AppearanceTransferClient : IDisposable
{
    private const int BufferBytes = 81920;
    private const int ErrorBodyBytes = 16 * 1024;
    private const int CommitAnswerBytes = 1024 * 1024;
    private const int ListAnswerBytes = 4 * 1024 * 1024;

    private readonly Func<ServerConnectionSettings> settings;
    private readonly AppearanceTransferOptions options;
    private readonly HttpClient http;
    private readonly SemaphoreSlim encodeGate = new(1, 1);
    private readonly object holdGate = new();
    private readonly HashSet<AppearanceBundleHold> holds = [];
    private bool disposed;

    public AppearanceTransferClient(Func<ServerConnectionSettings> settings, AppearanceBlobCache cache, AppearanceTransferOptions? options = null)
        : this(settings, cache, options, CreateHandler(options ?? new AppearanceTransferOptions()))
    {
    }

    internal AppearanceTransferClient(
        Func<ServerConnectionSettings> settings, AppearanceBlobCache cache, AppearanceTransferOptions? options, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(handler);
        this.settings = settings;
        Cache = cache;
        this.options = options ?? new AppearanceTransferOptions();
        http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public AppearanceBlobCache Cache { get; }

    /// <summary>Test seam: awaited at "encode", "upload", "download" and "decode". Null in production.</summary>
    internal Func<string, Task>? TestHook { get; set; }

    /// <summary>
    /// Test seam: opens an upload's local source instead of the file system
    /// (a controlled stream that supplies more bytes than it declares). The
    /// stream must be seekable. Null in production.
    /// </summary>
    internal Func<string, Stream?>? TestSourceOpener { get; set; }

    internal static SocketsHttpHandler CreateHandler(AppearanceTransferOptions options) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = options.ConnectTimeout,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Uploads a bundle: commits the manifest, uploads each blob the server
    /// reports missing for this account (in full, encoded to a temporary file
    /// one at a time from <paramref name="sources"/>, the raw file of each
    /// blob), then commits again. A blob whose source is not exactly the
    /// manifest's content is refused before anything is sent for it.
    /// </summary>
    public async Task<AppearanceTransferResult> UploadBundleAsync(
        AppearanceBundleManifest manifest,
        IReadOnlyDictionary<ContentHash, string> sources,
        ContentHash? replaces = null,
        CancellationToken cancellationToken = default,
        Action<AppearanceUploadPhase>? phase = null,
        Action<int>? missing = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(sources);
        if (Prepare() is not { } target)
            return new(AppearanceTransferStatus.Disabled);
        if (cancellationToken.IsCancellationRequested)
            return new(AppearanceTransferStatus.Cancelled);
        var query = replaces is { } old ? $"?replaces={old.Value}" : string.Empty;

        // A commit, once sent, is never cancelled: the server could commit while
        // the client reported "cancelled". Cancellation is honoured before it.
        phase?.Invoke(AppearanceUploadPhase.CheckingServer);
        var first = await CommitAsync(target, manifest, query, CancellationToken.None).ConfigureAwait(false);
        if (first.Result is not { Status: AppearanceTransferStatus.MissingBlobs })
        {
            // HV-6's counts: nothing was missing when the first commit already succeeded.
            if (first.Result.Succeeded)
                missing?.Invoke(0);
            return first.Result;
        }
        missing?.Invoke(first.Missing.Count);
        phase?.Invoke(AppearanceUploadPhase.UploadingFiles);
        var blobs = manifest.Blobs.ToDictionary(blob => blob.Hash);
        foreach (var hash in first.Missing)
        {
            if (!sources.TryGetValue(hash, out var path))
                return new(AppearanceTransferStatus.LocalContentInvalid, Blob: hash);
            var uploaded = await UploadBlobAsync(target, blobs[hash], path, query, cancellationToken).ConfigureAwait(false);
            if (!uploaded.Succeeded)
                return uploaded;
        }
        if (cancellationToken.IsCancellationRequested)
            return new(AppearanceTransferStatus.Cancelled);
        phase?.Invoke(AppearanceUploadPhase.Finishing);
        var second = await CommitAsync(target, manifest, query, CancellationToken.None).ConfigureAwait(false);
        return second.Result;
    }

    /// <summary>
    /// Downloads a bundle the account may see: its manifest (accepted only as
    /// the exact canonical bytes whose SHA-256 is <paramref name="bundleId"/>)
    /// and every blob not already verified in the cache, one at a time. Every
    /// blob of the bundle is held in use until the download ends, so making
    /// room for one never evicts another; a bundle that does not fit
    /// within the cache limit fails with
    /// <see cref="AppearanceTransferStatus.CacheFull"/>.
    /// </summary>
    /// <para>HV-6: <paramref name="readOptions"/> reads a format-3 human package
    /// only when the caller opts in; the default reads format 1 only, as before.</para>
    public async Task<AppearanceBundleDownload> DownloadBundleAsync(ContentHash bundleId, CancellationToken cancellationToken = default,
        AppearanceManifestReadOptions readOptions = AppearanceManifestReadOptions.FormatOneOnly)
    {
        if (Prepare() is not { } target)
            return new(new(AppearanceTransferStatus.Disabled));
        var read = await ManifestAsync(target, bundleId, cancellationToken, readOptions).ConfigureAwait(false);
        if (read.Manifest is not { } manifest)
            return new(read.Result);
        var bytes = read.ManifestBytes!;

        var leases = new List<AppearanceCacheLease>();
        var fetched = 0;
        try
        {
            foreach (var blob in manifest.Blobs)
            {
                AppearanceCacheState state;
                AppearanceCacheLease? lease;
                try
                {
                    (state, lease) = await Cache.AcquireAsync(blob, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new(new(AppearanceTransferStatus.Cancelled, Blob: blob.Hash));
                }
                if (lease is not null)
                {
                    leases.Add(lease);
                    continue;
                }
                if (state == AppearanceCacheState.Unavailable)
                    return new(new(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash));
                var downloaded = await DownloadBlobAsync(target, bundleId, blob, leases, cancellationToken).ConfigureAwait(false);
                if (!downloaded.Succeeded)
                    return new(downloaded);
                fetched++;
            }
            return new(AppearanceTransferResult.Success, manifest, bytes) { DownloadedBlobs = fetched };
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    /// <summary>
    /// Reads a bundle's manifest only, accepted only as the exact canonical
    /// bytes whose SHA-256 is <paramref name="bundleId"/> (for example to show
    /// whether an entry carries mod files). Downloads no blobs.
    /// </summary>
    public async Task<AppearanceManifestRead> GetManifestAsync(ContentHash bundleId, CancellationToken cancellationToken = default) =>
        Prepare() is { } target
            ? await ManifestAsync(target, bundleId, cancellationToken).ConfigureAwait(false)
            : new(new(AppearanceTransferStatus.Disabled));

    /// <summary>
    /// Reads a bundle's manifest (the server checks the account may read
    /// it) and then only its Glamourer blob, from the cache or downloaded from
    /// the server (which checks again). No other blob is touched. The blob is
    /// refused before any download if the manifest declares it larger than
    /// <paramref name="maximumBytes"/>. The returned bytes are re-read from the
    /// verified cache entry under a lease and re-hashed.
    /// </summary>
    public async Task<AppearanceGlamourerRead> ReadGlamourerStateAsync(ContentHash bundleId, long maximumBytes, CancellationToken cancellationToken = default)
    {
        if (Prepare() is not { } target)
            return new(new(AppearanceTransferStatus.Disabled));
        var read = await ManifestAsync(target, bundleId, cancellationToken).ConfigureAwait(false);
        if (read.Manifest is not { } manifest)
            return new(read.Result);
        var blob = manifest.Glamourer;
        if (blob.Size > maximumBytes)
            return new(AppearanceTransferResult.Success, blob.Hash, TooLarge: true);

        var leases = new List<AppearanceCacheLease>();
        try
        {
            AppearanceCacheState state;
            AppearanceCacheLease? lease;
            try
            {
                (state, lease) = await Cache.AcquireAsync(blob, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(new(AppearanceTransferStatus.Cancelled, Blob: blob.Hash), blob.Hash);
            }
            if (lease is not null)
            {
                leases.Add(lease);
            }
            else
            {
                if (state == AppearanceCacheState.Unavailable)
                    return new(new(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash), blob.Hash);
                var downloaded = await DownloadBlobAsync(target, bundleId, blob, leases, cancellationToken).ConfigureAwait(false);
                if (!downloaded.Succeeded)
                    return new(downloaded, blob.Hash);
            }
            if (leases.FirstOrDefault(held => held.Blob.Hash == blob.Hash) is not { } entry)
                return new(new(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash), blob.Hash);
            var bytes = await ReadVerifiedAsync(entry.Path, blob, cancellationToken).ConfigureAwait(false);
            // A read that does not match withdraws the session's trust in the
            // entry, so its next use re-hashes it (and replaces it if corrupt).
            if (bytes is null)
                Cache.Distrust(blob.Hash);
            return bytes is null
                ? new(new(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash), blob.Hash)
                : new(AppearanceTransferResult.Success, blob.Hash, bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(new(AppearanceTransferStatus.Cancelled, Blob: blob.Hash), blob.Hash);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    /// <summary>
    /// One catalogue entry as the server answers it now (404 when it is
    /// gone or no longer visible to this account). Used as the final
    /// authorization check; nothing cached stands in for it.
    /// </summary>
    public async Task<AppearanceBundleRead> GetBundleAsync(ContentHash bundleId, CancellationToken cancellationToken = default)
    {
        if (!bundleId.IsValid)
            return new(new(AppearanceTransferStatus.NotFound));
        if (Prepare() is not { } target)
            return new(new(AppearanceTransferStatus.Disabled));
        if (cancellationToken.IsCancellationRequested)
            return new(new(AppearanceTransferStatus.Cancelled));
        return await GuardAsync(cancellationToken, async watchdog =>
        {
            using var request = Request(target, HttpMethod.Get, $"bundles/{bundleId.Value}");
            using var response = await SendAsync(request, watchdog).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return new AppearanceBundleRead(await FailureAsync(response, watchdog).ConfigureAwait(false));
            var body = await ReadBoundedAsync(response, ErrorBodyBytes, watchdog).ConfigureAwait(false);
            return ParseBundle(body) is { } bundle
                ? new AppearanceBundleRead(AppearanceTransferResult.Success, bundle)
                : new AppearanceBundleRead(new(AppearanceTransferStatus.InvalidResponse));
        }, failure => new AppearanceBundleRead(failure)).ConfigureAwait(false);
    }

    /// <summary>The account's storage use and limits (sends nothing about any capture).</summary>
    public async Task<AppearanceUsageRead> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        if (Prepare() is not { } target)
            return new(new(AppearanceTransferStatus.Disabled));
        if (cancellationToken.IsCancellationRequested)
            return new(new(AppearanceTransferStatus.Cancelled));
        return await GuardAsync(cancellationToken, async watchdog =>
        {
            using var request = Request(target, HttpMethod.Get, "usage");
            using var response = await SendAsync(request, watchdog).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return new AppearanceUsageRead(await FailureAsync(response, watchdog).ConfigureAwait(false));
            var body = await ReadBoundedAsync(response, ErrorBodyBytes, watchdog).ConfigureAwait(false);
            return ParseUsage(body) is { } usage
                ? new AppearanceUsageRead(AppearanceTransferResult.Success, usage)
                : new AppearanceUsageRead(new(AppearanceTransferStatus.InvalidResponse));
        }, failure => new AppearanceUsageRead(failure)).ConfigureAwait(false);
    }

    /// <summary>The entries this account may see: its own, and other group members' published ones.</summary>
    public async Task<AppearanceBundleList> ListBundlesAsync(CancellationToken cancellationToken = default)
    {
        if (Prepare() is not { } target)
            return new(new(AppearanceTransferStatus.Disabled), []);
        if (cancellationToken.IsCancellationRequested)
            return new(new(AppearanceTransferStatus.Cancelled), []);
        return await GuardAsync(cancellationToken, async watchdog =>
        {
            using var request = Request(target, HttpMethod.Get, "bundles");
            using var response = await SendAsync(request, watchdog).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return new AppearanceBundleList(await FailureAsync(response, watchdog).ConfigureAwait(false), []);
            var body = await ReadBoundedAsync(response, ListAnswerBytes, watchdog).ConfigureAwait(false);
            return ParseList(body) is { } bundles
                ? new AppearanceBundleList(AppearanceTransferResult.Success, bundles)
                : new AppearanceBundleList(new(AppearanceTransferStatus.InvalidResponse), []);
        }, failure => new AppearanceBundleList(failure, [])).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes an own entry's mutable metadata (name, description, folder,
    /// published to the group). The bundle id never changes. Owner only; the
    /// server enforces it.
    /// </summary>
    public async Task<AppearanceTransferResult> UpdateBundleAsync(ContentHash bundleId, AppearanceBundleEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!bundleId.IsValid)
            return new(AppearanceTransferStatus.NotFound);
        if (Prepare() is not { } target)
            return new(AppearanceTransferStatus.Disabled);
        if (cancellationToken.IsCancellationRequested)
            return new(AppearanceTransferStatus.Cancelled);
        var body = EditBody(edit);
        return await GuardAsync(cancellationToken, async watchdog =>
        {
            using var request = Request(target, HttpMethod.Patch, $"bundles/{bundleId.Value}");
            request.Content = new WatchedBytesContent(body, watchdog);
            using var response = await SendAsync(request, watchdog).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK
                ? AppearanceTransferResult.Success
                : await EditFailureAsync(response, watchdog).ConfigureAwait(false);
        }, failure => failure).ConfigureAwait(false);
    }

    /// <summary>Deletes an own entry; refused while a scene or catalogue object still refers to it (unpublish instead).</summary>
    public async Task<AppearanceTransferResult> DeleteBundleAsync(ContentHash bundleId, CancellationToken cancellationToken = default)
    {
        if (!bundleId.IsValid)
            return new(AppearanceTransferStatus.NotFound);
        if (Prepare() is not { } target)
            return new(AppearanceTransferStatus.Disabled);
        if (cancellationToken.IsCancellationRequested)
            return new(AppearanceTransferStatus.Cancelled);
        return await GuardAsync(cancellationToken, async watchdog =>
        {
            using var request = Request(target, HttpMethod.Delete, $"bundles/{bundleId.Value}");
            using var response = await SendAsync(request, watchdog).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK
                ? AppearanceTransferResult.Success
                : await EditFailureAsync(response, watchdog).ConfigureAwait(false);
        }, failure => failure).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives a bundle and holds its skeleton group (manifest format 2) for
    /// as long as the caller needs it. Default off: with
    /// <see cref="AppearanceReceiveOptions.Default"/> as <paramref name="receive"/> a format-2 manifest is
    /// refused as <see cref="AppearanceTransferStatus.InvalidResponse"/>
    /// before any blob is requested, as today.
    /// <list type="number">
    /// <item>The manifest is read and checked as for a download (exact canonical
    /// bytes whose SHA-256 is <paramref name="bundleId"/>); the declared group
    /// already passed the path rules there.</item>
    /// <item>Before any blob request: a group with the bone deformer is refused
    /// unless <see cref="AppearanceReceiveOptions.AllowDeformer"/>
    /// (<see cref="AppearanceTransferStatus.SkeletonGroupNotAllowed"/>), and the
    /// group's target race must equal <paramref name="puppetRaceCode"/>
    /// (<see cref="AppearanceTransferStatus.SkeletonGroupRaceMismatch"/>).</item>
    /// <item>Each blob, in manifest order: a group member is held through a
    /// write-locked, re-hashed lease within its role's size limit (downloaded
    /// first when missing, once); every other blob is verified in the cache as
    /// for a download and released when this call returns.</item>
    /// <item>The group is assembled from exactly the held bytes, against the
    /// same race, all or nothing (<see cref="AppearanceTransferStatus.SkeletonGroupInvalid"/>).</item>
    /// </list>
    /// The receiver checks what the manifest declares (paths, bytes, race); it
    /// cannot find a dependency the sender's capture left out. On any failure or
    /// cancellation nothing stays held. A format-1 bundle gets no hold: it makes
    /// exactly the requests <see cref="DownloadBundleAsync"/> makes. A returned
    /// hold belongs to the caller and to this client: see <see cref="AppearanceBundleHold"/>.
    /// </summary>
    public async Task<AppearanceBundleHoldResult> HoldBundleAsync(
        ContentHash bundleId, ushort? puppetRaceCode, AppearanceReceiveOptions receive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receive);
        if (Prepare() is not { } target)
            return new(new(AppearanceTransferStatus.Disabled));
        var readOptions = receive.AllowSkeletonGroups ? AppearanceManifestReadOptions.AllowSkeletonGroups : AppearanceManifestReadOptions.FormatOneOnly;
        var read = await ManifestAsync(target, bundleId, cancellationToken, readOptions).ConfigureAwait(false);
        if (read.Manifest is not { } manifest)
            return new(read.Result);
        await Hook("hold:after-manifest").ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
            return new(new(AppearanceTransferStatus.Cancelled), Manifest: manifest);

        // The cheap refusals: no blob is requested for a group this receiver will not use.
        var group = manifest.SkeletonGroup;
        var memberLimits = new Dictionary<ContentHash, long>();
        if (group is not null)
        {
            if (!receive.AllowDeformer && group.Members.FirstOrDefault(member => member.Path.Role == SkeletonResourceRole.Deformer) is { } deformer)
                return new(new(AppearanceTransferStatus.SkeletonGroupNotAllowed), Manifest: manifest, GamePath: deformer.Path.GamePath);
            if (puppetRaceCode != group.RaceCode)
                return new(new(AppearanceTransferStatus.SkeletonGroupRaceMismatch), Manifest: manifest);
            // One hash serving two members is held once, within the larger role limit.
            foreach (var member in group.Members)
            {
                memberLimits[member.Content.Hash] = Math.Max(
                    memberLimits.GetValueOrDefault(member.Content.Hash), SkeletonResourceValidation.MaximumBytes(member.Path.Role));
            }
        }

        var shortLeases = new List<AppearanceCacheLease>();
        var held = new Dictionary<ContentHash, AppearanceCacheHeldLease>();
        var keep = false;
        try
        {
            foreach (var blob in manifest.Blobs)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new(new(AppearanceTransferStatus.Cancelled, Blob: blob.Hash), Manifest: manifest);
                var result = memberLimits.TryGetValue(blob.Hash, out var limit)
                    ? await HoldMemberAsync(target, bundleId, blob, limit, held, shortLeases, cancellationToken).ConfigureAwait(false)
                    : await AcquireShortAsync(target, bundleId, blob, shortLeases, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                    return new(result, Manifest: manifest);
            }
            if (group is null)
                return new(AppearanceTransferResult.Success, Manifest: manifest);

            await Hook("hold:before-assemble").ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return new(new(AppearanceTransferStatus.Cancelled), Manifest: manifest);
            // Exactly the bytes hashed through the held handles, against the same race.
            var candidates = new List<SkeletonGroupCandidate>(group.Members.Count);
            foreach (var member in group.Members)
            {
                if (held[member.Content.Hash].Bytes is not { } bytes)
                {
                    return new(new(AppearanceTransferStatus.SkeletonGroupInvalid), Manifest: manifest,
                        Refusal: SkeletonGroupRefusal.Structure, GamePath: member.Path.GamePath, Problem: SkeletonStructureProblem.TooLarge);
                }
                candidates.Add(new SkeletonGroupCandidate(member.Path.GamePath, bytes));
            }
            var required = manifest.Files
                .Where(file => SkeletonResourcePaths.TryClassify(file.GamePath, out _))
                .Select(file => file.GamePath)
                .ToArray();
            var assembled = SkeletonResourceGroupAssembler.Assemble(group.RaceCode, required, candidates);
            if (assembled.Group is not { } accepted)
            {
                return new(new(AppearanceTransferStatus.SkeletonGroupInvalid), Manifest: manifest,
                    Refusal: assembled.Refusal, GamePath: assembled.GamePath, Problem: assembled.Problem);
            }
            // Defensive: the accepted members are exactly the declared ones.
            if (!accepted.Members.Select(member => (member.Path.GamePath, member.Content))
                    .SequenceEqual(group.Members.Select(member => (member.Path.GamePath, member.Content))))
                return new(new(AppearanceTransferStatus.InvalidResponse), Manifest: manifest);

            // The bytes are no longer needed; the handles keep the files unchanged.
            foreach (var lease in held.Values)
                lease.DropBytes();
            var hold = new AppearanceBundleHold(this, bundleId, accepted, [.. held.Values]);
            if (!Register(hold))
            {
                // This client was disposed meanwhile: nothing outlives it. (Disposed
                // just after this, the client disposes the returned hold too.)
                hold.Dispose();
                return new(new(AppearanceTransferStatus.Cancelled), Manifest: manifest);
            }
            keep = true;
            return new(AppearanceTransferResult.Success, hold, accepted, Manifest: manifest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(new(AppearanceTransferStatus.Cancelled), Manifest: manifest);
        }
        finally
        {
            foreach (var lease in shortLeases)
                lease.Dispose();
            if (!keep)
            {
                foreach (var lease in held.Values)
                    lease.Dispose();
            }
        }
    }

    /// <summary>Test seam: holds this client made that are not yet disposed.</summary>
    internal int LiveHoldCount
    {
        get
        {
            lock (holdGate)
                return holds.Count;
        }
    }

    /// <summary>Disposes every hold this client still has first, then the client.</summary>
    public void Dispose()
    {
        AppearanceBundleHold[] live;
        lock (holdGate)
        {
            disposed = true;
            live = [.. holds];
        }
        foreach (var hold in live)
            hold.Dispose();
        http.Dispose();
        encodeGate.Dispose();
    }

    internal void Unregister(AppearanceBundleHold hold)
    {
        lock (holdGate)
            holds.Remove(hold);
    }

    private bool Register(AppearanceBundleHold hold)
    {
        lock (holdGate)
        {
            if (disposed)
                return false;
            holds.Add(hold);
            return true;
        }
    }

    /// <summary>
    /// A group member: held through a write-locked, re-hashed lease; when it is
    /// not in the cache, downloaded once and then held. Still missing after that
    /// is <see cref="AppearanceTransferStatus.CacheUnavailable"/>, without a retry.
    /// </summary>
    private async Task<AppearanceTransferResult> HoldMemberAsync(
        Target target, ContentHash bundleId, AppearanceBlobRef blob, long maximumBytes,
        Dictionary<ContentHash, AppearanceCacheHeldLease> held, List<AppearanceCacheLease> shortLeases, CancellationToken cancellationToken)
    {
        await Hook("hold:before-member").ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
            return new(AppearanceTransferStatus.Cancelled, Blob: blob.Hash);
        var (state, lease) = await Cache.AcquireHeldAsync(blob, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            if (state == AppearanceCacheState.Unavailable)
                return new(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash);
            var downloaded = await DownloadBlobAsync(target, bundleId, blob, shortLeases, cancellationToken).ConfigureAwait(false);
            if (!downloaded.Succeeded)
                return downloaded;
            await Hook("hold:after-download").ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return new(AppearanceTransferStatus.Cancelled, Blob: blob.Hash);
            (_, lease) = await Cache.AcquireHeldAsync(blob, maximumBytes, cancellationToken).ConfigureAwait(false);
            if (lease is null)
                return new(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash);
        }
        held.Add(blob.Hash, lease);
        return AppearanceTransferResult.Success;
    }

    /// <summary>Any other blob: verified in the cache (downloaded when missing), exactly as a bundle download does.</summary>
    private async Task<AppearanceTransferResult> AcquireShortAsync(
        Target target, ContentHash bundleId, AppearanceBlobRef blob, List<AppearanceCacheLease> shortLeases, CancellationToken cancellationToken)
    {
        var (state, lease) = await Cache.AcquireAsync(blob, cancellationToken).ConfigureAwait(false);
        if (lease is not null)
        {
            shortLeases.Add(lease);
            return AppearanceTransferResult.Success;
        }
        if (state == AppearanceCacheState.Unavailable)
            return new(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash);
        return await DownloadBlobAsync(target, bundleId, blob, shortLeases, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AppearanceManifestRead> ManifestAsync(
        Target target, ContentHash bundleId, CancellationToken cancellationToken,
        AppearanceManifestReadOptions readOptions = AppearanceManifestReadOptions.FormatOneOnly)
    {
        if (!bundleId.IsValid)
            return new(new(AppearanceTransferStatus.InvalidResponse));
        if (cancellationToken.IsCancellationRequested)
            return new(new(AppearanceTransferStatus.Cancelled));
        var (result, bytes) = await GuardAsync<(AppearanceTransferResult, byte[]?)>(cancellationToken, async watchdog =>
        {
            using var request = Request(target, HttpMethod.Get, $"bundles/{bundleId.Value}/manifest");
            using var response = await SendAsync(request, watchdog).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return (await FailureAsync(response, watchdog).ConfigureAwait(false), null);
            var body = await ReadBoundedAsync(response, AppearanceLimits.MaximumManifestBytes, watchdog).ConfigureAwait(false);
            return (AppearanceTransferResult.Success, body);
        }, failure => (failure, null)).ConfigureAwait(false);
        if (!result.Succeeded)
            return new(result);

        // The exact bytes the server keeps: their SHA-256 is the bundle id, and
        // they must already be the canonical form (a semantically equal
        // reserialization would be another bundle and is refused).
        if (ContentHash.Of(bytes!) != bundleId ||
            AppearanceBundleManifest.TryParse(bytes!, readOptions, out _) is not { } manifest ||
            manifest.BundleId != bundleId || !manifest.CanonicalBytes.AsSpan().SequenceEqual(bytes!))
            return new(new(AppearanceTransferStatus.InvalidResponse));
        return new(AppearanceTransferResult.Success, manifest, bytes);
    }

    private static async Task<AppearanceTransferResult> EditFailureAsync(HttpResponseMessage response, Watchdog watchdog) =>
        response.StatusCode == HttpStatusCode.Conflict
            ? new AppearanceTransferResult(AppearanceTransferStatus.StillReferenced)
            : response.StatusCode == HttpStatusCode.BadRequest
                ? new AppearanceTransferResult(AppearanceTransferStatus.InvalidMetadata)
                : await FailureAsync(response, watchdog).ConfigureAwait(false);

    private static byte[] EditBody(AppearanceBundleEdit edit)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (edit.Name is not null)
                writer.WriteString("name", edit.Name);
            if (edit.Description is not null)
                writer.WriteString("description", edit.Description);
            if (edit.Folder is not null)
                writer.WriteString("folder", edit.Folder);
            if (edit.Published is { } published)
                writer.WriteBoolean("published", published);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static AppearanceUsage? ParseUsage(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("usedBytes", out var used) && used.TryGetInt64(out var usedBytes) && usedBytes >= 0 &&
                root.TryGetProperty("quotaBytes", out var quota) && quota.TryGetInt64(out var quotaBytes) && quotaBytes >= 0 &&
                root.TryGetProperty("entries", out var entries) && entries.TryGetInt32(out var entryCount) && entryCount >= 0 &&
                root.TryGetProperty("entryLimit", out var limit) && limit.TryGetInt32(out var entryLimit) && entryLimit >= 0
                ? new AppearanceUsage(usedBytes, quotaBytes, entryCount, entryLimit)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The list, or null if any entry is malformed (fail closed).</summary>
    private static List<AppearanceBundleSummary>? ParseList(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("bundles", out var array) || array.ValueKind != JsonValueKind.Array)
                return null;
            var bundles = new List<AppearanceBundleSummary>();
            foreach (var item in array.EnumerateArray())
            {
                if (Summary(item) is not { } summary)
                    return null;
                bundles.Add(summary);
            }
            return bundles;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>{"bundle": …}</c> answer of one entry, or null if malformed (fail closed).</summary>
    private static AppearanceBundleSummary? ParseBundle(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("bundle", out var item)
                ? Summary(item)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One entry of the list or info answer, or null if any field is malformed.</summary>
    private static AppearanceBundleSummary? Summary(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !ContentHash.TryParse(Text(item, "bundleId"), out var id) ||
            Text(item, "owner") is not { Length: > 0 } owner ||
            !item.TryGetProperty("totalBytes", out var total) || !total.TryGetInt64(out var totalBytes) ||
            !item.TryGetProperty("storedBytes", out var stored) || !stored.TryGetInt64(out var storedBytes) ||
            !DateTimeOffset.TryParse(Text(item, "createdAt"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var created) ||
            !item.TryGetProperty("published", out var published) || published.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            Text(item, "name") is not { } name || Text(item, "description") is not { } description || Text(item, "folder") is not { } folder)
            return null;
        return new(id, owner, totalBytes, storedBytes, created, published.GetBoolean(), name, description, folder);
    }

    /// <summary>
    /// The exact bytes of a verified cache entry, re-hashed: null if the file
    /// is not exactly the blob's size and SHA-256. Never logs the path.
    /// </summary>
    private static async Task<byte[]?> ReadVerifiedAsync(string path, AppearanceBlobRef blob, CancellationToken cancellationToken)
    {
        if (blob.Size is <= 0 or > int.MaxValue)
            return null;
        try
        {
            var bytes = new byte[blob.Size];
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            var filled = 0;
            while (filled < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    return null;
                filled += count;
            }
            if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                return null;
            return ContentHash.Of(bytes) == blob.Hash ? bytes : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<AppearanceTransferResult> DownloadBlobAsync(
        Target target, ContentHash bundleId, AppearanceBlobRef blob, List<AppearanceCacheLease> leases, CancellationToken cancellationToken) =>
        await GuardAsync(cancellationToken, async watchdog =>
        {
            using var request = Request(target, HttpMethod.Get, $"bundles/{bundleId.Value}/blobs/{blob.Hash.Value}");
            using var response = await SendAsync(request, watchdog).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return await FailureAsync(response, watchdog).ConfigureAwait(false) with { Blob = blob.Hash };
            var cap = AppearanceBlobEnvelopeHeader.Length + blob.Size;
            var declared = response.Content.Headers.ContentLength;
            if (declared is { } length && (length > cap || length < AppearanceBlobEnvelopeHeader.Length))
                return new AppearanceTransferResult(AppearanceTransferStatus.InvalidResponse, Blob: blob.Hash);
            await Hook("download").ConfigureAwait(false);
            var decodeHooked = false;
            await using var body = new ResponseBodyStream(
                await response.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false), declared, cap, watchdog,
                async total =>
                {
                    if (!decodeHooked && total >= AppearanceBlobEnvelopeHeader.Length)
                    {
                        decodeHooked = true;
                        await Hook("decode").ConfigureAwait(false);
                    }
                });
            // Local work (decoding to disk, the lock, re-hashing an existing
            // entry) is not network inactivity: the cache gets the caller's
            // token, while each network read stays under the watchdog.
            var (stored, lease) = await Cache.StoreAndAcquireEnvelopeAsync(body, blob, cancellationToken).ConfigureAwait(false);
            if (lease is not null)
                leases.Add(lease);
            return stored switch
            {
                AppearanceCacheStoreStatus.Stored or AppearanceCacheStoreStatus.AlreadyPresent => AppearanceTransferResult.Success,
                AppearanceCacheStoreStatus.Invalid => new AppearanceTransferResult(AppearanceTransferStatus.InvalidResponse, Blob: blob.Hash),
                AppearanceCacheStoreStatus.Full => new AppearanceTransferResult(AppearanceTransferStatus.CacheFull, Blob: blob.Hash),
                AppearanceCacheStoreStatus.OverLimit => new AppearanceTransferResult(AppearanceTransferStatus.CacheOverLimit, Blob: blob.Hash),
                _ => new AppearanceTransferResult(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash),
            };
        }, failure => failure with { Blob = blob.Hash }).ConfigureAwait(false);

    private async Task<AppearanceTransferResult> UploadBlobAsync(
        Target target, AppearanceBlobRef blob, string source, string query, CancellationToken cancellationToken)
    {
        // The envelope and the Brotli scratch file are temporary files in the
        // cache: they are written only within a reservation of their bound.
        AppearanceCacheReserveStatus reserveStatus;
        AppearanceCacheReservation? reservation;
        try
        {
            (reserveStatus, reservation) = await Cache.TryReserveAsync(AppearanceBlobCache.UploadTemporaryBytes(blob.Size), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(AppearanceTransferStatus.Cancelled, Blob: blob.Hash);
        }
        if (reservation is null)
            return new(reserveStatus == AppearanceCacheReserveStatus.OverLimit ? AppearanceTransferStatus.CacheOverLimit : AppearanceTransferStatus.CacheFull, Blob: blob.Hash);
        using var reserved = reservation;
        string envelopePath, scratch;
        try
        {
            envelopePath = Cache.NewTemporaryPath(blob.Hash, "rscb");
            scratch = Cache.NewTemporaryPath(blob.Hash, "br");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash);
        }
        try
        {
            var encoded = await EncodeAsync(blob, source, envelopePath, scratch, cancellationToken).ConfigureAwait(false);
            if (!encoded.Succeeded)
                return encoded;
            return await GuardAsync(cancellationToken, async watchdog =>
            {
                var length = new FileInfo(envelopePath).Length;
                using var request = Request(target, HttpMethod.Put, $"blobs/{blob.Hash.Value}{query}");
                request.Content = new OneShotFileContent(envelopePath, length, watchdog, () => Hook("upload"));
                using var response = await SendAsync(request, watchdog).ConfigureAwait(false);
                return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
                    ? AppearanceTransferResult.Success
                    : await FailureAsync(response, watchdog).ConfigureAwait(false) with { Blob = blob.Hash };
            }, failure => failure with { Blob = blob.Hash }).ConfigureAwait(false);
        }
        finally
        {
            // Temporary files that could not be deleted keep their space
            // reserved until the next startup removes them.
            var envelopeGone = TryDelete(envelopePath);
            var scratchGone = TryDelete(scratch);
            if (!envelopeGone || !scratchGone)
                reservation.TryTake();
        }
    }

    /// <summary>
    /// Encodes the raw source into an envelope file (one encoding at a time,
    /// off the caller's thread); the 5% rule picks raw or Brotli. The source
    /// must be exactly the manifest's blob: reading beyond its size fails, so
    /// the temporary files stay within their reservation.
    /// </summary>
    private async Task<AppearanceTransferResult> EncodeAsync(
        AppearanceBlobRef blob, string source, string envelopePath, string scratch, CancellationToken cancellationToken)
    {
        try
        {
            await encodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Stream raw;
                try
                {
                    raw = TestSourceOpener?.Invoke(source) ??
                        new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return new(AppearanceTransferStatus.LocalContentInvalid, Blob: blob.Hash);
                }
                await using (raw)
                {
                    if (raw.Length != blob.Size)
                        return new(AppearanceTransferStatus.LocalContentInvalid, Blob: blob.Hash);
                    var input = new AppearanceBoundedSourceStream(raw, blob.Size, TestHook is null ? null : () => Hook("encode"));
                    await using var output = new FileStream(envelopePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
                    var header = await Task.Run(() => AppearanceBlobEnvelope.EncodeAsync(input, output, scratch, cancellationToken), cancellationToken).ConfigureAwait(false);
                    if (header.Blob != blob)
                        return new(AppearanceTransferStatus.LocalContentInvalid, Blob: blob.Hash);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return AppearanceTransferResult.Success;
                }
            }
            finally
            {
                encodeGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(AppearanceTransferStatus.Cancelled, Blob: blob.Hash);
        }
        catch (Exception exception) when (exception is InvalidDataException or AppearanceSourceException)
        {
            // Larger than permitted, empty, changed while it was read, or unreadable.
            return new(AppearanceTransferStatus.LocalContentInvalid, Blob: blob.Hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(AppearanceTransferStatus.CacheUnavailable, Blob: blob.Hash);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    private async Task<(AppearanceTransferResult Result, IReadOnlyList<ContentHash> Missing)> CommitAsync(
        Target target, AppearanceBundleManifest manifest, string query, CancellationToken cancellationToken) =>
        await GuardAsync<(AppearanceTransferResult, IReadOnlyList<ContentHash>)>(cancellationToken, async watchdog =>
        {
            using var request = Request(target, HttpMethod.Post, "bundles" + query);
            request.Content = new WatchedBytesContent(manifest.CanonicalBytes, watchdog);
            using var response = await SendAsync(request, watchdog).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                return (AppearanceTransferResult.Success with { AlreadyExisted = response.StatusCode == HttpStatusCode.OK }, Array.Empty<ContentHash>());
            if (response.StatusCode != HttpStatusCode.Conflict)
                return (await FailureAsync(response, watchdog).ConfigureAwait(false), Array.Empty<ContentHash>());
            var body = await ReadBoundedAsync(response, CommitAnswerBytes, watchdog).ConfigureAwait(false);
            var (code, missing) = ParseConflict(body, manifest);
            if (code == "missingBlobs" && missing is { Count: > 0 })
                return (new AppearanceTransferResult(AppearanceTransferStatus.MissingBlobs, code), missing);
            return code == "revisionTargetExists"
                ? (new AppearanceTransferResult(AppearanceTransferStatus.RevisionTargetExists, code), Array.Empty<ContentHash>())
                : (new AppearanceTransferResult(AppearanceTransferStatus.InvalidResponse), Array.Empty<ContentHash>());
        }, failure => (failure, Array.Empty<ContentHash>())).ConfigureAwait(false);

    /// <summary>The conflict code and, for missing blobs, the listed hashes (each must be a distinct blob of this manifest).</summary>
    private static (string? Code, List<ContentHash>? Missing) ParseConflict(byte[] body, AppearanceBundleManifest manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String)
                return (null, null);
            var code = AppearanceTransferResult.SafeCode(codeElement.GetString());
            if (code != "missingBlobs")
                return (code, null);
            if (!root.TryGetProperty("missing", out var list) || list.ValueKind != JsonValueKind.Array)
                return (code, null);
            var known = manifest.Blobs.Select(blob => blob.Hash).ToHashSet();
            var missing = new List<ContentHash>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !ContentHash.TryParse(item.GetString(), out var hash) ||
                    !known.Contains(hash) || missing.Contains(hash))
                    return (code, null);
                missing.Add(hash);
            }
            return (code, missing);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Maps a non-success answer to a typed result (only the server's code is kept, if it is a plain identifier).</summary>
    private static async Task<AppearanceTransferResult> FailureAsync(HttpResponseMessage response, Watchdog watchdog)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
            return new(AppearanceTransferStatus.Redirected);
        var code = await ErrorCodeAsync(response, watchdog).ConfigureAwait(false);
        return status switch
        {
            401 => new(AppearanceTransferStatus.InvalidCredentials),
            403 => new(AppearanceTransferStatus.Forbidden, code),
            404 when code == "replacedNotFound" => new(AppearanceTransferStatus.ReplacedNotFound, code),
            404 => new(AppearanceTransferStatus.NotFound, code),
            409 when code == "revisionTargetExists" => new(AppearanceTransferStatus.RevisionTargetExists, code),
            400 or 409 or 411 or 413 => new(AppearanceTransferStatus.Refused, code),
            503 => new(AppearanceTransferStatus.ServerBusy, code),
            507 when code == "entryLimit" => new(AppearanceTransferStatus.EntryLimit, code),
            507 => new(AppearanceTransferStatus.StorageQuota, code),
            500 when code == "blobCorrupt" => new(AppearanceTransferStatus.BlobCorrupt, code),
            500 when code == "blobUnavailable" => new(AppearanceTransferStatus.BlobUnavailable, code),
            >= 500 => new(AppearanceTransferStatus.ServerError, code),
            _ => new(AppearanceTransferStatus.InvalidResponse, code),
        };
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, Watchdog watchdog)
    {
        try
        {
            var body = await ReadBoundedAsync(response, ErrorBodyBytes, watchdog).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? AppearanceTransferResult.SafeCode(code.GetString())
                : null;
        }
        catch (Exception exception) when (exception is JsonException or AppearanceTransferException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>The whole body, at most <paramref name="cap"/> bytes and exactly its declared length.</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int cap, Watchdog watchdog)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared > cap)
            throw new AppearanceTransferException(AppearanceTransferStatus.InvalidResponse);
        await using var body = new ResponseBodyStream(
            await response.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false), declared, cap, watchdog, null);
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, 16 * 1024, watchdog.Token).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Watchdog watchdog)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token).ConfigureAwait(false);
        watchdog.Touch();
        return response;
    }

    /// <summary>
    /// Runs one request under its own inactivity watchdog and maps every
    /// transport failure to a typed result (no exception text is kept).
    /// </summary>
    private async Task<T> GuardAsync<T>(CancellationToken cancellationToken, Func<Watchdog, Task<T>> body, Func<AppearanceTransferResult, T> failure)
    {
        using var watchdog = new Watchdog(cancellationToken, options.InactivityTimeout);
        try
        {
            return await body(watchdog).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransferFailure(exception))
        {
            return failure(Classify(exception, cancellationToken, watchdog));
        }
    }

    private static bool IsTransferFailure(Exception exception) =>
        exception is OperationCanceledException or HttpRequestException or IOException or AppearanceTransferException;

    private static AppearanceTransferResult Classify(Exception exception, CancellationToken cancellationToken, Watchdog watchdog)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(AppearanceTransferStatus.Cancelled);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AppearanceTransferException typed)
                return new(typed.Status);
        }
        if (watchdog.Expired)
            return new(AppearanceTransferStatus.TimedOut);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // E.g. the handler's connect timeout.
            if (current is TimeoutException)
                return new(AppearanceTransferStatus.TimedOut);
        }
        return new(AppearanceTransferStatus.ConnectionInterrupted);
    }

    /// <summary>
    /// The derived base address and the key, only when the user has the
    /// connection enabled and every check passes; otherwise nothing is sent.
    /// </summary>
    private Target? Prepare()
    {
        var current = settings();
        if (current is null || !current.Enabled || AppearanceEndpoint.TryDerive(current.Endpoint) is not { } address ||
            !current.HasWellFormedAccessKey)
            return null;
        return new Target(address, current.AccessKey);
    }

    private static HttpRequestMessage Request(Target target, HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method, new Uri(target.Address, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Key);
        return request;
    }

    private Task Hook(string point) => TestHook?.Invoke(point) ?? Task.CompletedTask;

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

    private sealed record Target(Uri Address, string Key)
    {
        public override string ToString() => Address.ToString();
    }

    /// <summary>
    /// Cancels its token after <see cref="AppearanceTransferOptions.InactivityTimeout"/>
    /// without progress (or when the caller cancels).
    /// </summary>
    private sealed class Watchdog : IDisposable
    {
        private readonly CancellationToken caller;
        private readonly CancellationTokenSource source;
        private readonly TimeSpan inactivity;

        public Watchdog(CancellationToken caller, TimeSpan inactivity)
        {
            this.caller = caller;
            this.inactivity = inactivity;
            source = CancellationTokenSource.CreateLinkedTokenSource(caller);
            source.CancelAfter(inactivity);
        }

        public CancellationToken Token => source.Token;

        public bool Expired => source.IsCancellationRequested && !caller.IsCancellationRequested;

        public void Touch()
        {
            try
            {
                source.CancelAfter(inactivity);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => source.Dispose();
    }

    /// <summary>
    /// The request body of one blob upload: streamed from the envelope file
    /// with progress feeding the watchdog. It can be sent only once; a second
    /// attempt (an implicit resend) fails instead of replaying the upload.
    /// </summary>
    private sealed class OneShotFileContent : HttpContent
    {
        private readonly string path;
        private readonly long length;
        private readonly Watchdog watchdog;
        private readonly Func<Task> afterFirstChunk;
        private int sent;

        public OneShotFileContent(string path, long length, Watchdog watchdog, Func<Task> afterFirstChunk)
        {
            this.path = path;
            this.length = length;
            this.watchdog = watchdog;
            this.afterFirstChunk = afterFirstChunk;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            Headers.ContentLength = length;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref sent, 1) != 0)
                throw new AppearanceTransferException(AppearanceTransferStatus.ConnectionInterrupted);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
            try
            {
                FileStream file;
                try
                {
                    file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new AppearanceTransferException(AppearanceTransferStatus.CacheUnavailable);
                }
                await using (file)
                {
                    var first = true;
                    long total = 0;
                    int read;
                    while ((read = await file.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > length)
                            throw new AppearanceTransferException(AppearanceTransferStatus.CacheUnavailable);
                        await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        watchdog.Touch();
                        if (first)
                        {
                            first = false;
                            await afterFirstChunk().ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    if (total != length)
                        throw new AppearanceTransferException(AppearanceTransferStatus.CacheUnavailable);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = this.length;
            return true;
        }
    }

    /// <summary>A small request body (the manifest) written in chunks that feed the watchdog.</summary>
    private sealed class WatchedBytesContent : HttpContent
    {
        private const int ChunkBytes = 16 * 1024;
        private readonly byte[] bytes;
        private readonly Watchdog watchdog;

        public WatchedBytesContent(byte[] bytes, Watchdog watchdog)
        {
            this.bytes = bytes;
            this.watchdog = watchdog;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
            Headers.ContentLength = bytes.Length;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < bytes.Length; offset += ChunkBytes)
            {
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(ChunkBytes, bytes.Length - offset)), cancellationToken).ConfigureAwait(false);
                watchdog.Touch();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }

    /// <summary>
    /// A response body read with a hard cap and the declared length enforced
    /// exactly (short, long or absent lengths), with progress feeding the
    /// watchdog. Network failures surface as <see cref="AppearanceTransferException"/>
    /// (not <see cref="IOException"/>), so the cache never mistakes them for its own.
    /// </summary>
    private sealed class ResponseBodyStream : Stream
    {
        private readonly Stream inner;
        private readonly long? declared;
        private readonly long cap;
        private readonly Watchdog watchdog;
        private readonly Func<long, Task>? beforeRead;
        private long total;

        public ResponseBodyStream(Stream inner, long? declared, long cap, Watchdog watchdog, Func<long, Task>? beforeRead)
        {
            this.inner = inner;
            this.declared = declared;
            this.cap = cap;
            this.watchdog = watchdog;
            this.beforeRead = beforeRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => total; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (beforeRead is not null)
                await beforeRead(total).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Never ask for more than one byte past the cap or the declared length.
            var limit = Math.Min(cap, declared ?? cap) - total + 1;
            if (limit < buffer.Length)
                buffer = buffer[..(int)Math.Max(limit, 1)];
            int read;
            try
            {
                read = await inner.ReadAsync(buffer, watchdog.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new AppearanceTransferException(watchdog.Expired
                    ? AppearanceTransferStatus.TimedOut
                    : AppearanceTransferStatus.ConnectionInterrupted);
            }
            total += read;
            watchdog.Touch();
            if (total > cap || (declared is { } more && total > more))
                throw new AppearanceTransferException(AppearanceTransferStatus.InvalidResponse);
            if (read == 0 && declared is { } exact && total != exact)
                throw new AppearanceTransferException(AppearanceTransferStatus.InvalidResponse);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The raw source file during encoding: a read error is reported as the
    /// source's problem (not the cache's). Test seam: an optional hook awaited
    /// before the first read (cancellation during compression).
    /// </summary>
}

/// <summary>
/// The raw source of one blob being uploaded. Reading beyond
/// <paramref name="size"/> bytes from its start (the file grew while it was
/// encoded) fails with <see cref="AppearanceSourceException"/> before the extra
/// bytes are returned, so they never reach the envelope or the Brotli scratch
/// file and the upload's temporary files stay within their reservation.
/// </summary>
internal sealed class AppearanceBoundedSourceStream(Stream inner, long size, Func<Task>? hook) : Stream
{
    private bool fired;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!fired && hook is not null)
        {
            fired = true;
            await hook().ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var remaining = size - inner.Position;
            if (remaining < 0)
                throw new AppearanceSourceException();
            // Within the declared size: never more than what is left of it.
            if (remaining > 0)
                return await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken).ConfigureAwait(false);
            // At the declared end: one probe byte. Any byte means the source grew;
            // it is never returned, so it never reaches an output.
            if (buffer.Length == 0)
                return 0;
            if (await inner.ReadAsync(buffer[..1], cancellationToken).ConfigureAwait(false) != 0)
                throw new AppearanceSourceException();
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AppearanceSourceException();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>The local source file of an upload could not be read.</summary>
internal sealed class AppearanceSourceException() : Exception("The source file could not be read.");

/// <summary>A typed transfer failure raised inside a transfer; carries no text, so it cannot expose the key.</summary>
internal sealed class AppearanceTransferException(AppearanceTransferStatus status) : Exception("Appearance transfer failed.")
{
    public AppearanceTransferStatus Status { get; } = status;
}
