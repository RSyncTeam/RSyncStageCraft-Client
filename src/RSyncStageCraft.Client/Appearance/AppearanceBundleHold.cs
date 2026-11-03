namespace RSyncStageCraft.Core.Appearance;

/// <summary>
/// What a receiver may accept beyond manifest format 1. Every option is off
/// by default, and nothing in the plugin sets one yet (receiving a skeleton
/// group is not wired to any actor).
/// </summary>
/// <param name="AllowSkeletonGroups">
/// Read a format-2 manifest and hold its skeleton group. Off: a format-2
/// manifest is refused as an invalid response before any blob is requested,
/// exactly as before format 2 existed.
/// </param>
/// <param name="AllowDeformer">
/// Accept a group that holds the bone deformer (<c>human.pbd</c>). Off: such
/// a group is refused whole before any blob is requested.
/// </param>
public sealed record AppearanceReceiveOptions(bool AllowSkeletonGroups = false, bool AllowDeformer = false)
{
    /// <summary>Both options off: format 1 only.</summary>
    public static AppearanceReceiveOptions Default { get; } = new();
}

/// <summary>
/// The outcome of <see cref="AppearanceTransferClient.HoldBundleAsync"/>.
/// <list type="bullet">
/// <item>Format 2 accepted: <see cref="Hold"/> and <see cref="Group"/> are set.</item>
/// <item>Format 1: no hold and no group; every blob was verified in the cache
/// exactly as <see cref="AppearanceTransferClient.DownloadBundleAsync"/> does.</item>
/// <item>A refused group: <see cref="AppearanceTransferStatus.SkeletonGroupInvalid"/> with
/// <see cref="Refusal"/>, and <see cref="GamePath"/> and <see cref="Problem"/> when known.</item>
/// </list>
/// Only game paths are ever carried, never a local path.
/// </summary>
public sealed record AppearanceBundleHoldResult(
    AppearanceTransferResult Result,
    AppearanceBundleHold? Hold = null,
    SkeletonResourceGroup? Group = null,
    SkeletonGroupRefusal? Refusal = null,
    string? GamePath = null,
    SkeletonStructureProblem? Problem = null,
    AppearanceBundleManifest? Manifest = null);

/// <summary>
/// The skeleton-group members of one received bundle, each held through a
/// write-locked, re-hashed cache lease (<see cref="AppearanceBlobCache.AcquireHeldAsync"/>),
/// after the group was validated and assembled from exactly those bytes.
/// <list type="bullet">
/// <item><b>Retention.</b> Keep it for as long as anything may read the members'
/// cache files, across redraws; a redraw needs no new lease while
/// <see cref="IsIntact"/> is true. Only the members are held; the bundle's
/// other blobs are not.</item>
/// <item><b>Release.</b> Dispose it last: after whatever names its files has
/// stopped naming them. Dispose is idempotent and never throws; for each
/// member it closes the handle, then releases the entry.</item>
/// <item><b>Replacement</b> is make before break: acquire and validate the new
/// hold first, and dispose the old one only once the new one is in use. If the
/// new hold fails, keep the old one unchanged. While both exist, both count
/// against the cache limit, which is still enforced: a new hold that does not
/// fit fails whole (<see cref="AppearanceTransferStatus.CacheFull"/>) and the old
/// one stays. The cache never releases the old hold to make room.</item>
/// <item><b>Ownership.</b> A hold never outlives the client that made it:
/// disposing the client disposes every hold it still has, including one it
/// returned a moment before (check <see cref="IsIntact"/>).</item>
/// </list>
/// <para><b>Capacity.</b> One group has at most
/// <see cref="SkeletonResourceGroupAssembler.MaximumMembers"/> members of at most
/// 4 MiB each, one open handle per distinct member. In steady state eight
/// actors therefore hold at most 352 MiB and 88 handles. These are not limits
/// during a replacement: each actor being replaced can briefly hold a second
/// group (up to another 44 MiB and 11 handles) until its old hold is disposed.</para>
/// </summary>
public sealed class AppearanceBundleHold : IDisposable
{
    private readonly AppearanceTransferClient owner;
    private AppearanceCacheHeldLease[]? leases;

    internal AppearanceBundleHold(AppearanceTransferClient owner, ContentHash bundleId, SkeletonResourceGroup group, AppearanceCacheHeldLease[] leases)
    {
        this.owner = owner;
        this.leases = leases;
        BundleId = bundleId;
        Group = group;
    }

    public ContentHash BundleId { get; }

    /// <summary>The accepted group: its target race and members (game paths and blobs).</summary>
    public SkeletonResourceGroup Group { get; }

    /// <summary>
    /// True until the hold is disposed: every member is still held, and (where
    /// sharing modes are enforced, on Windows) unchanged.
    /// </summary>
    public bool IsIntact => Volatile.Read(ref leases) is not null;

    /// <summary>Test seam: the held leases (one per distinct member blob); 0 once disposed.</summary>
    internal int LeaseCount => Volatile.Read(ref leases)?.Length ?? 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref leases, null) is not { } held)
            return;
        foreach (var lease in held)
            lease.Dispose();
        owner.Unregister(this);
    }

    public override string ToString() => $"Held skeleton group of bundle {BundleId.Value}";
}
