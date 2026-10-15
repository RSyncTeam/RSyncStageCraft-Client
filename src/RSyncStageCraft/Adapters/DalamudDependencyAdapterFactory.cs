using Dalamud.Plugin;
using RSyncStageCraft.Core.Dependencies;
using GlamourerApiVersion = Glamourer.Api.IpcSubscribers.ApiVersion;
using PenumbraApiVersion = Penumbra.Api.IpcSubscribers.ApiVersion;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Dalamud-only construction boundary for generated dependency subscribers.
/// The returned adapters expose only the signature-neutral dependency contract.
/// </summary>
public static class DalamudDependencyAdapterFactory
{
    public static IDependencyAdapter CreatePenumbra(
        IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        var apiVersion = new PenumbraApiVersion(pluginInterface);
        return new PenumbraDependencyAdapter(
            apiVersion.Invoke,
            () => GetInstalledVersion(pluginInterface, "Penumbra"),
            typeof(PenumbraApiVersion).Assembly.GetName().Version?.ToString());
    }

    public static IDependencyAdapter CreateGlamourer(
        IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        var apiVersion = new GlamourerApiVersion(pluginInterface);
        return new GlamourerDependencyAdapter(
            apiVersion.Invoke,
            () => GetInstalledVersion(pluginInterface, "Glamourer"),
            typeof(GlamourerApiVersion).Assembly.GetName().Version?.ToString());
    }

    private static string? GetInstalledVersion(
        IDalamudPluginInterface pluginInterface,
        string internalName)
    {
        try
        {
            return pluginInterface.InstalledPlugins
                .FirstOrDefault(plugin => string.Equals(
                    plugin.InternalName,
                    internalName,
                    StringComparison.OrdinalIgnoreCase))
                ?.Version
                .ToString();
        }
        catch
        {
            // Installed-plugin metadata is diagnostic only. Provider API
            // readiness continues to come from the pinned generated subscriber.
            return null;
        }
    }
}
