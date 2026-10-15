using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RSyncStageCraft.Core.Appearance;
using GlamourerApiVersion = Glamourer.Api.IpcSubscribers.ApiVersion;
using GlamourerGetDesignBase64 = Glamourer.Api.IpcSubscribers.GetDesignBase64;
using GlamourerGetDesignList = Glamourer.Api.IpcSubscribers.GetDesignList;
using GlamourerGetStateBase64 = Glamourer.Api.IpcSubscribers.GetStateBase64;
using PenumbraApiVersion = Penumbra.Api.IpcSubscribers.ApiVersion;
using PenumbraGetGameObjectResourcePaths = Penumbra.Api.IpcSubscribers.GetGameObjectResourcePaths;
using PenumbraGetModDirectory = Penumbra.Api.IpcSubscribers.GetModDirectory;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// The local player through Dalamud's public object table (never a
/// hardcoded index). Framework thread only.
/// </summary>
internal sealed class DalamudAppearancePlayerLocator(IObjectTable objects) : IAppearancePlayerLocator
{
    public AppearancePlayer? LocalPlayer() =>
        objects.LocalPlayer is { } player && player.Address != nint.Zero
            ? new AppearancePlayer(player.ObjectIndex, player.Address, player.GameObjectId)
            : null;

    /// <summary>Still the local player, and its index still resolves to that exact object.</summary>
    public bool IsStill(AppearancePlayer player) =>
        objects.LocalPlayer is { } local &&
        local.Address == player.Address && local.ObjectIndex == player.ObjectIndex && local.GameObjectId == player.GameObjectId &&
        objects[player.ObjectIndex] is { } atIndex &&
        atIndex.Address == player.Address && atIndex.GameObjectId == player.GameObjectId;
}

/// <summary>
/// The approved read-only IPC calls through the pinned generated
/// subscribers (Penumbra.Api 5.15.1, Glamourer.Api 2.8.2). Only these labels
/// are bound:
/// <c>Penumbra.ApiVersion.V5</c>, <c>Penumbra.GetModDirectory</c>,
/// <c>Penumbra.GetGameObjectResourcePaths.V5</c>, <c>Glamourer.ApiVersion.V2</c>,
/// <c>Glamourer.GetStateBase64</c>, <c>Glamourer.GetDesignList.V2</c> and
/// <c>Glamourer.GetDesignBase64</c>. Nothing applies, sets, reverts, redraws,
/// or touches temporary mods or collections; Penumbra meta is never read.
/// Every member is called on the framework thread (by the capture reader).
/// </summary>
internal sealed class DalamudAppearanceIpc : IAppearanceIpc
{
    private readonly PenumbraApiVersion penumbraVersion;
    private readonly PenumbraGetModDirectory modDirectory;
    private readonly PenumbraGetGameObjectResourcePaths resourcePaths;
    private readonly GlamourerApiVersion glamourerVersion;
    private readonly GlamourerGetStateBase64 state;
    private readonly GlamourerGetDesignList designList;
    private readonly GlamourerGetDesignBase64 designBase64;

    internal DalamudAppearanceIpc(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        penumbraVersion = new PenumbraApiVersion(pluginInterface);
        modDirectory = new PenumbraGetModDirectory(pluginInterface);
        resourcePaths = new PenumbraGetGameObjectResourcePaths(pluginInterface);
        glamourerVersion = new GlamourerApiVersion(pluginInterface);
        state = new GlamourerGetStateBase64(pluginInterface);
        designList = new GlamourerGetDesignList(pluginInterface);
        designBase64 = new GlamourerGetDesignBase64(pluginInterface);
    }

    /// <summary>The same gate as <see cref="PenumbraDependencyAdapter"/>; every approved subscriber must be valid.</summary>
    public AppearanceApiState PenumbraState()
    {
        if (!penumbraVersion.Valid || !modDirectory.Valid || !resourcePaths.Valid)
            return AppearanceApiState.Unavailable;
        var (breaking, feature) = penumbraVersion.Invoke();
        return breaking == PenumbraDependencyAdapter.RequiredBreakingVersion && feature >= PenumbraDependencyAdapter.MinimumFeatureVersion
            ? AppearanceApiState.Ready
            : AppearanceApiState.Incompatible;
    }

    /// <summary>The same gate as <see cref="GlamourerDependencyAdapter"/>; every approved subscriber must be valid.</summary>
    public AppearanceApiState GlamourerState()
    {
        if (!glamourerVersion.Valid || !state.Valid || !designList.Valid || !designBase64.Valid)
            return AppearanceApiState.Unavailable;
        var (major, minor) = glamourerVersion.Invoke();
        return major == GlamourerDependencyAdapter.RequiredMajorVersion && minor >= GlamourerDependencyAdapter.MinimumMinorVersion
            ? AppearanceApiState.Ready
            : AppearanceApiState.Incompatible;
    }

    public string? GetModDirectory() => modDirectory.Invoke();

    public Dictionary<string, HashSet<string>>?[]? GetGameObjectResourcePaths(ushort[] gameObjectIndices) =>
        resourcePaths.Invoke(gameObjectIndices);

    public (int Code, string? State) GetStateBase64(int objectIndex)
    {
        // Key 0: read only; a state locked by another plugin answers InvalidKey.
        var (code, text) = state.Invoke(objectIndex, 0);
        return ((int)code, text);
    }

    public Dictionary<Guid, string>? GetDesignList() => designList.Invoke();

    public string? GetDesignBase64(Guid designId) => designBase64.Invoke(designId);
}
