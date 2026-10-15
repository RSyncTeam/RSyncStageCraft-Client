using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using RSyncStageCraft.Core.Appearance;
using GlamourerAddDesign = Glamourer.Api.IpcSubscribers.AddDesign;
using GlamourerApiVersion = Glamourer.Api.IpcSubscribers.ApiVersion;
using GlamourerGetDesignListExtended = Glamourer.Api.IpcSubscribers.GetDesignListExtended;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// "Make a Copy": the approved Glamourer calls through the pinned
/// generated subscribers (Glamourer.Api 2.8.2). Only these labels are bound:
/// <c>Glamourer.ApiVersion.V2</c>, <c>Glamourer.AddDesign</c> and the
/// read-only <c>Glamourer.GetDesignListExtended</c>. Nothing applies, moves,
/// renames or deletes a design. Separate from the read-only capture IPC.
/// Every member is called on the framework thread (by <see cref="AppearanceCopier"/>).
/// </summary>
internal sealed class DalamudGlamourerDesignIpc : IGlamourerDesignIpc
{
    private readonly GlamourerApiVersion version;
    private readonly GlamourerAddDesign addDesign;
    private readonly GlamourerGetDesignListExtended designList;

    internal DalamudGlamourerDesignIpc(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        version = new GlamourerApiVersion(pluginInterface);
        addDesign = new GlamourerAddDesign(pluginInterface);
        designList = new GlamourerGetDesignListExtended(pluginInterface);
    }

    /// <summary>
    /// The existing gate (<see cref="GlamourerDependencyAdapter"/>): all three
    /// subscribers valid, then the version. A missing provider throws
    /// <see cref="IpcNotReadyError"/>, which fails closed as unavailable.
    /// </summary>
    public AppearanceApiState State() =>
        GlamourerDesignGate.Evaluate(
            version.Valid && addDesign.Valid && designList.Valid,
            () => version.Invoke(),
            GlamourerDependencyAdapter.RequiredMajorVersion,
            GlamourerDependencyAdapter.MinimumMinorVersion);

    /// <summary><c>Glamourer.GetDesignListExtended</c> (read only): GUID, name and full path; null on any failure.</summary>
    public IReadOnlyList<GlamourerDesignListing>? ListDesigns()
    {
        try
        {
            if (designList.Invoke() is not { } list)
                return null;
            var designs = new List<GlamourerDesignListing>(list.Count);
            foreach (var (id, data) in list)
                designs.Add(new GlamourerDesignListing(id, data.Item1 ?? string.Empty, data.Item2 ?? string.Empty));
            return designs;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// One <c>Glamourer.AddDesign</c> call. <see cref="IpcNotReadyError"/>
    /// means it never reached Glamourer; any other exception leaves it unknown
    /// whether Glamourer acted.
    /// </summary>
    public GlamourerAddAnswer AddDesign(string text, string name) =>
        GlamourerDesignGate.Call(() =>
        {
            var code = addDesign.Invoke(text, name, out var created);
            return ((int)code, created);
        }, exception => exception is IpcNotReadyError);
}
