using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Dependencies;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Lights;
using RSyncStageCraft.Core.Location;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft;

/// <summary>
/// Owns the plugin's connection to the RSC server: settings, the protected
/// access key, the client runtime, the connection service and the furniture
/// host that draws server furniture as client-only objects. Its only network
/// peer is the configured RSC endpoint.
/// </summary>
internal sealed class PluginConnection
{
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ProtectedAccessKeyStore keyStore;
    private readonly DalamudLocationProvider location;
    private readonly IFramework framework;
    private readonly UnloadAwareDispatcher dispatcher;
    private readonly SceneChangeWatcher scenes;
    private readonly Func<ActorSuppression> actorsSuppressed;
    private readonly ITargetManager targets;
    private readonly Action<string> log;
    private volatile bool appearanceStopped;
    // Written on the game thread: GPose or a cutscene was active at the last frame.
    private volatile bool actorsWereSuppressed;
    private readonly object gate = new();
    private string accessKey;
#if RSC_DEVELOPMENT
    // T3-1 only: null unless a Debug build is loaded as a dev plugin.
    private readonly HumanPuppetT3Run? t31;
    // HV-1 only: null unless a Debug build is loaded as a dev plugin.
    private readonly HumanActorBaselineRun? hv1;
    // HV-2 only: null unless a Debug build is loaded as a dev plugin.
    private readonly HumanActorPenumbraRun? hv2;

    private readonly HumanActorGlamourerRun? hv3;
    // HV-4 only: null unless a Debug build is loaded as a dev plugin.
    private readonly HumanActorCustomizePlusRun? hv4;
#endif

    internal PluginConnection(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        DependencyRegistry dependencies,
        DalamudPluginDataPaths paths,
        DalamudLocationProvider location,
        IDataManager data,
        IClientState clientState,
        ICondition condition,
        ITargetManager targets,
        IObjectTable objects,
#if RSC_DEVELOPMENT
        ICommandManager commands,
#endif
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(pluginInterface);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clientState);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(log);
#if RSC_DEVELOPMENT
        ArgumentNullException.ThrowIfNull(commands);
#endif
        this.targets = targets;
        // Entering GPose or a cutscene is treated like leaving the place for
        // actors only: they are freed and drawn again on return.
        actorsSuppressed = () => new ActorSuppression(
            clientState.IsGPosing,
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent],
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene],
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene78]);
        this.pluginInterface = pluginInterface;
        this.location = location;
        this.framework = framework;
        this.log = log;
        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        keyStore = new ProtectedAccessKeyStore(paths.ConfigDirectory);
        accessKey = keyStore.Load() ?? string.Empty;

        dispatcher = new UnloadAwareDispatcher(new DalamudGameThreadDispatcher(framework));
        // Only a change of the normalized scene location wipes and respawns
        // furniture; walking across plots outdoors does not.
        scenes = new SceneChangeWatcher(OnSceneChanged);
        // Furniture, lights and protocol 6 base-game actors are drawn by the
        // local-object host below.
        Resolver = new FurnitureSupportResolver(LoadCatalog(data, log), new DalamudFurnitureAssets(data));
        Stains = LoadStains(data, log);
        Furniture = new FurnitureLocalObjectHost(Resolver, new BgObjectFurnitureBackend(), Stains);
        // Actors: base-game monsters and demihumans, resolved from the
        // player's own game data; BNpcBaseId is the only identity sent.
        var actorData = new DalamudActorData(data);
        ActorResolver = new ActorAppearanceResolver(actorData);
        ActorCatalog = LoadActorCatalog(actorData, ActorResolver, log);
        var actorBackend = new BattleCharaActorBackend();
        // Controls (typed ClientStructs members only). Until a
        // path passes its in-game check (ActorControlVerification.Current),
        // synchronized actors asking for it are not drawn.
        // Animation: only the curated, game-version-pinned allowlist; a
        // different game version disables animation only.
        GameVersion = DalamudGameVersion.Read();
        // The production composition (verified paths, shipped allowlist);
        // development diagnostics exist only in a Debug build loaded as a dev
        // plugin: a Release build never enables them.
        var actors = ActorHostComposition.Create(actorBackend, ActorResolver.Resolve,
            new TimelineActorControlBackend(), actorData, GameVersion,
            developmentTools: DevelopmentBuild.ToolsEnabled(pluginInterface.IsDev));
        // Lights and actors have their own hosts and native backends; the
        // composite host only dispatches by object kind.
        Objects = new LocalObjectHost(Furniture, new LightLocalObjectHost(new SceneLightBackend()), actors);
        Runtime = new RSyncStageCraftRuntime(
            dispatcher,
            Objects,
            // The longest allowed request-ID budget per connection: at one
            // poll a second that is several hours before a fresh connection.
            controlSendLimits: ClientControlSendLimits.Default with
            {
                Requests = new(
                    ClientControlRequestLimits.Default.MaximumPending,
                    ClientControlRequestLimits.MaximumRetainedIdsHardLimit),
            },
            localObjectVisibility: new ActorSuppressingVisibility(
                new CurrentLocationVisibility(() => scenes.Current),
                () => actorsWereSuppressed));
        var health = new DependencyHealthMonitor(dependencies);
        Service = new ServerConnectionService(
            Runtime,
            CurrentSettings,
            token => dispatcher.IsUnloading
                ? Task.FromResult(default(Core.Receiver.ReceiverDependencySnapshot))
                : dispatcher
                .InvokeAsync(() => ConnectionDependencies.ForCurrentPlace(
                    health.Snapshot(),
                    // Scene objects and actors are drawn only where the place is fully known.
                    sceneKnown: scenes.Current is not null), token)
                .WaitAsync(token),
            log: log);
        Editor = new FurnitureEditor((request, token) => Service.SubmitAsync(request, token));
        Gizmo = new FurnitureGizmoController(
            Objects,
            Editor,
            () => Service.SessionSerial,
            objectId => Runtime.DesiredLocalObjects()
                .FirstOrDefault(state => state.ObjectId == objectId));
        // A native fault retired an instance: release its receipt and respawn
        // it. An actor fault redraws actors only (furniture and lights keep theirs).
        Objects.Furniture.InstanceRetired += Runtime.ResetLocalObjects;
        Objects.Lights.InstanceRetired += Runtime.ResetLocalObjects;
        actors.InstanceRetired += () => Runtime.ResetLocalObjects(RscObjectKind.Actor);
        // An actor slot freed up while another was refused for the actor
        // limit: draw the actors again so the refused one gets its turn.
        actors.CapacityFreed += () => Runtime.ResetLocalObjects(RscObjectKind.Actor);
        // Appearances: the owner's capture and catalogue over HTTPS to the
        // RSC server. Capture only reads, through the approved read-only
        // Penumbra and Glamourer calls on the framework thread.
        // If the disposable cache folder cannot be prepared, only appearances
        // are unavailable; the rest of the plugin loads.
        try
        {
            var appearanceCache = new AppearanceBlobCache(paths.AppearanceCacheDirectory);
            var appearanceTransfer = new AppearanceTransferClient(CurrentSettings, appearanceCache);
            // Skeleton groups: a Debug build loaded as a dev plugin only, and
            // preview only. DevelopmentBuild.Enabled is a constant false in
            // Release, so a Release build never captures one.
            var skeletonGroups = DevelopmentBuild.ToolsEnabled(pluginInterface.IsDev);
            var captureOptions = new AppearanceCaptureOptions(AllowSkeletonGroups: skeletonGroups, AllowDeformer: skeletonGroups);
            Appearance = new AppearanceAuthoring(
                new GameAppearanceCaptureReader(dispatcher, new DalamudAppearancePlayerLocator(objects), new DalamudAppearanceIpc(pluginInterface),
                    () => dispatcher.IsUnloading, captureOptions.AllowSkeletonGroups),
                appearanceTransfer,
                () => Service.SelfUserId,
                appearanceCache.TemporaryDirectory,
                captureOptions);
            // "Make a Copy": a local Glamourer design from another member's
            // published entry; only Glamourer.AddDesign and the read-only
            // design list are bound.
            AppearanceCopy = new AppearanceCopier(
                appearanceTransfer, dispatcher, new DalamudGlamourerDesignIpc(pluginInterface), () => Service.SelfUserId,
                () => dispatcher.IsUnloading || appearanceStopped, log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Appearance = null;
            AppearanceCopy = null;
            log("RSC appearances are unavailable: the appearance cache folder could not be prepared.");
        }
#if RSC_DEVELOPMENT
        // T3-1 only (R-1, R-1b): a Debug build loaded as a dev plugin composes
        // the human puppet runner here and nowhere else; Release has none of it.
        if (DevelopmentBuild.ToolsEnabled(pluginInterface.IsDev))
        {
            try
            {
                t31 = new HumanPuppetT3Run(pluginInterface, commands, framework, clientState, condition, data,
                    () => actors.OwnedCount + (int)Math.Min(actors.AbandonedCount, int.MaxValue - ActorCapacityPolicy.ActiveMaximumActorsPerScene) +
                        (hv1?.LiveCount ?? 0) + (hv2?.LiveCount ?? 0) + (hv3?.LiveCount ?? 0) + (hv4?.LiveCount ?? 0),
                    () => Service.Status.State is not (ServerConnectionState.Disabled or ServerConnectionState.NotConfigured
                        or ServerConnectionState.SignInRefused or ServerConnectionState.Stopped),
                    () => scenes.Current,
                    log);
            }
            catch (Exception exception)
            {
                t31 = null;
                log($"RSC T3-1 runner not loaded ({exception.GetType().Name}).");
            }
            // HV-1: one visible client-only human, refused while any other RSC actor or T3-1 puppet exists.
            try
            {
                hv1 = new HumanActorBaselineRun(commands, framework, clientState, condition,
                    () => actors.OwnedCount + (int)Math.Min(actors.AbandonedCount, int.MaxValue - ActorCapacityPolicy.ActiveMaximumActorsPerScene) +
                        (t31?.OccupiedSlots ?? 0) + (hv2?.LiveCount ?? 0) + (hv3?.LiveCount ?? 0) + (hv4?.LiveCount ?? 0),
                    () => Service.Status.State is not (ServerConnectionState.Disabled or ServerConnectionState.NotConfigured
                        or ServerConnectionState.SignInRefused or ServerConnectionState.Stopped),
                    log);
            }
            catch (Exception exception)
            {
                hv1 = null;
                log($"RSC HV-1 runner not loaded ({exception.GetType().Name}).");
            }
            // HV-2: HV-1's human with an actor-specific Penumbra temporary collection, under the same refusals.
            try
            {
                hv2 = new HumanActorPenumbraRun(pluginInterface, commands, framework, clientState, condition,
                    () => actors.OwnedCount + (int)Math.Min(actors.AbandonedCount, int.MaxValue - ActorCapacityPolicy.ActiveMaximumActorsPerScene) +
                        (t31?.OccupiedSlots ?? 0) + (hv1?.LiveCount ?? 0) + (hv3?.LiveCount ?? 0) + (hv4?.LiveCount ?? 0),
                    () => Service.Status.State is not (ServerConnectionState.Disabled or ServerConnectionState.NotConfigured
                        or ServerConnectionState.SignInRefused or ServerConnectionState.Stopped),
                    log);
            }
            catch (Exception exception)
            {
                hv2 = null;
                log($"RSC HV-2 runner not loaded ({exception.GetType().Name}).");
            }
            // HV-3: HV-2's human with one equipment-only Glamourer state, under the same refusals.
            try
            {
                hv3 = new HumanActorGlamourerRun(pluginInterface, commands, framework, clientState, condition,
                    () => actors.OwnedCount + (int)Math.Min(actors.AbandonedCount, int.MaxValue - ActorCapacityPolicy.ActiveMaximumActorsPerScene) +
                        (t31?.OccupiedSlots ?? 0) + (hv1?.LiveCount ?? 0) + (hv2?.LiveCount ?? 0) + (hv4?.LiveCount ?? 0),
                    () => Service.Status.State is not (ServerConnectionState.Disabled or ServerConnectionState.NotConfigured
                        or ServerConnectionState.SignInRefused or ServerConnectionState.Stopped),
                    log);
            }
            catch (Exception exception)
            {
                hv3 = null;
                log($"RSC HV-3 runner not loaded ({exception.GetType().Name}).");
            }
            // HV-4: HV-3's human with one Customize+ temporary profile, under the same refusals.
            try
            {
                hv4 = new HumanActorCustomizePlusRun(pluginInterface, commands, framework, clientState, condition,
                    () => actors.OwnedCount + (int)Math.Min(actors.AbandonedCount, int.MaxValue - ActorCapacityPolicy.ActiveMaximumActorsPerScene) +
                        (t31?.OccupiedSlots ?? 0) + (hv1?.LiveCount ?? 0) + (hv2?.LiveCount ?? 0) + (hv3?.LiveCount ?? 0),
                    () => Service.Status.State is not (ServerConnectionState.Disabled or ServerConnectionState.NotConfigured
                        or ServerConnectionState.SignInRefused or ServerConnectionState.Stopped),
                    log);
            }
            catch (Exception exception)
            {
                hv4 = null;
                log($"RSC HV-4 runner not loaded ({exception.GetType().Name}).");
            }
        }
#endif
        framework.Update += OnFrameworkUpdate;
        location.Changed += OnLocationChanged;
        scenes.Observe(location.Current?.Location);
        Service.Start();
    }

    internal Configuration Configuration { get; }
    internal FurnitureSupportResolver Resolver { get; }

    /// <summary>The local actor resolver: BNpcBase row to draw plan or not-drawn reason.</summary>
    internal ActorAppearanceResolver ActorResolver { get; }

    /// <summary>The creatures the player can place.</summary>
    internal ActorCatalog ActorCatalog { get; }

    /// <summary>The housing stains this client can draw (Default excluded).</summary>
    internal FurnitureStainPalette Stains { get; }
    internal FurnitureLocalObjectHost Furniture { get; }

    /// <summary>Furniture, lights and actors: the receiver's local-object host (dispatches by kind).</summary>
    internal LocalObjectHost Objects { get; }
    internal FurnitureEditor Editor { get; }
    internal FurnitureGizmoController Gizmo { get; }
    internal RSyncStageCraftRuntime Runtime { get; }
    internal ServerConnectionService Service { get; }

    /// <summary>The owner's appearance capture and catalogue.</summary>
    internal AppearanceAuthoring? Appearance { get; }

    /// <summary>"Make a Copy" as a local Glamourer design; null when appearances are unavailable.</summary>
    internal AppearanceCopier? AppearanceCopy { get; }

    internal bool HasStoredKey
    {
        get
        {
            lock (gate)
                return accessKey.Length != 0;
        }
    }

    /// <summary>
    /// Saves the address and, when <paramref name="newAccessKey"/> is not
    /// empty, replaces the stored key. Returns false if the key could not be
    /// stored securely (it is then kept for this session only).
    /// </summary>
    internal bool SaveSettings(string endpoint, string? newAccessKey)
    {
        var stored = true;
        lock (gate)
        {
            Configuration.ServerEndpoint = endpoint.Trim();
            if (!string.IsNullOrWhiteSpace(newAccessKey))
            {
                accessKey = newAccessKey.Trim();
                stored = keyStore.Save(accessKey);
            }
            pluginInterface.SavePluginConfig(Configuration);
        }
        Service.Wake();
        return stored;
    }

    /// <summary>
    /// Forgets the key for this session and deletes the saved copy. Returns
    /// false if the saved file could not be deleted (it would load again on
    /// the next start).
    /// </summary>
    internal bool ForgetKey()
    {
        bool deleted;
        lock (gate)
        {
            accessKey = string.Empty;
            deleted = keyStore.Save(null) && !keyStore.Exists;
        }
        Service.Wake();
        return deleted;
    }

    internal void SetEnabled(bool enabled)
    {
        lock (gate)
        {
            Configuration.ConnectEnabled = enabled;
            pluginInterface.SavePluginConfig(Configuration);
        }
        if (enabled)
            Service.Reconnect();
        else
            Service.Wake();
    }

    internal void Reconnect() => Service.Reconnect();

    internal void DependenciesChanged() => Service.Wake();

    /// <summary>
    /// "Use my target": reads the current target, never sets, clears or
    /// changes it. Accepts only a battle NPC whose nonzero BNpcBase row
    /// resolves to a supported monster or demihuman. Game thread (UI draw).
    /// </summary>
    internal ActorDrawPlanResult ReadTargetCreature()
    {
        var target = targets.Target;
        return ActorTargetSelection.FromTarget(
            target is not null,
            target?.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc,
            target?.BaseId ?? 0,
            ActorResolver);
    }

    internal void Shutdown()
    {
        // From here on no copy makes a Glamourer call, whatever the
        // object host's state (a queued framework callback checks this).
        appearanceStopped = true;
#if RSC_DEVELOPMENT
        // T3-1 (S2): its command first, then its puppet, before the framework update and the actor host stop.
        t31?.Shutdown();
        hv1?.Shutdown();
        hv2?.Shutdown();
        hv3?.Shutdown();
        hv4?.Shutdown();
#endif
        location.Changed -= OnLocationChanged;
        framework.Update -= OnFrameworkUpdate;
        // Unload ends any drag; freeing the furniture below ends its preview.
        Gizmo.Cancel();

        // First free every furniture object, on the framework thread, and
        // refuse spawns still queued, so nothing outlives the plugin.
        int ShutdownObjects() => Objects.Shutdown();
        int freed;
        if (framework.IsInFrameworkUpdateThread)
        {
            freed = ShutdownObjects();
        }
        else
        {
            var onFramework = framework.RunOnFrameworkThread<int>(ShutdownObjects);
            freed = onFramework.Wait(ShutdownWait) ? onFramework.Result : 0;
        }
        if (freed > 0)
            log($"RSC freed {freed} scene object(s) on unload.");
        ReportAbandonedActors();

        // Dalamud disposes on the framework thread, which the bounded wait
        // below blocks. The receiver's remaining cleanup only releases
        // receipts the closed host no longer holds, so it may run inline
        // instead of waiting for that thread; dependency probes stop.
        if (Objects.IsShutDown)
            dispatcher.RunInline();
        var stopping = Task.Run(async () =>
        {
            await Service.DisposeAsync().ConfigureAwait(false);
            await Runtime.ShutdownAsync().ConfigureAwait(false);
        });
        try
        {
            stopping.Wait(ShutdownWait);
        }
        catch (AggregateException)
        {
        }
        Appearance?.Client.Dispose();
    }

    private void OnLocationChanged(ClientPlace? place) => scenes.Observe(place?.Location);

    /// <summary>
    /// On the framework thread, right after the scene location changed:
    /// objects of the old place must not be drawn in the new one. Free them
    /// now, then let the receiver release its receipts and respawn what
    /// belongs here.
    /// </summary>
    /// <summary>Game thread, every frame: applies gizmo drags through the furniture host.</summary>
    private void OnFrameworkUpdate(IFramework _)
    {
        WatchActorSuppression();
        try
        {
            Gizmo.Pump();
        }
        catch (Exception exception)
        {
            log($"RSC gizmo error ({exception.GetType().Name}); the move was discarded.");
            Gizmo.Cancel("The gizmo hit an error; the move was discarded.");
        }
        PruneUnsupportedReports();
        try
        {
            // Per-frame drawing steps and loop checks for RSC's own puppets.
            Objects.Actors?.Tick();
        }
        catch (Exception exception)
        {
            log($"RSC actor controls error ({exception.GetType().Name}).");
        }
        ReportAbandonedActors();
#if RSC_DEVELOPMENT
        try
        {
            t31?.Tick();
        }
        catch (Exception exception)
        {
            log($"RSC T3-1 tick error ({exception.GetType().Name}).");
        }
        try
        {
            hv1?.Tick();
        }
        catch (Exception exception)
        {
            log($"RSC HV-1 tick error ({exception.GetType().Name}).");
        }
        try
        {
            hv2?.Tick();
        }
        catch (Exception exception)
        {
            log($"RSC HV-2 tick error ({exception.GetType().Name}).");
        }
        try
        {
            hv3?.Tick();
        }
        catch (Exception exception)
        {
            log($"RSC HV-3 tick error ({exception.GetType().Name}).");
        }
        try
        {
            hv4?.Tick();
        }
        catch (Exception exception)
        {
            log($"RSC HV-4 tick error ({exception.GetType().Name}).");
        }
#endif
    }

    /// <summary>The last GPose/cutscene observation (game thread), shown in the Actors window.</summary>
    internal ActorSuppression CurrentSuppression { get; private set; } = ActorSuppression.None;

    private long abandonedLogged;
    private long abandonedDroppedLogged;
    private long creationAttemptsLogged;
    private long controlFailuresLogged;
    private long nextAbandonedPrune;

    /// <summary>
    /// Logs each actor record RSC retired because it could no longer prove the
    /// object was its own (deleted, replaced, or its tag lost or changed). RSC
    /// stopped writing to that object and did not delete it. About once a
    /// second it also forgets (read-only) records whose object is gone.
    /// </summary>
    private void ReportAbandonedActors()
    {
        if (Objects.Actors is not { } actors)
            return;
        var total = actors.AbandonedCount;
        if (total != abandonedLogged)
        {
            var logged = 0;
            foreach (var item in actors.Abandoned.Where(item => item.Sequence > abandonedLogged))
            {
                log($"RSC stopped managing actor {item.ObjectId.Value:N} (global index {item.GlobalIndex}): {item.Reason}" +
                    (item.ProofFailure is { } proof ? $" ({proof})" : "") + ". The object was not deleted, hidden or otherwise touched.");
                logged++;
            }
            var unseen = total - abandonedLogged - logged;
            if (unseen > 0)
                log($"RSC stopped managing {unseen} more actor object(s) whose records are no longer kept " +
                    "(already gone, or over the record limit). None was deleted.");
            abandonedLogged = total;
        }
        var dropped = actors.DroppedAbandonedRecords;
        if (dropped != abandonedDroppedLogged)
        {
            log($"RSC dropped {dropped - abandonedDroppedLogged} abandoned-actor record(s) at the limit of " +
                $"{ActorLocalObjectHost.MaximumAbandoned}; their objects were not touched.");
            abandonedDroppedLogged = dropped;
        }
        // Every native creation attempt: in full with the development tools
        // (dev plugin only), otherwise the failed gate of each failed attempt
        // (the gate is not in the player text).
        foreach (var attempt in actors.CreationAttempts.Where(item => item.Sequence > creationAttemptsLogged))
        {
            if (attempt.LogLine(actors.DevelopmentToolsEnabled) is { } line)
                log(line);
            creationAttemptsLogged = attempt.Sequence;
        }
        // Every build: why an actor's synchronized loop could not be
        // applied on this client (ids, numbers and flags only).
        foreach (var failure in actors.ControlFailures.Where(item => item.Sequence > controlFailuresLogged))
        {
            log("RSC actor controls not applied " + failure.Describe());
            controlFailuresLogged = failure.Sequence;
        }
        var now = Environment.TickCount64;
        if (now >= nextAbandonedPrune)
        {
            nextAbandonedPrune = now + 1000;
            if (actors.Abandoned.Count > 0)
                actors.PruneAbandoned();
        }
    }

    /// <summary>This client's game version (local only), matched against the animation allowlist.</summary>
    internal string? GameVersion { get; }

    private long nextReportPrune;

    /// <summary>
    /// About once a second: drop "not drawn" reports of objects that were
    /// deleted or are otherwise no longer wanted. Reports of objects that
    /// draw are cleared by the host itself.
    /// </summary>
    private void PruneUnsupportedReports()
    {
        var now = Environment.TickCount64;
        if (now < nextReportPrune)
            return;
        nextReportPrune = now + 1000;
        if (Objects.Unsupported.Count == 0)
            return;
        var wanted = Runtime.DesiredLocalObjects().Select(state => state.ObjectId).ToHashSet();
        Objects.RetainUnsupported(wanted.Contains);
    }

    /// <summary>
    /// Game thread, every frame: entering or leaving GPose or a cutscene frees
    /// every RSC actor at once and redraws the actors (only them) through the
    /// receiver; while suppressed, actors are not visible here, so nothing is
    /// spawned. RSC puppets are never added to GPose. Furniture and lights are
    /// unaffected.
    /// </summary>
    private void WatchActorSuppression()
    {
        ActorSuppression state;
        try
        {
            state = actorsSuppressed();
        }
        catch (Exception exception)
        {
            state = ActorSuppression.Failed(exception.GetType().Name);
        }
        CurrentSuppression = state;
        var suppressed = state.Suppressed;
        if (suppressed == actorsWereSuppressed)
            return;
        actorsWereSuppressed = suppressed;
        log(suppressed
            ? $"RSC actors hidden on this client: {state.Describe()}."
            : "RSC actors shown again (no GPose or cutscene).");
        if (suppressed)
            Gizmo.Cancel("GPose or a cutscene started; the move was discarded.");
        Objects.Actors?.DestroyAll();
        Objects.Actors?.ClearUnsupported();
        Runtime.ResetLocalObjects(RscObjectKind.Actor);
    }

    private void OnSceneChanged(SceneLocation? scene)
    {
        Gizmo.Cancel("You moved to another place; the move was discarded.");
        Objects.DestroyAll();
        Objects.ClearUnsupported();
        Runtime.ResetLocalObjects();
        // Furniture readiness follows the known place; re-check it now, not
        // on the next periodic check (up to 5 s later), so arriving at a place
        // draws its furniture without that delay.
        Service.RecheckDependencies();
    }

    /// <summary>
    /// "Retry all scene objects": the established whole-domain reset, exactly
    /// as on a location change. Every furniture item and light at this place
    /// is released and drawn again (a fresh round of attempts for each), not
    /// only failed ones. Runs on the framework thread.
    /// </summary>
    internal void RetryAllSceneObjects()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            Gizmo.Cancel("Scene objects are being drawn again.");
            Objects.DestroyAll();
            Objects.ClearUnsupported();
            Runtime.ResetLocalObjects();
            Service.RecheckDependencies();
        });
    }

    private static FurnitureCatalog LoadCatalog(IDataManager data, Action<string> log)
    {
        try
        {
            var catalog = DalamudFurnitureData.LoadCatalog(data);
            log($"RSC furniture catalogue: {catalog.Count} items.");
            return catalog;
        }
        catch (Exception exception)
        {
            // Without a catalogue every item fails closed as unknown.
            log($"RSC could not read the furniture catalogue ({exception.GetType().Name}).");
            return new FurnitureCatalog([]);
        }
    }

    private static ActorCatalog LoadActorCatalog(DalamudActorData data, ActorAppearanceResolver resolver, Action<string> log)
    {
        try
        {
            var links = DalamudActorData.LoadNameLinks();
            var catalog = ActorCatalog.Build(data, resolver, links, data.NameOf);
            log($"RSC actor catalogue: {catalog.Count} creatures ({links.BaseCount} named rows linked).");
            return catalog;
        }
        catch (Exception exception)
        {
            // Without a catalogue nothing can be placed; actors placed by
            // others still resolve and draw on their own.
            log($"RSC could not read the actor catalogue ({exception.GetType().Name}).");
            return ActorCatalog.Empty;
        }
    }

    private static FurnitureStainPalette LoadStains(IDataManager data, Action<string> log)
    {
        try
        {
            var stains = DalamudFurnitureData.LoadStains(data);
            log($"RSC housing stains: {stains.Entries.Count}.");
            return stains;
        }
        catch (Exception exception)
        {
            // Without stains every dyed item fails closed (not drawn); Default still draws.
            log($"RSC could not read the housing stains ({exception.GetType().Name}).");
            return FurnitureStainPalette.Empty;
        }
    }

    private ServerConnectionSettings CurrentSettings()
    {
        lock (gate)
            return new(Configuration.ServerEndpoint, accessKey, Configuration.ConnectEnabled);
    }
}
