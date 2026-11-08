#if RSC_DEVELOPMENT
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LuminaWorld = Lumina.Excel.Sheets.World;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Protocol;
using PenumbraDisposed = Penumbra.Api.IpcSubscribers.Disposed;
using PenumbraInitialized = Penumbra.Api.IpcSubscribers.Initialized;

namespace RSyncStageCraft;

/// <summary>
/// A5, T3-1 only: composes the Debug human puppet host with its four adapters
/// for one hidden vanilla human, and nothing else. It exists only in a Debug
/// build (RSC_DEVELOPMENT) loaded as a dev plugin, and its only trigger is one
/// command. R-1 is an operating rule, not enforced here: the command is typed
/// only in the Dalamud log window's console. Dalamud would also handle it if
/// typed in game chat, locally and with nothing sent, but that route is not
/// used. Each step runs on the next framework update. Its records go to
/// the Dalamud log with no name, address or path.
/// </summary>
internal sealed class HumanPuppetT3Run
{
    /// <summary>Not a game command; registered only here, with no help listing.</summary>
    internal const string Command = "/rsct31";

    private static readonly TimeSpan UnloadWait = TimeSpan.FromSeconds(5);
    private static readonly string[] Providers = ["Penumbra", "Glamourer", HumanParentMapIpc.CustomizePlusInternalName];

    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly Func<int> otherRscActors;
    private readonly Func<bool> serverSessionPossible;
    private readonly Action<string> log;
    private readonly HumanPuppetNativeBackend backend = new();
    private readonly HumanPuppetT3Sequence sequence;
    private readonly bool commandAdded;
    private readonly Action releaseSubscriptions;
    private long generation;
    private long update;
    private volatile bool shutDown;

    internal HumanPuppetT3Run(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IDataManager data,
        Func<int> otherRscActors,
        Func<bool> serverSessionPossible,
        Func<SceneLocation?> location,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(pluginInterface);
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        this.condition = condition ?? throw new ArgumentNullException(nameof(condition));
        this.otherRscActors = otherRscActors ?? throw new ArgumentNullException(nameof(otherRscActors));
        this.serverSessionPossible = serverSessionPossible ?? throw new ArgumentNullException(nameof(serverSessionPossible));
        this.log = log ?? throw new ArgumentNullException(nameof(log));

        HumanPuppetT3Sequence? built = null;
        sequence = new HumanPuppetT3Sequence(
            backend,
            new HumanParentMapIpc(pluginInterface),
            new HumanPenumbraReadIpc(pluginInterface),
            new HumanGlamourerReadIpc(pluginInterface),
            new HumanPuppetT3Probe(pluginInterface, backend, () => built?.PuppetSlot),
            ReadW2Row(data),
            SafeOtherRscActors,
            () => Interlocked.Read(ref generation),
            SceneExit,
            location,
            log);
        built = sequence;

        // H1-b: any load, unload or reinitialization of a provider moves the
        // generation. The subscriptions are released by the host's unload, or
        // here directly when the constructor fails or the unload times out.
        IDisposable? initialized = null;
        IDisposable? disposed = null;
        var subscribed = false;
        releaseSubscriptions = () =>
        {
            if (subscribed)
                pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
            subscribed = false;
            initialized?.Dispose();
            disposed?.Dispose();
            initialized = null;
            disposed = null;
        };
        try
        {
            pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
            subscribed = true;
            initialized = PenumbraInitialized.Subscriber(pluginInterface, NextGeneration);
            disposed = PenumbraDisposed.Subscriber(pluginInterface, NextGeneration);
            sequence.Host.RegisterDependency(new Release(releaseSubscriptions));

            commandAdded = commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "T3-1 step, from the Dalamud log window's console only.",
                ShowInHelp = false,
            });
        }
        catch
        {
            ReleaseQuietly();
            throw;
        }
        log(commandAdded
            ? $"RSC T3-1 runner loaded. Steps, typed only in the Dalamud log window's console: {Command} " + string.Join(", ", HumanPuppetT3Sequence.Steps) + "."
            : "RSC T3-1 runner loaded without its command; no step can run.");
    }

    /// <summary>Game thread, every framework update.</summary>
    internal void Tick()
    {
        if (shutDown)
            return;
        update++;
        sequence.OnFrameworkUpdate(update, framework.UpdateDelta.TotalMilliseconds);
    }

    /// <summary>
    /// Plugin unload (S2): the command first, then the host's unload order with
    /// its game-thread steps inline when already on the framework thread, within
    /// a bounded wait. A puppet left after the wait is residue with no approved clean-up.
    /// </summary>
    internal void Shutdown()
    {
        if (shutDown)
            return;
        shutDown = true;
        if (commandAdded)
            commands.RemoveHandler(Command);
        try
        {
            var unload = sequence.Host.UnloadAsync(RunOnGameThread);
            if (!unload.Wait(UnloadWait))
            {
                // Nothing may call back into the unloaded plugin.
                ReleaseQuietly();
                log("RSC T3-1 unload did not finish within 5 s; any puppet left is residue with no approved clean-up.");
                return;
            }
            var report = unload.Result;
            log($"RSC T3-1 unload: deleted {report.Deleted}, residue {report.Residue.Count}, drained {report.DrainedWork}, " +
                $"release failures {report.DependencyFailures.Count}; order {string.Join(" > ", report.Order)}." +
                (sequence.Host.Stop is { } stop ? $" Stop: {stop.Rule} at {stop.Step}." : ""));
        }
        catch (Exception exception)
        {
            ReleaseQuietly();
            log($"RSC T3-1 unload failed ({exception.GetType().Name}); any puppet left is residue with no approved clean-up.");
        }
    }

    private void ReleaseQuietly()
    {
        try
        {
            releaseSubscriptions();
        }
        catch
        {
            // Best effort: the subscriptions belong to an unloading plugin.
        }
    }

    private Task RunOnGameThread(Action action)
    {
        if (!framework.IsInFrameworkUpdateThread)
            return framework.RunOnFrameworkThread(action);
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private void OnCommand(string command, string arguments)
    {
        if (!shutDown)
            sequence.Submit(arguments);
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        try
        {
            if (args.AffectedInternalNames.Any(name => Providers.Contains(name, StringComparer.Ordinal)))
                NextGeneration();
        }
        catch
        {
            // Unknown which plugins changed: fail closed.
            NextGeneration();
        }
    }

    private void NextGeneration() => Interlocked.Increment(ref generation);

    private int SafeOtherRscActors()
    {
        try
        {
            return otherRscActors();
        }
        catch
        {
            // Unknown: treat the scene as full.
            return int.MaxValue;
        }
    }

    /// <summary>H1-c: null while the scene is quiet; otherwise why not. Total.</summary>
    private string? SceneExit()
    {
        try
        {
            if (!clientState.IsLoggedIn)
                return "not logged in";
            if (clientState.IsGPosing)
                return "GPose is active";
            if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] ||
                condition[ConditionFlag.WatchingCutscene78])
                return "a cutscene is active";
            if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
                return "a loading screen is active";
            if (backend.LocalPlayer == 0)
                return "there is no local player";
            if (SafeOtherRscActors() != 0)
                return "another RSC actor is in the scene";
            if (serverSessionPossible())
                return "an RSC server session is active or may start";
            return null;
        }
        catch (Exception exception)
        {
            return $"the scene could not be read ({exception.GetType().Name})";
        }
    }

    /// <summary>W-2's row from the game data, in English as F11 read it; null when it cannot be read.</summary>
    private static HumanPuppetWorldRow? ReadW2Row(IDataManager data)
    {
        try
        {
            return data.GetExcelSheet<LuminaWorld>(ClientLanguage.English).GetRowOrDefault(HumanPuppetW2.WorldId) is { } row
                ? new HumanPuppetWorldRow(row.RowId, row.InternalName.ExtractText(), row.Name.ExtractText(), row.DataCenter.RowId, row.IsPublic)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
#endif
