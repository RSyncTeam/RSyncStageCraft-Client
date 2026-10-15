using System.Text;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Appearance;

/// <summary>A saved Glamourer design the owner can capture (state only).</summary>
public sealed record AppearanceDesignEntry(Guid Id, string Name);

public enum AppearanceReadStatus
{
    Ok,
    /// <summary>Penumbra or Glamourer is missing, too old, or its API is not available.</summary>
    Unavailable,
    /// <summary>The local player could not be found (for example during a zone change).</summary>
    PlayerNotFound,
    /// <summary>Another plugin has locked the player's Glamourer state; it cannot be read.</summary>
    StateLocked,
    /// <summary>The saved design no longer exists.</summary>
    DesignNotFound,
    /// <summary>Glamourer returned data that is not canonical Base64 within the size limit.</summary>
    InvalidData,
    /// <summary>The read failed.</summary>
    Failed,
}

public sealed record AppearanceReadResult<T>(AppearanceReadStatus Status, T? Value = default)
{
    public bool Ok => Status == AppearanceReadStatus.Ok && Value is not null;
}

/// <summary>
/// Read-only access to the owner's own appearance. Implementations only
/// read: they never write to the player, change a collection, redraw, or apply
/// a design. Each Penumbra and Glamourer call is
/// made on the framework thread through the game-thread dispatcher.
/// </summary>
public interface IAppearanceCaptureReader
{
    /// <summary>The local player's Glamourer state and Penumbra resource list.</summary>
    Task<AppearanceReadResult<AppearanceCaptureRead>> ReadCurrentCharacterAsync(CancellationToken cancellationToken);

    /// <summary>The saved Glamourer designs (id and name).</summary>
    Task<AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>> ListDesignsAsync(CancellationToken cancellationToken);

    /// <summary>One saved design's state, without applying it to anything.</summary>
    Task<AppearanceReadResult<AppearanceCaptureRead>> ReadDesignAsync(AppearanceDesignEntry design, CancellationToken cancellationToken);
}

/// <summary>Capture is unavailable (no verified adapter is installed).</summary>
public sealed class UnavailableAppearanceCaptureReader : IAppearanceCaptureReader
{
    public static UnavailableAppearanceCaptureReader Instance { get; } = new();

    public Task<AppearanceReadResult<AppearanceCaptureRead>> ReadCurrentCharacterAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.Unavailable));

    public Task<AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>> ListDesignsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>(AppearanceReadStatus.Unavailable));

    public Task<AppearanceReadResult<AppearanceCaptureRead>> ReadDesignAsync(AppearanceDesignEntry design, CancellationToken cancellationToken) =>
        Task.FromResult(new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.Unavailable));
}

/// <summary>The outcome of a capture: the plan, or why reading failed.</summary>
public sealed record AppearanceCaptureOutcome(AppearanceReadStatus Status, AppearanceCapturePlan? Plan = null);

/// <summary>Where an upload goes: a new entry with a name, or an explicit revision of one of the owner's entries.</summary>
public sealed record AppearanceUploadTarget(string? Name, ContentHash? Replaces)
{
    public static AppearanceUploadTarget NewEntry(string name) => new(name, null);

    public static AppearanceUploadTarget Revision(ContentHash replaces) => new(null, replaces);
}

/// <summary>The outcome of an upload: the transfer result and, on success, the new bundle id.</summary>
/// <param name="AlreadyExisted">The same bundle was already in the owner's catalogue: nothing new was created, and its name and published state were left unchanged.</param>
/// <param name="Revision">The upload revised an existing entry, which keeps that entry's name and published state.</param>
public sealed record AppearanceUploadOutcome(
    AppearanceTransferResult Result, ContentHash? BundleId = null, bool NameApplied = true, bool AlreadyExisted = false, bool Revision = false);

/// <summary>
/// The owner's capture-and-upload workflow. Capturing only reads local
/// state and plans the bundle; nothing about it (manifest, game paths,
/// hashes, Glamourer state or file contents) is sent until
/// <see cref="UploadAsync"/> is called after the owner confirms the summary.
/// </summary>
public sealed class AppearanceAuthoring
{
    /// <summary>The longest entry name the server accepts, in UTF-8 bytes.</summary>
    public const int MaximumNameBytes = 64;

    private readonly IAppearanceCaptureReader reader;
    private readonly AppearanceTransferClient client;
    private readonly Func<RscUserId?> owner;
    private readonly string workingDirectory;

    public AppearanceAuthoring(IAppearanceCaptureReader reader, AppearanceTransferClient client, Func<RscUserId?> owner, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        this.reader = reader;
        this.client = client;
        this.owner = owner;
        this.workingDirectory = workingDirectory;
    }

    public AppearanceTransferClient Client => client;

    /// <summary>Reads the current character (read-only) and plans the bundle locally.</summary>
    public async Task<AppearanceCaptureOutcome> CaptureCurrentCharacterAsync(CancellationToken cancellationToken) =>
        await PlanAsync(await reader.ReadCurrentCharacterAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public Task<AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>> ListDesignsAsync(CancellationToken cancellationToken) =>
        reader.ListDesignsAsync(cancellationToken);

    /// <summary>Reads a saved design's state (not applied to anything) and plans a state-only bundle.</summary>
    public async Task<AppearanceCaptureOutcome> CaptureDesignAsync(AppearanceDesignEntry design, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(design);
        var read = await reader.ReadDesignAsync(design, cancellationToken).ConfigureAwait(false);
        // A saved design never carries Penumbra files, whatever a reader returns.
        if (read.Ok)
            read = read with { Value = Normalize(read.Value!, AppearanceCaptureSource.SavedDesign, design.Name, design.Id) };
        return await PlanAsync(read, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads a confirmed capture: each missing blob in full (re-read and
    /// re-hashed while it is encoded; any change fails the upload before the
    /// bundle is committed), then the manifest. A new entry is then named; a
    /// revision keeps the old entry's name and publish state (server rule).
    /// </summary>
    public async Task<AppearanceUploadOutcome> UploadAsync(
        AppearanceCapturePlan plan, AppearanceUploadTarget target, CancellationToken cancellationToken, Action<AppearanceUploadPhase>? phase = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);
        if (!plan.CanUpload)
            return new(new(AppearanceTransferStatus.LocalContentInvalid));
        if (target.Replaces is null && !IsValidName(target.Name))
            return new(new(AppearanceTransferStatus.InvalidMetadata));
        var manifest = plan.Manifest!;

        // One logical snapshot: read again (read-only) right before anything is
        // sent and compare. A different player object, mapping, Glamourer data
        // or file content invalidates the summary; nothing is uploaded.
        if (await StillSameAsync(plan, cancellationToken).ConfigureAwait(false) is { } changed)
            return new(new(changed));
        var uploaded = await client.UploadBundleAsync(manifest, plan.Sources, target.Replaces, cancellationToken, phase).ConfigureAwait(false);
        if (!uploaded.Succeeded)
            return new(uploaded);
        if (target.Replaces is not null)
            return new(uploaded, manifest.BundleId, Revision: true);
        // An identical capture is the existing entry: it keeps its name and
        // published state, and the owner is told nothing new was created. Only
        // an unnamed one (a commit whose answer was lost before it was named)
        // is given the chosen name.
        if (uploaded.AlreadyExisted)
        {
            var list = await client.ListBundlesAsync(CancellationToken.None).ConfigureAwait(false);
            var existing = list.Bundles.FirstOrDefault(bundle => bundle.BundleId == manifest.BundleId);
            var renamed = false;
            if (existing is { Name.Length: 0 } && owner() is { } self && existing.Owner == self.Value)
                renamed = (await client.UpdateBundleAsync(manifest.BundleId, new AppearanceBundleEdit(Name: target.Name!.Trim()), CancellationToken.None)
                    .ConfigureAwait(false)).Succeeded;
            return new(uploaded, manifest.BundleId, NameApplied: renamed, AlreadyExisted: true);
        }
        // The name is mutable metadata outside the bundle id. The new entry is
        // unpublished; if naming fails it stays unnamed and unpublished.
        var named = await client.UpdateBundleAsync(manifest.BundleId, new AppearanceBundleEdit(Name: target.Name!.Trim()), CancellationToken.None)
            .ConfigureAwait(false);
        return new(uploaded, manifest.BundleId, named.Succeeded);
    }

    /// <summary>Null when the snapshot is unchanged; otherwise why nothing may be sent.</summary>
    private async Task<AppearanceTransferStatus?> StillSameAsync(AppearanceCapturePlan plan, CancellationToken cancellationToken)
    {
        var read = plan.Source == AppearanceCaptureSource.SavedDesign
            ? plan.Read.DesignId is { } id
                ? await reader.ReadDesignAsync(new AppearanceDesignEntry(id, plan.Read.DesignName ?? string.Empty), cancellationToken).ConfigureAwait(false)
                : new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.Failed)
            : await reader.ReadCurrentCharacterAsync(cancellationToken).ConfigureAwait(false);
        if (!read.Ok)
            return AppearanceTransferStatus.CaptureChanged;
        var again = Normalize(read.Value!, plan.Source, plan.Read.DesignName, plan.Read.DesignId);
        // Hashing runs off the framework thread (the read may complete there).
        var fingerprint = await Task.Run(() => AppearanceCapturePlanner.Fingerprint(again), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(fingerprint, plan.Fingerprint, StringComparison.Ordinal) || owner() is not { IsValid: true } self)
            return AppearanceTransferStatus.CaptureChanged;
        // The same mapping can still point at changed files: plan again (hashing
        // every file) and require the identical bundle.
        using var fresh = await Task.Run(() => AppearanceCapturePlanner.PlanAsync(self, again, workingDirectory, cancellationToken, ReserveWorkingFileAsync), cancellationToken)
            .ConfigureAwait(false);
        // No room for the re-check's working file is not a changed appearance.
        if (fresh.Refusal == AppearanceCaptureRefusal.CacheFull)
            return AppearanceTransferStatus.CacheFull;
        return fresh.Manifest is { } manifest && manifest.BundleId == plan.Manifest!.BundleId ? null : AppearanceTransferStatus.CaptureChanged;
    }

    /// <summary>The capture's working file is a temporary file of the cache: it counts toward the cache limit.</summary>
    private async Task<IDisposable?> ReserveWorkingFileAsync(long bytes, CancellationToken cancellationToken) =>
        await client.Cache.ReserveAsync(bytes, cancellationToken).ConfigureAwait(false);

    /// <summary>A saved design never carries Penumbra files, whatever a reader returned.</summary>
    private static AppearanceCaptureRead Normalize(AppearanceCaptureRead read, AppearanceCaptureSource source, string? designName, Guid? designId) =>
        source == AppearanceCaptureSource.SavedDesign
            ? read with { Source = AppearanceCaptureSource.SavedDesign, Resources = [], DesignName = designName, DesignId = designId, PenumbraModDirectory = null, Player = null }
            : read;

    public static bool IsValidName(string? name) =>
        name is not null && name.Trim().Length > 0 && Encoding.UTF8.GetByteCount(name.Trim()) <= MaximumNameBytes &&
        !name.Any(char.IsControl);

    private async Task<AppearanceCaptureOutcome> PlanAsync(AppearanceReadResult<AppearanceCaptureRead> read, CancellationToken cancellationToken)
    {
        if (!read.Ok)
            return new(read.Status == AppearanceReadStatus.Ok ? AppearanceReadStatus.Failed : read.Status);
        if (owner() is not { IsValid: true } self)
            return new(AppearanceReadStatus.Unavailable);
        // Hashing runs off the framework thread.
        var plan = await Task.Run(() => AppearanceCapturePlanner.PlanAsync(self, read.Value!, workingDirectory, cancellationToken, ReserveWorkingFileAsync), cancellationToken)
            .ConfigureAwait(false);
        return new(AppearanceReadStatus.Ok, plan);
    }
}
