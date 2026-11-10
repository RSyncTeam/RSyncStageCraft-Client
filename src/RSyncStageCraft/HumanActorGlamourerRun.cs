#if RSC_DEVELOPMENT
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft;

/// <summary>
/// HV-3 only: composes HV-1's visible-human baseline with its native adapter,
/// HV-2's Penumbra stage (releasing by collection deletion once an assignment
/// was attempted) and the Glamourer stage with its IPC adapter, and nothing
/// else. It exists only in a Debug build (RSC_DEVELOPMENT) loaded as a dev
/// plugin, and its only trigger is one command, typed in the Dalamud log
/// window's console. Each step runs on the next framework update. Its records
/// go to the Dalamud log with no name, address, path, world or key. No
/// Customize+ or server call is made. Its only subscription is Dalamud's
/// plugin-list change, which only moves a counter: no RSC step can run inside
/// another plugin's call.
/// </summary>
internal sealed class HumanActorGlamourerRun
{
    /// <summary>Not a game command; registered only here, with no help listing.</summary>
    internal const string Command = "/rschv3";

    internal const string LogPrefix = "RSC HV-3 ";

    internal static readonly string[] Steps = ["spawn", "remove", "status"];

    private static readonly TimeSpan UnloadWait = TimeSpan.FromSeconds(5);

    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly Func<int> otherRscActors;
    private readonly Func<bool> serverSessionPossible;
    private readonly Action<string> log;
    private readonly HumanActorNativeBackend backend = new();
    private readonly HumanActorBaseline baseline;
    private readonly bool commandAdded;
    private readonly object gate = new();
    private string? pending;
    private readonly IDalamudPluginInterface pluginInterface;
    private volatile bool shutDown;
    private long generation;
    private bool subscribed;

    internal HumanActorGlamourerRun(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        Func<int> otherRscActors,
        Func<bool> serverSessionPossible,
        Action<string> log)
    {
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        this.condition = condition ?? throw new ArgumentNullException(nameof(condition));
        this.otherRscActors = otherRscActors ?? throw new ArgumentNullException(nameof(otherRscActors));
        this.serverSessionPossible = serverSessionPossible ?? throw new ArgumentNullException(nameof(serverSessionPossible));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        var penumbra = new HumanActorPenumbraIpc(pluginInterface);
        var glamourer = new HumanActorGlamourerIpc(pluginInterface, () => Interlocked.Read(ref generation));
        baseline = new HumanActorBaseline(backend, SceneExit, log,
            new HumanActorPenumbraStage(penumbra, log, LogPrefix) { ReleaseByDeletion = true },
            new HumanActorGlamourerStage(glamourer, log, LogPrefix), LogPrefix);
        commandAdded = commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "HV-3 step, from the Dalamud log window's console only.",
            ShowInHelp = false,
        });
        log(commandAdded
            ? $"RSC HV-3 runner loaded. Steps, typed only in the Dalamud log window's console: {Command} " + string.Join(", ", Steps) + "."
            : "RSC HV-3 runner loaded without its command; no step can run.");
        // Last, so a constructor that throws leaves no subscription behind.
        pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        subscribed = true;
    }

    /// <summary>1 while the baseline holds an actor; read by the T3-1, HV-1 and HV-2 runners' scene checks.</summary>
    internal int LiveCount => baseline.LiveCount;

    /// <summary>Game thread, every framework update: the pending step first, then the draw steps.</summary>
    internal void Tick()
    {
        if (shutDown)
            return;
        string? step;
        lock (gate)
        {
            step = pending;
            pending = null;
        }
        switch (step)
        {
            case "spawn":
                baseline.Spawn();
                break;
            case "remove":
                baseline.Remove();
                break;
            case "status":
                log(baseline.Status());
                break;
        }
        baseline.OnFrameworkUpdate();
    }

    /// <summary>
    /// Plugin unload: the command first, then the Glamourer revert, the
    /// collection and the actor (in that order, by the baseline), on the game
    /// thread within a bounded wait; then the plugin-list subscription.
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
            Task unload;
            if (framework.IsInFrameworkUpdateThread)
            {
                baseline.Unload();
                unload = Task.CompletedTask;
            }
            else
            {
                unload = framework.RunOnFrameworkThread(new Action(baseline.Unload));
            }
            if (!unload.Wait(UnloadWait))
                log("RSC HV-3 unload did not finish within 5 s; any actor, collection or Glamourer state left is residue; CLEANUP FAILURE.");
        }
        catch (Exception exception)
        {
            log($"RSC HV-3 unload failed ({exception.GetType().Name}); any actor, collection or Glamourer state left is residue; CLEANUP FAILURE.");
        }
        finally
        {
            if (subscribed)
                pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
            subscribed = false;
        }
    }

    /// <summary>Any plugin load or unload moves the generation; nothing else happens here.</summary>
    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => Interlocked.Increment(ref generation);

    private void OnCommand(string command, string arguments)
    {
        if (shutDown)
            return;
        var step = arguments.Trim();
        if (!Steps.Contains(step, StringComparer.Ordinal))
        {
            log($"RSC HV-3 unknown step; use {Command} " + string.Join(", ", Steps) + ".");
            return;
        }
        lock (gate)
        {
            if (pending is not null)
            {
                log("RSC HV-3 refused: a step is already waiting for the next update.");
                return;
            }
            pending = step;
        }
    }

    /// <summary>Null while the scene is quiet; otherwise why not. Total.</summary>
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
            if (otherRscActors() != 0)
                return "another RSC actor or puppet is in the scene";
            if (serverSessionPossible())
                return "an RSC server session is active or may start";
            return null;
        }
        catch (Exception exception)
        {
            return $"the scene could not be read ({exception.GetType().Name})";
        }
    }
}
#endif
