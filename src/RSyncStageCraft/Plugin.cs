using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Dependencies;
using RSyncStageCraft.Core.Diagnostics;
using RSyncStageCraft.Ui;

namespace RSyncStageCraft;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
#if RSC_DEVELOPMENT
    // T3-1's console command only (Debug build, dev plugin).
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
#endif

    public Plugin()
    {
        Observations = new RuntimeObservationTrace(PublishObservation);
        Observations.Record("bootstrap.start");
        DataPaths = new DalamudPluginDataPaths(PluginInterface.ConfigDirectory);
        Dependencies = RequiredDependencyRegistryFactory.Create(
            () => DalamudDependencyAdapterFactory.CreatePenumbra(PluginInterface),
            () => DalamudDependencyAdapterFactory.CreateGlamourer(PluginInterface));
        Observations.Record("bootstrap.dependencies-ready");
        PluginUi? ui = null;
        DalamudLocationProvider? location = null;
        PluginConnection? connection = null;
        try
        {
            location = new DalamudLocationProvider(Framework, Condition, ObjectTable, ClientState, PlayerState, DataManager);
            Location = location;
            connection = new PluginConnection(
                PluginInterface,
                Framework,
                Dependencies,
                DataPaths,
                location,
                DataManager,
                ClientState,
                Condition,
                TargetManager,
                ObjectTable,
#if RSC_DEVELOPMENT
                CommandManager,
#endif
                message => Log.Information(message));
            Connection = connection;
            Observations.Record("bootstrap.connection-ready");
            ui = new PluginUi(
                PluginInterface,
                Dependencies,
                Observations,
                connection,
                TextureProvider,
                () => Location.Current,
                () => Location.Pose,
                message => Log.Information(message));
            Ui = ui;
            Observations.Record("bootstrap.complete");
            Log.Information(
                $"RSyncStageCraft production bootstrap loaded; observation run " +
                $"{Observations.RunId:N}.");
        }
        catch
        {
            Observations.Record("bootstrap.rollback");
            ui?.Dispose();
            connection?.Shutdown();
            location?.Dispose();
            Dependencies.Dispose();
            throw;
        }
    }

    internal DalamudPluginDataPaths DataPaths { get; }
    internal DependencyRegistry Dependencies { get; }
    internal RuntimeObservationTrace Observations { get; }
    internal PluginUi Ui { get; }
    internal PluginConnection Connection { get; }
    internal DalamudLocationProvider Location { get; }

    public void Dispose()
    {
        Observations.Record("unload.start");
        Ui.Dispose();
        Observations.Record("unload.ui-detached");
        Connection.Shutdown();
        Observations.Record("unload.connection-closed");
        Location.Dispose();
        Dependencies.Dispose();
        Observations.Record("unload.dependencies-disposed");
        Observations.Record("unload.complete");
    }

    private static void PublishObservation(RuntimeObservation observation) =>
        Log.Information(
            $"RSyncStageCraft observation run={observation.RunId:N} " +
            $"sequence={observation.Sequence} " +
            $"thread={observation.ManagedThreadId} " +
            $"externalDepth={observation.ExternalCallDepth} " +
            $"reentrant={observation.Reentrant} " +
            $"phase={observation.Phase} " +
            $"detail={observation.Detail ?? "-"}");
}
