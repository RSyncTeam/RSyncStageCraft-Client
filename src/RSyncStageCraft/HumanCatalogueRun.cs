#if RSC_DEVELOPMENT
using System.Diagnostics;
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
/// No Customize+ profile is read.</item>
/// <item>HV-5 M3: <c>capture &lt;label&gt; with-customize+</c>: the same, plus
/// C8, the normal Customize+ profile active on the owner's character (owner
/// decision D2), read only (<see cref="HumanCatalogueProfileReader"/>), and
/// stored in a new entry of format <c>hv5-local-2</c>.</item>
/// <item><c>list</c>: the catalogue's entries, by label and short id, with
/// counts, completeness and versions; no path.</item>
/// <item>HV-5 M2: <c>spawn &lt;label&gt;</c>, <c>spawn-test-scale &lt;label&gt;</c>, <c>remove</c> and
/// <c>status</c>, run by <see cref="HumanCatalogueSpawnRun"/>: one actor from
/// one entry, which is read and never modified.</item>
/// </list>
/// Capture writes nothing to the game; nothing is uploaded, exported or
/// synchronized. The reads run on the framework thread, one framework call
/// each; hashing and copying run off it. At load the retention journal is
/// read, with the same-process marker: either, for this game process, refuses
/// spawn and capture until the game restarts; a journal of an earlier process
/// is cleared. Unload, the capture's step included, stays within one 5-second
/// bound.
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
    private readonly HumanActorCustomizePlusIpc customizePlus;
    private readonly Func<int> otherRscActors;
    private readonly HumanCatalogueSpawnRun spawn;
    private readonly string? journalRefusal;
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
        IDataManager data,
        IPlayerState playerState,
        string configDirectory,
        Func<int> otherRscActors,
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
        this.otherRscActors = otherRscActors ?? throw new ArgumentNullException(nameof(otherRscActors));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        catalogueRoot = Path.Combine(configDirectory, CatalogueFolder);
        var process = CurrentProcess();
        // Amendment 1 §3: holds kept by an earlier load of this game process refuse spawn and capture until the game restarts.
        // Item 7: the same-process marker refuses on its own, so a reload is refused even when both journal writes failed.
        var marked = HumanCatalogueRetentionMarker.IsSet(process, out var markedCount);
        journalRefusal = HumanCatalogueJournal.AtLoad(catalogueRoot, process, out var listed) switch
        {
            HumanCatalogueJournalState.SameProcess =>
                $"an earlier load of this game process kept {listed} stored file(s) held after an unconfirmed cleanup; spawn and capture are refused until the game restarts",
            HumanCatalogueJournalState.Unreadable =>
                "the retention journal could not be read or cleared; spawn and capture are refused (remove it by hand only after the game has restarted)",
            _ when marked =>
                $"an earlier load of this game process recorded {markedCount} stored file(s) held after an unconfirmed cleanup (same-process marker); " +
                "spawn and capture are refused until the game restarts",
            _ => null,
        };
        appearance = new DalamudAppearanceIpc(pluginInterface);
        locator = new DalamudAppearancePlayerLocator(objects);
        penumbra = new HumanActorPenumbraIpc(pluginInterface);
        // The generation is unused here: HV-5 M1 only reads the player's state.
        glamourer = new HumanActorGlamourerIpc(pluginInterface, () => 0);
        // HV-5 M3: the capture's Customize+ reads only (the gate, the active profile and its content, the fingerprint); the generation is unused here.
        customizePlus = new HumanActorCustomizePlusIpc(pluginInterface, () => 0);
        spawn = new HumanCatalogueSpawnRun(pluginInterface, framework, data, playerState, SpawnSceneExit, Store, CurrentVersions, catalogueRoot,
            process, log);
        commandAdded = commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "HV-5 step, from the Dalamud log window's console only.",
            ShowInHelp = false,
        });
        log(commandAdded
            ? $"RSC HV-5 runner loaded. Steps, typed only in the Dalamud log window's console: {Command} capture <label>, " +
                $"{Command} capture <label> with-customize+, {Command} list, " +
                $"{Command} spawn <label>, {Command} spawn-test-scale <label>, {Command} remove, {Command} status."
            : "RSC HV-5 runner loaded without its command; no step can run.");
        if (journalRefusal is not null)
            log("RSC HV-5: " + journalRefusal + ".");
        // Last: the spawn runner's plugin-list subscription, so a constructor that throws leaves none behind.
        spawn.Subscribe();
    }

    /// <summary>1 while HV-5's actor exists; read by the T3-1 and HV-1 to HV-4 runners' scene checks.</summary>
    internal int LiveCount => spawn.LiveCount;

    /// <summary>Game thread, every framework update: the pending step, if any, then the spawn's draw steps.</summary>
    internal void Tick()
    {
        if (shutDown)
            return;
        string? step;
        bool stepRunning;
        lock (gate)
        {
            step = pending;
            pending = null;
            stepRunning = running is { IsCompleted: false };
        }
        if (step is not null)
            Run(step, stepRunning);
        spawn.Tick();
    }

    /// <summary>Game thread: one step. <c>remove</c> and <c>status</c> always run; the others are refused while anything blocks them.</summary>
    private void Run(string chosen, bool stepRunning)
    {
        if (chosen is "remove" or "status")
        {
            spawn.Step(chosen);
            return;
        }
        if (stepRunning)
        {
            log("RSC HV-5 refused: a step is still running.");
            return;
        }
        if (journalRefusal is not null && chosen != "list")
        {
            log($"RSC HV-5 refused: {journalRefusal}.");
            return;
        }
        var spawnStep = HumanCatalogueSpawnCommand.TryParse(chosen, out _, out _);
        if (HumanCatalogueCaptureCommand.TryParse(chosen, out _, out _) && spawn.Busy)
        {
            log("RSC HV-5 refused: a spawn is being prepared, an actor exists, or stored files are held; capture after the removal.");
            return;
        }
        if ((spawnStep ? SpawnSceneExit() : SceneExit()) is { } exit)
        {
            log($"RSC HV-5 refused: {exit}.");
            return;
        }
        if (spawnStep)
        {
            spawn.Step(chosen);
            return;
        }
        var token = stopping.Token;
        var task = chosen == "list"
            ? Task.Run(() => List(), token)
            : Task.Run(() => CaptureAsync(chosen, token), token);
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
    /// First HV-5 M2's spawn is unloaded (<see cref="HumanCatalogueSpawnRun.Shutdown"/>), then the step is waited for with what is left of the one 5 s bound.
    /// </summary>
    internal void Shutdown()
    {
        if (shutDown)
            return;
        shutDown = true;
        if (commandAdded)
            commands.RemoveHandler(Command);
        stopping.Cancel();
        // One bound for the whole unload: the spawn's cleanup first (it changes the game), then the capture's step with what is left.
        var clock = Stopwatch.StartNew();
        TimeSpan Remaining() => UnloadWait - clock.Elapsed;
        spawn.Shutdown(Remaining);
        Task? task;
        lock (gate)
            task = running;
        if (task is not null && !framework.IsInFrameworkUpdateThread)
        {
            try
            {
                var left = Remaining();
                if (!task.IsCompleted && (left <= TimeSpan.Zero || !task.Wait(left)))
                    log("RSC HV-5 unload did not finish its step within the 5 s bound; a leftover temporary file is removed at the next start.");
            }
            catch (AggregateException)
            {
                // Cancelled or failed: reported by the step itself where it could be.
            }
        }
    }

    private void OnCommand(string command, string arguments)
    {
        if (shutDown)
            return;
        var text = arguments.Trim();
        string step;
        if (text is "list" or "remove" or "status")
            step = text;
        else if (HumanCatalogueCaptureCommand.TryParse(text, out _, out _) || HumanCatalogueSpawnCommand.TryParse(text, out _, out _))
            step = text;
        else
        {
            log($"RSC HV-5 unknown step; use {Command} capture <label>, {Command} capture <label> with-customize+, {Command} list, {Command} spawn <label>, " +
                $"{Command} spawn-test-scale <label>, {Command} remove, or {Command} status.");
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
                    $"Customize+ {entry.Versions.CustomizePlus ?? "not installed"}" +
                    // HV-5 M3: only an entry with a profile gets this; an entry without one keeps its line exactly as before.
                    (entry.ProfileBones is { } bones ? $", profile owner-approved ({bones} bone(s))." : "."));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The type only: an exception message may name a path.
            Say($"RSC HV-5 list failed ({exception.GetType().Name}).");
        }
    }

    private async Task CaptureAsync(string step, CancellationToken cancellationToken)
    {
        try
        {
            HumanCatalogueCaptureCommand.TryParse(step, out var label, out var withProfile);
            var result = await HumanCatalogueCapture.CaptureAsync(label, Store(), token => ReadAsync(withProfile, token), () => DateTimeOffset.UtcNow,
                cancellationToken, withProfile).ConfigureAwait(false);
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
    private async Task<HumanCatalogueReadResult> ReadAsync(bool withProfile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shutDown)
            return HumanCatalogueReadResult.Refused("the plugin is unloading");
        return await framework.RunOnFrameworkThread<HumanCatalogueReadResult>(() => ReadOnFramework(withProfile)).ConfigureAwait(false);
    }

    /// <summary>
    /// Framework thread. The player is re-checked as the exact same object
    /// before and after every read; any change refuses the read. Nothing is
    /// written to the game or to any plugin. HV-5 M3: with
    /// <paramref name="withProfile"/>, the Customize+ profile is read last, in
    /// the same call, with the skeleton's bone names (memory only).
    /// </summary>
    private HumanCatalogueReadResult ReadOnFramework(bool withProfile)
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
            HumanCatalogueProfileRead? profile = null;
            if (withProfile)
            {
                // D2 and D-M3-2: the active normal profile on this object, and only its content; the bone names classify it and are dropped.
                var bones = HumanPuppetNativeBackend.SkeletonBoneNames(player.Address);
                if (!locator.IsStill(player))
                    return HumanCatalogueReadResult.Refused(changed);
                var (profileRead, refusal) = HumanCatalogueProfileReader.Read(customizePlus, player.ObjectIndex, bones, summary);
                if (profileRead is null)
                    return HumanCatalogueReadResult.Refused(refusal ?? "Customize+ profile: not read");
                if (!locator.IsStill(player))
                    return HumanCatalogueReadResult.Refused(changed);
                profile = profileRead;
            }
            var versions = CurrentVersions();
            return new HumanCatalogueReadResult(new HumanCatalogueRead(modDirectory, resources, state, reference, manipulations, race, bodyType,
                summary, versions, $"{player.ObjectIndex}:{player.Address}:{player.GameObjectId}", profile), null);
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

    /// <summary>The versions now, as the capture records them (C9): game, Penumbra, Glamourer and Customize+ (read only).</summary>
    private HumanCatalogueVersions CurrentVersions() =>
        new(DalamudGameVersion.Read(), LoadedVersion("Penumbra"), LoadedVersion("Glamourer"), LoadedVersion("CustomizePlus"));

    /// <summary>The game process: its id and start time, for the retention journal.</summary>
    private static HumanCatalogueProcess CurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        return new HumanCatalogueProcess(process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    /// <summary>HV-5 M2: a capture's scene checks, and no other RSC actor or puppet. Total.</summary>
    private string? SpawnSceneExit()
    {
        if (SceneExit() is { } exit)
            return exit;
        try
        {
            return otherRscActors() != 0 ? "another RSC actor or puppet is in the scene" : null;
        }
        catch (Exception exception)
        {
            return $"the scene could not be read ({exception.GetType().Name})";
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
