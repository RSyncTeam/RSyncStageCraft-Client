using System.Buffers.Text;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Appearance;

/// <summary>
/// The local player as one exact game object: its current object-table index,
/// address and game object id. Never hardcoded; read from the public
/// Dalamud object APIs on the framework thread.
/// </summary>
public readonly record struct AppearancePlayer(ushort ObjectIndex, nint Address, ulong GameObjectId);

/// <summary>Finds the local player and checks it is still that exact object (framework thread only).</summary>
public interface IAppearancePlayerLocator
{
    /// <summary>The local player now, or null.</summary>
    AppearancePlayer? LocalPlayer();

    /// <summary>
    /// True only if <paramref name="player"/> is still the local player and its
    /// index still resolves to that exact object.
    /// </summary>
    bool IsStill(AppearancePlayer player);

    /// <summary>
    /// The race, clan and gender bytes of <paramref name="player"/>'s customize
    /// data, only while it is still that exact object; otherwise null. Read
    /// only when skeleton groups may be captured.
    /// </summary>
    AppearanceCharacterRace? CharacterRace(AppearancePlayer player) => null;
}

public enum AppearanceApiState
{
    Ready,
    /// <summary>The plugin or one of the approved subscribers is not available.</summary>
    Unavailable,
    /// <summary>The installed API version fails the existing version gate.</summary>
    Incompatible,
}

/// <summary>
/// The approved read-only IPC calls, exactly as the pinned subscribers expose
/// them. Implemented by the Dalamud adapter; called on the framework thread
/// only. Nothing here applies, sets, reverts, redraws, or touches temporary
/// mods or collections, and Penumbra meta is never read.
/// </summary>
public interface IAppearanceIpc
{
    /// <summary>Penumbra's approved subscribers are valid and its API passes the version gate.</summary>
    AppearanceApiState PenumbraState();

    /// <summary>Glamourer's approved subscribers are valid and its API passes the version gate.</summary>
    AppearanceApiState GlamourerState();

    /// <summary><c>Penumbra.GetModDirectory</c>: the Penumbra mod root (used only to constrain captured files).</summary>
    string? GetModDirectory();

    /// <summary><c>Penumbra.GetGameObjectResourcePaths.V5</c>: actual path to game paths, one dictionary per index.</summary>
    Dictionary<string, HashSet<string>>?[]? GetGameObjectResourcePaths(ushort[] gameObjectIndices);

    /// <summary><c>Glamourer.GetStateBase64</c> with key 0: the result code (GlamourerApiEc) and the state.</summary>
    (int Code, string? State) GetStateBase64(int objectIndex);

    /// <summary><c>Glamourer.GetDesignList.V2</c>: saved design ids and display names.</summary>
    Dictionary<Guid, string>? GetDesignList();

    /// <summary><c>Glamourer.GetDesignBase64</c>: one saved design's data, or null.</summary>
    string? GetDesignBase64(Guid designId);
}

/// <summary>
/// The read-only capture reader over the approved IPC calls.
/// <list type="bullet">
/// <item>Every call runs on the framework thread through the game-thread
/// dispatcher; the returned data is copied there into an RSC-owned snapshot.
/// Path checks, hashing, encoding and upload happen later, off that thread.</item>
/// <item>The local player is looked up, never assumed to be index 0, and is
/// re-checked right before (and after) every call that uses it. If it is
/// missing or changes, the capture fails; there is no fallback to another
/// character.</item>
/// <item>The version gates and subscriber checks run before any capture; an
/// unavailable or incompatible API is a typed result, never a fallback call.</item>
/// <item>Glamourer data is opaque: it must be canonical Base64 within the
/// size limit and is never decoded further or rewritten.</item>
/// <item>No exception or local path leaves this class; failures are statuses.</item>
/// </list>
/// Package metadata cannot prove the plugins' implementations have no side
/// effects (for example, Glamourer may begin tracking the player internally
/// when its state is read); the in-game checklist confirms no visible change.
/// </summary>
public sealed class GameAppearanceCaptureReader : IAppearanceCaptureReader
{
    /// <summary>The largest Glamourer state or design text accepted (the manifest size limit).</summary>
    public const int MaximumGlamourerTextLength = AppearanceLimits.MaximumManifestBytes;

    /// <summary>GlamourerApiEc.Success; every other code is a failure.</summary>
    public const int GlamourerSuccess = 0;
    public const int GlamourerActorNotFound = 2;
    public const int GlamourerInvalidKey = 6;

    private readonly IGameThreadDispatcher dispatcher;
    private readonly IAppearancePlayerLocator locator;
    private readonly IAppearanceIpc ipc;
    private readonly Func<bool> unloading;
    private readonly bool readCharacterRace;

    /// <param name="unloading">
    /// True once the plugin is unloading: the dispatcher may then run work
    /// inline on any thread, so no IPC call is made at all.
    /// </param>
    /// <param name="readCharacterRace">
    /// Also read the player's race, clan and gender (skeleton groups only).
    /// False: the read is exactly as before and the race is never read.
    /// </param>
    public GameAppearanceCaptureReader(
        IGameThreadDispatcher dispatcher, IAppearancePlayerLocator locator, IAppearanceIpc ipc, Func<bool>? unloading = null, bool readCharacterRace = false)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(ipc);
        this.dispatcher = dispatcher;
        this.locator = locator;
        this.ipc = ipc;
        this.unloading = unloading ?? (() => false);
        this.readCharacterRace = readCharacterRace;
    }

    public Task<AppearanceReadResult<AppearanceCaptureRead>> ReadCurrentCharacterAsync(CancellationToken cancellationToken) =>
        OnFrameworkAsync<AppearanceCaptureRead>(() =>
        {
            if (ipc.PenumbraState() != AppearanceApiState.Ready || ipc.GlamourerState() != AppearanceApiState.Ready)
                return new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.Unavailable);
            if (locator.LocalPlayer() is not { } player)
                return new(AppearanceReadStatus.PlayerNotFound);

            if (!locator.IsStill(player))
                return new(AppearanceReadStatus.PlayerNotFound);
            var modDirectory = ipc.GetModDirectory();

            if (!locator.IsStill(player))
                return new(AppearanceReadStatus.PlayerNotFound);
            var paths = ipc.GetGameObjectResourcePaths([player.ObjectIndex]);

            if (!locator.IsStill(player))
                return new(AppearanceReadStatus.PlayerNotFound);
            // The race the skeleton paths were resolved for, read in the same
            // framework callback (skeleton groups only). Missing is not a read
            // failure: only a group would be refused.
            AppearanceCharacterRace? race = null;
            if (readCharacterRace)
            {
                race = locator.CharacterRace(player);
                if (!locator.IsStill(player))
                    return new(AppearanceReadStatus.PlayerNotFound);
            }
            var (code, state) = ipc.GetStateBase64(player.ObjectIndex);
            if (!locator.IsStill(player))
                return new(AppearanceReadStatus.PlayerNotFound);

            if (code != GlamourerSuccess)
                return new(GlamourerFailure(code));
            if (state is null || state.Length > MaximumGlamourerTextLength)
                return new(AppearanceReadStatus.InvalidData);
            // Exactly one dictionary, for the one index asked; anything else fails closed.
            if (paths is not { Length: 1 } || paths[0] is not { } map)
                return new(AppearanceReadStatus.Failed);
            var resources = new List<AppearanceResourceEntry>(map.Count);
            foreach (var (actual, gamePaths) in map)
            {
                if (actual is null || gamePaths is null)
                    return new(AppearanceReadStatus.Failed);
                resources.Add(new AppearanceResourceEntry(actual, gamePaths.ToArray()));
            }
            return new(AppearanceReadStatus.Ok, new AppearanceCaptureRead(
                AppearanceCaptureSource.CurrentCharacter, state, resources, null, modDirectory, player, CharacterRace: race));
        }, ValidateGlamourerData, cancellationToken);

    public Task<AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>> ListDesignsAsync(CancellationToken cancellationToken) =>
        OnFrameworkAsync<IReadOnlyList<AppearanceDesignEntry>>(() =>
        {
            if (ipc.GlamourerState() != AppearanceApiState.Ready)
                return new AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>(AppearanceReadStatus.Unavailable);
            if (ipc.GetDesignList() is not { } list)
                return new(AppearanceReadStatus.Failed);
            IReadOnlyList<AppearanceDesignEntry> designs = list.Select(pair => new AppearanceDesignEntry(pair.Key, pair.Value ?? string.Empty)).ToList();
            return new(AppearanceReadStatus.Ok, designs);
        }, result => result, cancellationToken);

    public Task<AppearanceReadResult<AppearanceCaptureRead>> ReadDesignAsync(AppearanceDesignEntry design, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(design);
        return OnFrameworkAsync<AppearanceCaptureRead>(() =>
        {
            if (ipc.GlamourerState() != AppearanceApiState.Ready)
                return new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.Unavailable);
            var data = ipc.GetDesignBase64(design.Id);
            if (data is null)
                return new(AppearanceReadStatus.DesignNotFound);
            if (data.Length > MaximumGlamourerTextLength)
                return new(AppearanceReadStatus.InvalidData);
            // The design id and name are selection data only; they never enter the bundle.
            return new(AppearanceReadStatus.Ok, new AppearanceCaptureRead(
                AppearanceCaptureSource.SavedDesign, data, [], design.Name, null, null, design.Id));
        }, ValidateGlamourerData, cancellationToken);
    }

    /// <summary>Non-empty, within the limit, and exactly the canonical Base64 encoding of its bytes.</summary>
    public static bool IsCanonicalBase64(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaximumGlamourerTextLength || text.Length % 4 != 0)
            return false;
        foreach (var character in text)
        {
            if (character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '='))
                return false;
        }
        var bytes = new byte[Base64.GetMaxDecodedFromUtf8Length(text.Length)];
        if (!Convert.TryFromBase64String(text, bytes, out var written) || written == 0)
            return false;
        // Re-encoding must give the same text (no non-zero padding bits).
        return string.Equals(Convert.ToBase64String(bytes, 0, written), text, StringComparison.Ordinal);
    }

    private static AppearanceReadStatus GlamourerFailure(int code) => code switch
    {
        GlamourerActorNotFound => AppearanceReadStatus.PlayerNotFound,
        GlamourerInvalidKey => AppearanceReadStatus.StateLocked,
        _ => AppearanceReadStatus.Failed,
    };

    /// <summary>Off the framework thread: the copied Glamourer data must be canonical Base64 (opaque otherwise).</summary>
    private static AppearanceReadResult<AppearanceCaptureRead> ValidateGlamourerData(AppearanceReadResult<AppearanceCaptureRead> result) =>
        result.Ok && !IsCanonicalBase64(result.Value!.GlamourerState)
            ? new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.InvalidData)
            : result;

    /// <summary>
    /// Runs <paramref name="read"/> (IPC calls and the copy into an RSC-owned
    /// snapshot) on the framework thread, then <paramref name="check"/> on the
    /// thread pool. While unloading nothing is called.
    /// </summary>
    private async Task<AppearanceReadResult<T>> OnFrameworkAsync<T>(
        Func<AppearanceReadResult<T>> read, Func<AppearanceReadResult<T>, AppearanceReadResult<T>> check, CancellationToken cancellationToken)
    {
        if (unloading())
            return new AppearanceReadResult<T>(AppearanceReadStatus.Unavailable);
        AppearanceReadResult<T> copied;
        try
        {
            copied = await dispatcher.InvokeAsync(() =>
            {
                // The dispatcher runs inline (off the framework thread) once unloading.
                if (unloading())
                    return new AppearanceReadResult<T>(AppearanceReadStatus.Unavailable);
                try
                {
                    return read();
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
                {
                    // An IPC error (provider gone, type mismatch, its own fault):
                    // a status only; the exception (which may name a path) is dropped.
                    return new AppearanceReadResult<T>(AppearanceReadStatus.Failed);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The dispatcher could not run it (for example while unloading).
            return new AppearanceReadResult<T>(AppearanceReadStatus.Unavailable);
        }
        // The continuation may still be on the framework thread: check elsewhere.
        return await Task.Run(() => check(copied), cancellationToken).ConfigureAwait(false);
    }
}
