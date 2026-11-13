#if RSC_DEVELOPMENT
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// HV-4 only (Debug build, dev plugin): Customize+ IPC API 6.4 by label, as
/// checked against the Customize+ 2.2.1.3 tag (commit ff733616). Bound:
/// <c>General.GetApiVersion</c>, <c>General.IsValid</c>,
/// <c>GameState.Get/SetCutsceneParentIndex</c>,
/// <c>Profile.SetTemporaryProfileOnCharacter</c>,
/// <c>Profile.DeleteTemporaryProfileByUniqueId</c>, and the reads
/// <c>Profile.GetActiveProfileIdOnCharacter</c> and <c>Profile.GetList</c>.
/// HV-5 M3 (owner decision D-M3-2) adds one read, <c>Profile.GetByUniqueId</c>,
/// called only inside <see cref="ActiveProfile"/> and only with the id the
/// active-profile read returned in that same call; no member takes an id, and
/// the id leaves this class only as a short hash.
/// Nothing here deletes by object index, edits a persistent profile, template,
/// priority or character, or subscribes to a Customize+ event. The installed
/// version comes from Dalamud's plugin list (read only); the generation is the
/// runner's. Calls throw when Customize+ does not answer: the stage decides
/// what that means. Game thread only.
/// </summary>
internal sealed class HumanActorCustomizePlusIpc : IHumanActorCustomizePlus, IHumanCatalogueProfileReads
{
    internal const string InternalName = "CustomizePlus";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Func<long> generation;
    private readonly ICallGateSubscriber<(int, int)> version;
    private readonly ICallGateSubscriber<bool> valid;
    private readonly ICallGateSubscriber<int, int> getParent;
    private readonly ICallGateSubscriber<int, int, int> setParent;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> setTemporary;
    private readonly ICallGateSubscriber<Guid, int> deleteTemporary;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> activeProfile;
    private readonly ICallGateSubscriber<Guid, (int, string?)> profileContent;
    private readonly ICallGateSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>> profiles;

    internal HumanActorCustomizePlusIpc(IDalamudPluginInterface pluginInterface, Func<long> generation)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
        version = pluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        valid = pluginInterface.GetIpcSubscriber<bool>("CustomizePlus.General.IsValid");
        getParent = pluginInterface.GetIpcSubscriber<int, int>("CustomizePlus.GameState.GetCutsceneParentIndex");
        setParent = pluginInterface.GetIpcSubscriber<int, int, int>("CustomizePlus.GameState.SetCutsceneParentIndex");
        setTemporary = pluginInterface.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        deleteTemporary = pluginInterface.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId");
        activeProfile = pluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        profileContent = pluginInterface.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
        profiles = pluginInterface.GetIpcSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>(
            "CustomizePlus.Profile.GetList");
    }

    /// <summary>
    /// Null only when the plugin list holds no loaded Customize+; a list that
    /// cannot be read throws, so "not loaded" is never inferred from a failed read.
    /// </summary>
    public string? PluginVersion => pluginInterface.InstalledPlugins
        .FirstOrDefault(plugin => plugin.IsLoaded && string.Equals(plugin.InternalName, InternalName, StringComparison.Ordinal))
        ?.Version.ToString();

    public (int Major, int Minor)? ApiVersion
    {
        get
        {
            try
            {
                return version.HasFunction ? version.InvokeFunc() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool IsValid => valid.InvokeFunc();

    public long Generation => generation();

    public int GetParent(int objectIndex) => getParent.InvokeFunc(objectIndex);

    public int SetParent(int objectIndex, int parent) => setParent.InvokeFunc(objectIndex, parent);

    public (int Code, Guid? Profile) SetTemporaryProfile(ushort objectIndex, string profileJson) =>
        setTemporary.InvokeFunc(objectIndex, profileJson);

    public int DeleteTemporaryProfile(Guid profile) => deleteTemporary.InvokeFunc(profile);

    /// <summary>A comparable fingerprint only: short hashes, no profile name, character name or id in clear.</summary>
    public string OwnerProfiles()
    {
        var (code, active) = activeProfile.InvokeFunc(0);
        var list = profiles.InvokeFunc();
        var lines = string.Join("\n", list.Select(entry => $"{entry.Item1:N}|{entry.Item2}|{entry.Item3}|" +
            string.Join(";", entry.Item4.Select(character => $"{character.Item1}/{character.Item2}/{character.Item3}/{character.Item4}")) +
            $"|{entry.Item5}|{entry.Item6}"));
        return $"active {code}:{Hash((active ?? Guid.Empty).ToString("N"))}; list {list.Count}:{Hash(lines)}";
    }

    /// <summary>
    /// HV-5 M3, read only: the character's active normal profile, then its
    /// content for that id only. The id is returned as 8 hex characters of
    /// its SHA-256, never in clear; the content read is skipped unless the
    /// first read answered Success with a non-empty id.
    /// </summary>
    public HumanCatalogueProfileAnswer ActiveProfile(ushort objectIndex)
    {
        var (code, id) = activeProfile.InvokeFunc(objectIndex);
        if (code != HumanActorCustomizePlusStage.Success || id is not { } active || active == Guid.Empty)
            return new HumanCatalogueProfileAnswer(code, null, null, null);
        var (contentCode, json) = profileContent.InvokeFunc(active);
        return new HumanCatalogueProfileAnswer(code, Hash(active.ToString("N"))[..8], contentCode, json);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
}
#endif
