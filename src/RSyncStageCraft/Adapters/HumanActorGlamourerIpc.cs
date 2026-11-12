#if RSC_DEVELOPMENT
using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using RSyncStageCraft.Core.Actors.Humans;
using GlamourerApiVersion = Glamourer.Api.IpcSubscribers.ApiVersion;
using GlamourerApplyState = Glamourer.Api.IpcSubscribers.ApplyState;
using GlamourerCanUnlock = Glamourer.Api.IpcSubscribers.CanUnlock;
using GlamourerGetState = Glamourer.Api.IpcSubscribers.GetState;
using GlamourerRevertState = Glamourer.Api.IpcSubscribers.RevertState;
using GlamourerUnlockState = Glamourer.Api.IpcSubscribers.UnlockState;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// HV-3 only (Debug build, dev plugin): Glamourer through the pinned
/// Glamourer.Api 2.8.2 subscribers, by object index only. Bound:
/// <c>ApiVersion.V2</c>, <c>UnlockState</c>, <c>ApplyState</c>,
/// <c>GetState</c> (also as JSON text, for HV-5's readback), <c>RevertState</c> and <c>CanUnlock</c>. Nothing here
/// calls a by-name variant, <c>DeletePlayerState</c>, <c>UnlockAll</c>,
/// <c>ReapplyState</c>, <c>RevertToAutomation</c> or a design call, and
/// nothing subscribes to a Glamourer event. Installed versions come from
/// Dalamud's plugin list (read only); the generation is the runner's. Calls
/// throw when Glamourer does not answer: the stage decides what that means.
/// Game thread only.
/// </summary>
internal sealed class HumanActorGlamourerIpc : IHumanActorGlamourer
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Func<long> generation;
    private readonly GlamourerApiVersion version;
    private readonly GlamourerUnlockState unlock;
    private readonly GlamourerApplyState apply;
    private readonly GlamourerGetState getState;
    private readonly GlamourerRevertState revert;
    private readonly GlamourerCanUnlock canUnlock;

    internal HumanActorGlamourerIpc(IDalamudPluginInterface pluginInterface, Func<long> generation)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
        version = new GlamourerApiVersion(pluginInterface);
        unlock = new GlamourerUnlockState(pluginInterface);
        apply = new GlamourerApplyState(pluginInterface);
        getState = new GlamourerGetState(pluginInterface);
        revert = new GlamourerRevertState(pluginInterface);
        canUnlock = new GlamourerCanUnlock(pluginInterface);
    }

    public (int Major, int Minor)? ApiVersion
    {
        get
        {
            try
            {
                return version.Invoke();
            }
            catch
            {
                return null;
            }
        }
    }

    public string? GlamourerVersion => LoadedVersion("Glamourer");

    public string? PenumbraVersion => LoadedVersion("Penumbra");

    public long Generation => generation();

    public int UnlockState(int objectIndex, uint key) => (int)unlock.Invoke(objectIndex, key);

    public int ApplyState(string base64State, int objectIndex, uint key, ulong flags) =>
        (int)apply.Invoke(base64State, objectIndex, key, (ApplyFlag)flags);

    public (int Code, IReadOnlyDictionary<string, ulong>? Items) GetState(int objectIndex, uint key)
    {
        var (code, state) = getState.Invoke(objectIndex, key);
        if (code != GlamourerApiEc.Success || state?["Equipment"] is not JObject equipment)
            return ((int)code, null);
        var items = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var (slot, _) in HumanActorGlamourerStage.Slots)
        {
            if (equipment[slot]?["ItemId"] is JValue { Type: JTokenType.Integer } id)
                items[slot] = id.Value<ulong>();
        }
        return ((int)code, items);
    }

    public int RevertState(int objectIndex, uint key, ulong flags) => (int)revert.Invoke(objectIndex, key, (ApplyFlag)flags);

    /// <summary>
    /// HV-5 M1 only (C7, owner decision D8 option b): the whole state of
    /// <paramref name="objectIndex"/> as compact JSON text, read with key 0
    /// through the same by-index <c>GetState</c> subscriber. Read only;
    /// nothing is decoded here. Null unless Glamourer answers Success.
    /// </summary>
    internal (int Code, string? Json) StateJson(int objectIndex) => GetStateJson(objectIndex, 0);

    /// <summary>
    /// HV-5 M2: the whole state of <paramref name="objectIndex"/> under
    /// <paramref name="key"/> as compact JSON text, the same way
    /// <see cref="StateJson"/> reads the stored reference, for the readback.
    /// Read only. Null unless Glamourer answers Success.
    /// </summary>
    public (int Code, string? Json) GetStateJson(int objectIndex, uint key)
    {
        var (code, state) = getState.Invoke(objectIndex, key);
        return ((int)code, code == GlamourerApiEc.Success ? state?.ToString(Newtonsoft.Json.Formatting.None) : null);
    }

    public (int Code, bool IsLocked, bool CanUnlock) CanUnlock(int objectIndex, uint key)
    {
        var code = canUnlock.Invoke(objectIndex, key, out var isLocked, out var unlockable);
        return ((int)code, isLocked, unlockable);
    }

    private string? LoadedVersion(string internalName)
    {
        try
        {
            return pluginInterface.InstalledPlugins
                .FirstOrDefault(plugin => plugin.IsLoaded && string.Equals(plugin.InternalName, internalName, StringComparison.Ordinal))
                ?.Version.ToString();
        }
        catch
        {
            return null;
        }
    }
}
#endif
