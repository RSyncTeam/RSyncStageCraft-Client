#if RSC_DEVELOPMENT
using Dalamud.Plugin;
using Penumbra.Api.Enums;
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Appearance;
using PenumbraAddTemporaryMod = Penumbra.Api.IpcSubscribers.AddTemporaryMod;
using PenumbraApiVersion = Penumbra.Api.IpcSubscribers.ApiVersion;
using PenumbraAssignTemporaryCollection = Penumbra.Api.IpcSubscribers.AssignTemporaryCollection;
using PenumbraCreateTemporaryCollection = Penumbra.Api.IpcSubscribers.CreateTemporaryCollection;
using PenumbraDeleteTemporaryCollection = Penumbra.Api.IpcSubscribers.DeleteTemporaryCollection;
using PenumbraGetCollectionForObject = Penumbra.Api.IpcSubscribers.GetCollectionForObject;
using PenumbraGetCutsceneParentIndex = Penumbra.Api.IpcSubscribers.GetCutsceneParentIndex;
using PenumbraGetGameObjectResourcePaths = Penumbra.Api.IpcSubscribers.GetGameObjectResourcePaths;
using PenumbraGetMetaManipulations = Penumbra.Api.IpcSubscribers.GetMetaManipulations;
using PenumbraGetModDirectory = Penumbra.Api.IpcSubscribers.GetModDirectory;
using PenumbraRemoveTemporaryMod = Penumbra.Api.IpcSubscribers.RemoveTemporaryMod;
using PenumbraSetCutsceneParentIndex = Penumbra.Api.IpcSubscribers.SetCutsceneParentIndex;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// HV-2 only (Debug build, dev plugin): Penumbra through the pinned
/// Penumbra.Api 5.15.1 subscribers, for one actor-specific temporary
/// collection. Bound: <c>ApiVersion</c>, <c>GetModDirectory</c>,
/// <c>GetGameObjectResourcePaths</c>, <c>GetMetaManipulations</c>,
/// <c>Get/SetCutsceneParentIndex</c>, <c>GetCollectionForObject</c>,
/// <c>CreateTemporaryCollection</c>, <c>AddTemporaryMod</c>,
/// <c>AssignTemporaryCollection</c> (never forced), <c>RemoveTemporaryMod</c>
/// and <c>DeleteTemporaryCollection</c>. Nothing here sets a permanent
/// collection, assigns by name or for everyone, adds a mod for every
/// collection, redraws anything or subscribes to any event: the actor is
/// redrawn by RSC itself, so nothing is queued in Penumbra. Every member is
/// total. Game thread only.
/// </summary>
internal sealed class HumanActorPenumbraIpc : IHumanActorPenumbra
{
    /// <summary>Penumbra's caller identity for the temporary collection (any text is accepted).</summary>
    internal const string Identity = "RSyncStageCraft";

    private readonly PenumbraApiVersion version;
    private readonly PenumbraGetModDirectory modDirectory;
    private readonly PenumbraGetGameObjectResourcePaths resourcePaths;
    private readonly PenumbraGetMetaManipulations meta;
    private readonly PenumbraGetCutsceneParentIndex getParent;
    private readonly PenumbraSetCutsceneParentIndex setParent;
    private readonly PenumbraGetCollectionForObject collectionForObject;
    private readonly PenumbraCreateTemporaryCollection create;
    private readonly PenumbraAddTemporaryMod addMod;
    private readonly PenumbraAssignTemporaryCollection assign;
    private readonly PenumbraRemoveTemporaryMod removeMod;
    private readonly PenumbraDeleteTemporaryCollection delete;

    internal HumanActorPenumbraIpc(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        version = new PenumbraApiVersion(pluginInterface);
        modDirectory = new PenumbraGetModDirectory(pluginInterface);
        resourcePaths = new PenumbraGetGameObjectResourcePaths(pluginInterface);
        meta = new PenumbraGetMetaManipulations(pluginInterface);
        getParent = new PenumbraGetCutsceneParentIndex(pluginInterface);
        setParent = new PenumbraSetCutsceneParentIndex(pluginInterface);
        collectionForObject = new PenumbraGetCollectionForObject(pluginInterface);
        create = new PenumbraCreateTemporaryCollection(pluginInterface);
        addMod = new PenumbraAddTemporaryMod(pluginInterface);
        assign = new PenumbraAssignTemporaryCollection(pluginInterface);
        removeMod = new PenumbraRemoveTemporaryMod(pluginInterface);
        delete = new PenumbraDeleteTemporaryCollection(pluginInterface);
    }

    public bool Available
    {
        get
        {
            try
            {
                if (!version.Valid || !modDirectory.Valid || !resourcePaths.Valid || !meta.Valid || !getParent.Valid || !setParent.Valid ||
                    !collectionForObject.Valid || !create.Valid || !addMod.Valid || !assign.Valid || !removeMod.Valid || !delete.Valid)
                    return false;
                var (breaking, feature) = version.Invoke();
                return breaking == PenumbraDependencyAdapter.RequiredBreakingVersion && feature >= PenumbraDependencyAdapter.MinimumFeatureVersion;
            }
            catch
            {
                return false;
            }
        }
    }

    public string? ModDirectory()
    {
        try
        {
            return modDirectory.Invoke();
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<AppearanceResourceEntry>? ResourcePaths(int globalIndex)
    {
        if (globalIndex is < 0 or > ushort.MaxValue)
            return null;
        try
        {
            // Exactly one dictionary, for the one index asked; anything else fails closed.
            if (resourcePaths.Invoke([(ushort)globalIndex]) is not { Length: 1 } maps || maps[0] is not { } map)
                return null;
            var entries = new List<AppearanceResourceEntry>(map.Count);
            foreach (var (actual, gamePaths) in map)
            {
                if (actual is null || gamePaths is null)
                    return null;
                entries.Add(new AppearanceResourceEntry(actual, gamePaths.ToArray()));
            }
            return entries;
        }
        catch
        {
            return null;
        }
    }

    public string? MetaManipulations(int globalIndex)
    {
        try
        {
            return meta.Invoke(globalIndex);
        }
        catch
        {
            return null;
        }
    }

    public int? GetParent(int globalIndex)
    {
        try
        {
            return getParent.Invoke(globalIndex);
        }
        catch
        {
            return null;
        }
    }

    public bool SetParent(int globalIndex, int parent)
    {
        try
        {
            return setParent.Invoke(globalIndex, parent) == PenumbraApiEc.Success;
        }
        catch
        {
            return false;
        }
    }

    public HumanCollectionReading? CollectionFor(int globalIndex)
    {
        try
        {
            var (valid, individual, effective) = collectionForObject.Invoke(globalIndex);
            return new(valid, individual, effective.Id);
        }
        catch
        {
            return null;
        }
    }

    public Guid? CreateTemporaryCollection(string name)
    {
        try
        {
            return create.Invoke(Identity, name, out var id) == PenumbraApiEc.Success && id != Guid.Empty ? id : null;
        }
        catch
        {
            return null;
        }
    }

    public bool AddTemporaryMod(string tag, Guid collection, IReadOnlyDictionary<string, string> paths, string manipulations, int priority)
    {
        try
        {
            return addMod.Invoke(tag, collection, new Dictionary<string, string>(paths, StringComparer.OrdinalIgnoreCase), manipulations, priority) ==
                PenumbraApiEc.Success;
        }
        catch
        {
            return false;
        }
    }

    public HumanAssignmentResult Assign(Guid collection, int globalIndex)
    {
        try
        {
            return assign.Invoke(collection, globalIndex, false) switch
            {
                PenumbraApiEc.Success => HumanAssignmentResult.Success,
                PenumbraApiEc.CharacterCollectionExists => HumanAssignmentResult.Refused,
                _ => HumanAssignmentResult.Failed,
            };
        }
        catch
        {
            return HumanAssignmentResult.Failed;
        }
    }

    public HumanModRemoval RemoveTemporaryMod(string tag, Guid collection, int priority)
    {
        try
        {
            return removeMod.Invoke(tag, collection, priority) switch
            {
                PenumbraApiEc.Success => HumanModRemoval.Removed,
                PenumbraApiEc.NothingChanged => HumanModRemoval.NotPresent,
                _ => HumanModRemoval.Failed,
            };
        }
        catch
        {
            return HumanModRemoval.Failed;
        }
    }

    public bool DeleteTemporaryCollection(Guid collection)
    {
        try
        {
            return delete.Invoke(collection) == PenumbraApiEc.Success;
        }
        catch
        {
            return false;
        }
    }
}
#endif
