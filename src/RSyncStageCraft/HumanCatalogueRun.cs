#if RSC_DEVELOPMENT
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft;

/// <summary>
/// HV-5 M1 only: the local human catalogue's capture and listing. It exists
/// only in a Debug build (RSC_DEVELOPMENT) loaded as a dev plugin, and its
/// only trigger is one command typed in the Dalamud log window's console.
/// <list type="bullet">
/// <item><c>capture &lt;label&gt;</c>: reads the owner's own character, read
/// only (capture reads C1 to C7 and C9), stores its files and one immutable
/// entry in the local catalogue, and re-checks the character afterwards.
/// No Customize+ profile is read (C8 is M3, decision D2).</item>
/// <item><c>list</c>: the catalogue's entries, by label and short id, with
/// counts, completeness and versions; no path.</item>
/// </list>
/// Nothing is spawned, applied, written to the game, uploaded, exported or
/// synchronized. The reads run on the framework thread, one framework call
/// each; hashing and copying run off it.
/// </summary>
internal sealed class HumanCatalogueRun
{
    /// <summary>Not a game command; registered only here, with no help listing.</summary>
    internal const string Command = "/rschv5";

    internal const string LogPrefix = "RSC HV-5 ";

    internal const string CatalogueFolder = "hv5-catalogue";

    private static readonly TimeSpan UnloadWait = TimeSpan.FromSeconds(5);

    private static readonly string[] PluginNames = ["Penumbra", "Glamourer", "CustomizePlus"];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IObjectTable objects;
    private readonly Func<bool> serverSessionPossible;
    private readonly Action<string> log;
    private readonly string catalogueRoot;
    private readonly DalamudAppearanceIpc appearance;
    private readonly DalamudAppearancePlayerLocator locator;
    private readonly HumanActorPenumbraIpc penumbra;
    private readonly HumanActorGlamourerIpc glamourer;
    private readonly bool commandAdded;
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private string? pending;
    private Task? running;
    private HumanCatalogueStore? store;
    private volatile bool shutDown;

    internal HumanCatalogueRun(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IObjectTable objects,
        string configDirectory,
        Func<bool> serverSessionPossible,
        Action<string> log)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        this.condition = condition ?? throw new ArgumentNullException(nameof(condition));
        this.objects = objects ?? throw new ArgumentNullException(nameof(objects));
        this.serverSessionPossible = serverSessionPossible ?? throw new ArgumentNullException(nameof(serverSessionPossible));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        catalogueRoot = Path.Combine(configDirectory, CatalogueFolder);
        appearance = new DalamudAppearanceIpc(pluginInterface);
        locator = new DalamudAppearancePlayerLocator(objects);
        penumbra = new HumanActorPenumbraIpc(pluginInterface);
        // The generation is unused here: HV-5 M1 only reads the player's state.
        glamourer = new HumanActorGlamourerIpc(pluginInterface, () => 0);
        commandAdded = commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "HV-5 step, from the Dalamud log window's console only.",
            ShowInHelp = false,
        });
        log(commandAdded
            ? $"RSC HV-5 runner loaded. Steps, typed only in the Dalamud log window's console: {Command} capture <label>, {Command} list."
            : "RSC HV-5 runner loaded without its command; no step can run.");
    }

    /// <summary>Game thread, every framework update: starts the pending step, if any.</summary>
    internal void Tick()
    {
        if (shutDown)
            return;
        string? step;
        lock (gate)
        {
            step = pending;
            pending = null;
            if (step is null)
                return;
            if (running is { IsCompleted: false })
            {
                log("RSC HV-5 refused: a step is still running.");
                return;
            }
        }
        string chosen = step;
        if (SceneExit() is { } exit)
        {
            log($"RSC HV-5 refused: {exit}.");
            return;
        }
        var token = stopping.Token;
        var task = chosen == "list"
            ? Task.Run(() => List(), token)
            : Task.Run(() => CaptureAsync(chosen["capture ".Length..], token), token);
        lock (gate)
            running = task;
    }

    /// <summary>
    /// Plugin unload: the command first, then any running step is cancelled.
    /// Off the framework thread it is waited for within a bounded time; on
    /// the framework thread it is not waited for (its next read would need
    /// that thread), and it ends through the cancellation and the unloading
    /// checks. A cancelled copy leaves no partial file, an entry is written
    /// whole or not at all, and nothing is logged once unloading has begun.
    /// </summary>
    internal void Shutdown()
    {
        if (shutDown)
            return;
        shutDown = true;
        if (commandAdded)
            commands.RemoveHandler(Command);
        stopping.Cancel();
        Task? task;
        lock (gate)
            task = running;
        if (task is null || framework.IsInFrameworkUpdateThread)
            return;
        try
        {
            if (!task.Wait(UnloadWait))
                log("RSC HV-5 unload did not finish its step within 5 s; a leftover temporary file is removed at the next start.");
        }
        catch (AggregateException)
        {
            // Cancelled or failed: reported by the step itself where it could be.
        }
    }

    private void OnCommand(string command, string arguments)
    {
        if (shutDown)
            return;
        var text = arguments.Trim();
        string step;
        if (text == "list")
            step = "list";
        else if (text.StartsWith("capture ", StringComparison.Ordinal) && text.Length > "capture ".Length)
            step = text;
        else
        {
            log($"RSC HV-5 unknown step; use {Command} capture <label>, or {Command} list.");
            return;
        }
        lock (gate)
        {
            if (pending is not null)
            {
                log("RSC HV-5 refused: a step is already waiting for the next update.");
                return;
            }
            pending = step;
        }
    }

    private HumanCatalogueStore Store()
    {
        lock (gate)
            return store ??= new HumanCatalogueStore(catalogueRoot);
    }

    /// <summary>Logs a line unless unloading has begun.</summary>
    private void Say(string line)
    {
        if (!shutDown)
            log(line);
    }

    private void List()
    {
        try
        {
            var list = Store().List();
            Say($"RSC HV-5 catalogue: {list.Entries.Count} entr{(list.Entries.Count == 1 ? "y" : "ies")}, {list.UnreadableEntries} unreadable, " +
                $"{list.OrphanFiles} unreferenced file(s), {list.UsedBytes} of {list.LimitBytes} byte(s) used.");
            foreach (var entry in list.Entries)
            {
                Say($"RSC HV-5 \"{entry.Label}\" {entry.ShortId}: captured {entry.CapturedUtc:u}, {entry.GamePaths} game path(s), {entry.Files} file(s), " +
                    $"{entry.Bytes} byte(s), {entry.Outside} outside the mod folder left out, {(entry.Complete ? "complete" : "INCOMPLETE")}, " +
                    (entry.Skeleton is { } group ? $"skeleton c{group.RaceCode:D4} ({group.Members.Count}, deformer {(group.Deformer ? "yes" : "no")})" : "no skeleton group") +
                    $", game {entry.Versions.Game ?? "unknown"}, Penumbra {entry.Versions.Penumbra ?? "unknown"}, Glamourer {entry.Versions.Glamourer ?? "unknown"}, " +
                    $"Customize+ {entry.Versions.CustomizePlus ?? "not installed"}.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The type only: an exception message may name a path.
            Say($"RSC HV-5 list failed ({exception.GetType().Name}).");
        }
    }

    private async Task CaptureAsync(string label, CancellationToken cancellationToken)
    {
        try
        {
            var result = await HumanCatalogueCapture.CaptureAsync(label, Store(), ReadAsync, () => DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            if (result.Entry is null)
            {
                Say($"RSC HV-5 capture refused: {result.Refusal}. No entry was written.");
                return;
            }
            foreach (var line in result.Lines)
                Say(LogPrefix + line);
        }
        catch (OperationCanceledException)
        {
            Say("RSC HV-5 capture cancelled; no entry was written.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The type only: an exception message may name a path.
            Say($"RSC HV-5 capture failed ({exception.GetType().Name}). No entry was written.");
        }
    }

    /// <summary>One read, on the framework thread, in one framework call.</summary>
    private async Task<HumanCatalogueReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shutDown)
            return HumanCatalogueReadResult.Refused("the plugin is unloading");
        return await framework.RunOnFrameworkThread<HumanCatalogueReadResult>(ReadOnFramework).ConfigureAwait(false);
    }

    /// <summary>
    /// Framework thread. The player is re-checked as the exact same object
    /// before and after every read; any change refuses the read. Nothing is
    /// written to the game or to any plugin.
    /// </summary>
    private HumanCatalogueReadResult ReadOnFramework()
    {
        if (shutDown)
            return HumanCatalogueReadResult.Refused("the plugin is unloading");
        try
        {
            if (SceneExit() is { } exit)
                return HumanCatalogueReadResult.Refused(exit);
            if (appearance.PenumbraState() != AppearanceApiState.Ready)
                return HumanCatalogueReadResult.Refused("Penumbra is not available");
            if (appearance.GlamourerState() != AppearanceApiState.Ready)
                return HumanCatalogueReadResult.Refused("Glamourer is not available");
            if (locator.LocalPlayer() is not { } player)
                return HumanCatalogueReadResult.Refused("the player was not found");
            const string changed = "the player changed during the read";

            var modDirectory = appearance.GetModDirectory();
            if (!locator.IsStill(player))
                return HumanCatalogueReadResult.Refused(changed);
            if (appearance.GetGameObjectResourcePaths([player.ObjectIndex]) is not { Length: 1 } maps || maps[0] is not { } map)
                return HumanCatalogueReadResult.Refused("Penumbra's resource paths could not be read");
            var resources = new List<AppearanceResourceEntry>(map.Count);
            foreach (var (actual, gamePaths) in map)
            {
                if (actual is null || gamePaths is null)
                    return HumanCatalogueReadResult.Refused("Penumbra reported an incomplete resource entry");
                resources.Add(new AppearanceResourceEntry(actual, gamePaths.ToArray()));
            }
            if (!locator.IsStill(player))
                return HumanCatalogueReadResult.Refused(changed);
            if (penumbra.MetaManipulations(player.ObjectIndex) is not { } manipulations)
                return HumanCatalogueReadResult.Refused("Penumbra's manipulations could not be read");
            if (manipulations.Length > HumanCatalogueEntryCodec.MaximumEntryBytes / 4)
                return HumanCatalogueReadResult.Refused("Penumbra's manipulations are too large to store");
            if (!locator.IsStill(player))
                return HumanCatalogueReadResult.Refused(changed);
            var (stateCode, state) = appearance.GetStateBase64(player.ObjectIndex);
            if (stateCode != GameAppearanceCaptureReader.GlamourerSuccess || state is null ||
                state.Length > GameAppearanceCaptureReader.MaximumGlamourerTextLength)
                return HumanCatalogueReadResult.Refused($"Glamourer's state could not be read (code {stateCode})");
            if (!locator.IsStill(player))
                return HumanCatalogueReadResult.Refused(changed);
            var (referenceCode, reference) = glamourer.StateJson(player.ObjectIndex);
            if (referenceCode != GameAppearanceCaptureReader.GlamourerSuccess || reference is null ||
                reference.Length > HumanCatalogueEntryCodec.MaximumEntryBytes / 4)
                return HumanCatalogueReadResult.Refused($"Glamourer's structured state could not be read (code {referenceCode})");
            if (!locator.IsStill(player))
                return HumanCatalogueReadResult.Refused(changed);
            // C5: the race, clan and gender bytes and the body-type byte only; nothing else is decoded.
            if (locator.CharacterRace(player) is not { } race || objects.LocalPlayer is not { } local ||
                local.Customize.Length <= (int)CustomizeIndex.ModelType)
                return HumanCatalogueReadResult.Refused("the character's race and body could not be read");
            // Customize index 2 (Dalamud's ModelType; the body type).
            var bodyType = local.Customize[(int)CustomizeIndex.ModelType];
            // The player's own skeleton summary, as T3-1 reads it, without its
            // hash of the resource file name (which can be a local path): bone
            // counts and hashes of the names, parents and reference pose only.
            var summary = HumanPuppetNativeBackend.SkeletonSummary(player.Address, "player")
                .Select(fact => (fact.Key.StartsWith("player ", StringComparison.Ordinal) ? fact.Key["player ".Length..] : fact.Key) + ": " +
                    HumanCatalogueEntryCodec.WithoutFileHash(fact.Value))
                .ToList();
            if (!locator.IsStill(player))
                return HumanCatalogueReadResult.Refused(changed);
            var versions = new HumanCatalogueVersions(DalamudGameVersion.Read(), LoadedVersion("Penumbra"), LoadedVersion("Glamourer"),
                LoadedVersion("CustomizePlus"));
            return new HumanCatalogueReadResult(new HumanCatalogueRead(modDirectory, resources, state, reference, manipulations, race, bodyType,
                summary, versions, $"{player.ObjectIndex}:{player.Address}:{player.GameObjectId}"), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A provider error (gone, type mismatch, its own fault): a reason only; the exception, which may name a path, is dropped.
            return HumanCatalogueReadResult.Refused($"a read failed ({exception.GetType().Name})");
        }
    }

    /// <summary>The loaded plugin's manifest version (Dalamud's plugin list, read only), or null.</summary>
    private string? LoadedVersion(string internalName)
    {
        if (!PluginNames.Contains(internalName, StringComparer.Ordinal))
            return null;
        try
        {
            return HumanCatalogueVersions.Bound(pluginInterface.InstalledPlugins
                .Take(DalamudOptionalHostMetadataProbe.MaximumInstalledPlugins)
                .FirstOrDefault(plugin => plugin.IsLoaded && string.Equals(plugin.InternalName, internalName, StringComparison.Ordinal))
                ?.Version.ToString());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Null while a capture may read; otherwise why not. Total.</summary>
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
