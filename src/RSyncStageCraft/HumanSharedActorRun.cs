#if RSC_DEVELOPMENT
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Location;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft;

/// <summary>What HV-6 needs from the connection: the appearance transfer and cache, the object sends, the account and the place.</summary>
internal sealed record HumanSharedServices(
    AppearanceTransferClient Transfer,
    AppearanceBlobCache Cache,
    FurnitureEditor Editor,
    Func<RscUserId?> Self,
    Func<SceneLocation?> Here);

/// <summary>
/// HV-6 only: the shared human actor's connected host and its steps, a Debug
/// build loaded as a dev plugin only (a Release build binds
/// <see cref="UnavailableHumanActorHost"/>). It is composed by
/// <see cref="HumanCatalogueRun"/>, whose <c>/rschv5</c> command routes HV-6's
/// steps here.
/// <list type="bullet">
/// <item>The host: given the desired shared humans on every framework update,
/// it draws at most one, the first in object-id order whose scene is the
/// player's current scene, while no GPose, cutscene or loading screen is
/// active and no other RSC actor or puppet exists. It downloads and verifies
/// the bundle into the appearance cache, holds every blob there, rebuilds an
/// in-memory entry (format <c>hv6-shared-1</c>, never written to the
/// catalogue) and spawns it through HV-5's unchanged sequence at the shared
/// position and yaw. A later revision moves it; a delete, another scene, a
/// suppression or the session's end removes it. The holds are released last,
/// only once the actor's deletion and the collection's removal are both
/// confirmed; otherwise retained with the journal and detached at unload.</item>
/// <item>The sender's steps: <c>share</c>, <c>unshare</c>,
/// <c>place-shared</c>, <c>move-shared</c>, <c>remove-shared</c> and
/// <c>shared-status</c>. A local entry is read only; its package is a
/// format-3 bundle; placement, editing and removal are the bundle owner's
/// only (the server checks).</item>
/// </list>
/// Its records go to the Dalamud log with no name, address, path, key, full
/// hash or receipt: objects and bundles by their first 8 hex characters only.
/// </summary>
internal sealed class HumanSharedActorRun : IHumanActorHostPort
{
    internal const string LogPrefix = "RSC HV-6 ";

    /// <summary>HV-6's own temporary mod tag and collection name.</summary>
    internal const string PenumbraName = "RSC HV-6";

    /// <summary>HV-6's retention journal folder, beside the HV-5 catalogue (never inside it).</summary>
    internal const string HoldFolder = "hv6-shared";

    private readonly IFramework framework;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Func<string?> sceneExit;
    private readonly Func<HumanCatalogueStore> store;
    private readonly Func<HumanCatalogueVersions> versions;
    private readonly HumanSharedServices services;
    private readonly Action<string> log;
    private readonly string? loadRefusal;
    private readonly HumanActorNativeBackend backend = new();
    private readonly HumanActorPenumbraIpc penumbraIpc;
    private readonly HumanActorBaseline baseline;
    private readonly HumanCatalogueHoldSet holds;
    private readonly HumanCatalogueSpawnSession session;
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<RscObjectId, string> refused = [];
    private HumanActorDesiredView latest = HumanActorDesiredView.Empty;
    private Tracked? current;
    private (RscObjectId ObjectId, HumanCatalogueSpawnReady Ready)? prepared;
    private (RscObjectId ObjectId, string Refusal)? preparationRefused;
    private Task? command;
    private string? waitingReason;
    private volatile bool shutDown;
    private long generation;
    private bool subscribed;

    /// <summary>The one shared human this host follows: prepared, then spawned (<see cref="Started"/>).</summary>
    private sealed class Tracked(HumanActorState state, CancellationTokenSource cancel)
    {
        public HumanActorState State { get; set; } = state;
        public CancellationTokenSource Cancel { get; } = cancel;
        public bool Started { get; set; }
        public string Short => ShortId(State.ObjectId);
    }

    internal HumanSharedActorRun(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IDataManager data,
        IPlayerState playerState,
        Func<string?> sceneExit,
        Func<HumanCatalogueStore> store,
        Func<HumanCatalogueVersions> versions,
        string holdRoot,
        HumanCatalogueProcess process,
        HumanSharedServices services,
        Action<string> log,
        string? loadRefusal = null)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(playerState);
        this.sceneExit = sceneExit ?? throw new ArgumentNullException(nameof(sceneExit));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.versions = versions ?? throw new ArgumentNullException(nameof(versions));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.loadRefusal = loadRefusal;
        ArgumentException.ThrowIfNullOrWhiteSpace(holdRoot);
        penumbraIpc = new HumanActorPenumbraIpc(pluginInterface);
        var glamourerIpc = new HumanActorGlamourerIpc(pluginInterface, () => Interlocked.Read(ref generation));
        var customizePlusIpc = new HumanActorCustomizePlusIpc(pluginInterface, () => Interlocked.Read(ref generation));
        var penumbra = new HumanActorPenumbraStage(penumbraIpc, log, LogPrefix)
        {
            ReleaseByDeletion = true,
            ModTag = PenumbraName,
            TemporaryCollectionName = PenumbraName,
            Split = true,
            PackageSource = CurrentPackage,
        };
        var glamourer = new HumanActorGlamourerStage(glamourerIpc, log, LogPrefix);
        var customizePlus = new HumanActorCustomizePlusStage(customizePlusIpc, log, LogPrefix) { AllowAbsent = true };
        // The scene rule here is the host's own (GPose, cutscene, loading screen, other RSC actors); a server session is required, not refused.
        baseline = new HumanActorBaseline(backend, sceneExit, log, penumbra, glamourer, customizePlus, new HumanActorWorldData(data, playerState),
            LogPrefix);
        holds = new HumanCatalogueHoldSet(holdRoot, process, log, LogPrefix);
        session = new HumanCatalogueSpawnSession(baseline, penumbra, holds);
    }

    /// <summary>The plugin-list subscription, called last by <see cref="HumanCatalogueRun"/>'s constructor.</summary>
    internal void Subscribe()
    {
        if (subscribed)
            return;
        pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        subscribed = true;
    }

    /// <summary>1 while the baseline holds a shared human (any phase); read by the other runners' scene checks.</summary>
    public int LiveCount => baseline.LiveCount;

    /// <summary>True while a shared human is followed, prepared or drawn, or any hold is active or retained (a capture or an HV-5 spawn is then refused).</summary>
    internal bool Busy
    {
        get
        {
            lock (gate)
            {
                if (current is not null || prepared is not null)
                    return true;
            }
            return baseline.LiveCount != 0 || holds.Active + holds.Retained > 0;
        }
    }

    /// <summary>Game thread, every framework update: converge on the desired shared humans, then the draw steps, then the holds.</summary>
    public void Tick(HumanActorDesiredView desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (shutDown)
            return;
        (RscObjectId ObjectId, HumanCatalogueSpawnReady Ready)? next;
        (RscObjectId ObjectId, string Refusal)? failed;
        lock (gate)
        {
            latest = desired;
            next = prepared;
            prepared = null;
            failed = preparationRefused;
            preparationRefused = null;
        }
        var exit = SafeSceneExit();
        var target = exit is null ? Target(desired) : null;
        // A refusal holds only while its human stays the drawable target: leaving
        // the scene (or GPose, a cutscene, a loading screen) and coming back tries
        // again, so a transient failure is not permanent. A lasting cause simply
        // refuses again, logged once per return.
        foreach (var gone in refused.Keys.Where(id => id != target?.ObjectId).ToArray())
            refused.Remove(gone);
        if (failed is { } refusal && refusal.ObjectId == target?.ObjectId)
            Refuse(refusal.ObjectId, refusal.Refusal);
        Report(exit, target);

        if (current is { } followed)
        {
            if (target is null || target.ObjectId != followed.State.ObjectId)
                Drop(followed, target is null && exit is not null ? exit : "it is no longer shared in this scene");
            else if (followed.Started && baseline.LiveCount == 0)
                EndedByItself(followed);
        }
        if (next is { } spawn)
        {
            if (current is { Started: false } waiting && waiting.State.ObjectId == spawn.ObjectId && target?.ObjectId == spawn.ObjectId)
                Start(waiting, spawn.Ready, target);
            else
                spawn.Ready.Abandon();
        }
        if (target is not null && current is null && !refused.ContainsKey(target.ObjectId))
            Admit(target);
        else if (target is not null && current is { Started: true } shown && shown.State.ObjectId == target.ObjectId &&
                 target.Revision.Value > shown.State.Revision.Value)
            Follow(shown, target);
        baseline.OnFrameworkUpdate();
        session.Settle();
    }

    /// <summary>Game thread: one HV-6 step, routed here by <c>/rschv5</c>.</summary>
    internal void Step(string text)
    {
        if (shutDown)
            return;
        var step = HumanSharedCommand.TryParse(text, out var label, out var withProfile);
        if (step == HumanSharedStep.None)
            return;
        if (step == HumanSharedStep.Status)
        {
            Status();
            return;
        }
        lock (gate)
        {
            if (command is { IsCompleted: false })
            {
                log(LogPrefix + "refused: an HV-6 step is still running.");
                return;
            }
        }
        if (services.Self() is not { } self)
        {
            log(LogPrefix + "refused: not signed in to the RSC server.");
            return;
        }
        var token = stopping.Token;
        Task? task = null;
        switch (step)
        {
            case HumanSharedStep.Share:
            case HumanSharedStep.Unshare:
            {
                var running = versions();
                task = Task.Run(() => step == HumanSharedStep.Share
                    ? ShareAsync(label, withProfile, self, running, token)
                    : UnshareAsync(label, withProfile, self, running, token), token);
                break;
            }
            case HumanSharedStep.Place:
            {
                if (OwnPlacementNow(out var here, out var refusal) is not { } placement)
                {
                    log(LogPrefix + $"place refused: {refusal}.");
                    return;
                }
                var running = versions();
                task = Task.Run(() => PlaceAsync(label, withProfile, self, running, here, placement, token), token);
                break;
            }
            case HumanSharedStep.Move:
            case HumanSharedStep.Remove:
            {
                if (OwnHuman(self) is not { } own)
                {
                    log(LogPrefix + $"{(step == HumanSharedStep.Move ? "move" : "remove")} refused: you have no shared human in this scene.");
                    return;
                }
                if (step == HumanSharedStep.Remove)
                {
                    task = Report("remove", services.Editor.DeleteAsync(own, token), own.ObjectId);
                    break;
                }
                if (OwnPlacementNow(out _, out var refusal) is not { } placement)
                {
                    log(LogPrefix + $"move refused: {refusal}.");
                    return;
                }
                task = Report("move", services.Editor.SetHumanTransformAsync(own, placement.Position, placement.Rotation, token), own.ObjectId);
                break;
            }
        }
        lock (gate)
            command = task;
    }

    /// <summary>
    /// Unload, after the last tick, within the caller's one 5-second bound:
    /// any preparation and step are cancelled (a preparation that finishes
    /// later closes its own holds); a prepared spawn's holds are closed; then
    /// the actor and its provider state (by the baseline) on the game thread;
    /// then the holds: released if both confirmations hold, otherwise
    /// retained and detached. Nothing is sent to the server.
    /// </summary>
    public void Shutdown(Func<TimeSpan> remaining)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        if (shutDown)
            return;
        lock (gate)
        {
            shutDown = true;
            prepared?.Ready.Abandon();
            prepared = null;
            current?.Cancel.Cancel();
        }
        stopping.Cancel();
        var finished = false;
        try
        {
            Task unload;
            if (framework.IsInFrameworkUpdateThread)
            {
                UnloadOnFramework();
                unload = Task.CompletedTask;
            }
            else
            {
                unload = framework.RunOnFrameworkThread(new Action(UnloadOnFramework));
            }
            var left = remaining();
            finished = unload.IsCompletedSuccessfully || (left > TimeSpan.Zero && unload.Wait(left));
            if (!finished)
                log(LogPrefix + "unload did not finish within the 5 s bound; any actor, collection, Glamourer state or Customize+ profile left is residue; " +
                    "CLEANUP FAILURE.");
            // A cancelled step (share, unshare, place, move, remove) is waited for
            // within what is left of the same bound, so it does not outlive the
            // transfer client and cache it uses. Its own errors are its own.
            Task? step;
            lock (gate)
                step = command;
            if (step is { IsCompleted: false } && remaining() is var wait && wait > TimeSpan.Zero)
            {
                try
                {
                    if (!step.Wait(wait))
                        log(LogPrefix + "a cancelled HV-6 step did not end within the bound; it is abandoned.");
                }
                catch (AggregateException)
                {
                }
            }
        }
        catch (Exception exception)
        {
            log(LogPrefix + $"unload failed ({exception.GetType().Name}); any actor, collection, Glamourer state or Customize+ profile left is residue; CLEANUP FAILURE.");
        }
        finally
        {
            if (!finished)
                holds.Retain("the unload did not finish");
            holds.DetachAll();
            if (subscribed)
                pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
            subscribed = false;
        }
    }

    private void UnloadOnFramework()
    {
        baseline.Unload();
        session.Settle();
        current = null;
    }

    /// <summary>The first desired human, in object-id order, of the player's current scene; null when none or the place is unknown.</summary>
    private HumanActorState? Target(HumanActorDesiredView desired)
    {
        if (services.Here() is not { IsValid: true } here || LocationScenes.SceneFor(here) is not { } scene)
            return null;
        return desired.Humans.FirstOrDefault(human => human.SceneId == scene);
    }

    /// <summary>Logs once each time the reason a desired human waits changes (never per frame).</summary>
    private void Report(string? exit, HumanActorState? target)
    {
        string? reason = null;
        if (exit is not null && latest.Humans.Count != 0)
            reason = exit;
        if (reason == waitingReason)
            return;
        waitingReason = reason;
        if (reason is not null)
            log(LogPrefix + $"shared humans wait: {reason}.");
    }

    /// <summary>Game thread: the quick refusals, one framework read, then the download and preparation off the framework thread.</summary>
    private void Admit(HumanActorState target)
    {
        if (loadRefusal is not null)
        {
            Refuse(target.ObjectId, loadRefusal);
            return;
        }
        if (baseline.Stop is not null)
        {
            Refuse(target.ObjectId, "an earlier step stopped; nothing is retried until unload");
            return;
        }
        if (holds.Retained > 0)
        {
            Refuse(target.ObjectId, $"{holds.Retained} hold(s) are retained after an unconfirmed cleanup; no shared human until unload");
            return;
        }
        if (holds.Active > 0 || baseline.LiveCount != 0)
            return;
        if (ReadNow() is not { } read)
        {
            Refuse(target.ObjectId, "the versions or the player's resources could not be read");
            return;
        }
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        var followed = new Tracked(target, cancel);
        current = followed;
        log(LogPrefix + $"shared human {followed.Short} (bundle {target.AppearanceBundleId[..8]}): fetching and preparing.");
        var token = cancel.Token;
        _ = Task.Run(() => PrepareAsync(target, read, token), token);
    }

    private async Task PrepareAsync(HumanActorState target, HumanCatalogueSpawnRead read, CancellationToken cancellationToken)
    {
        string? refusal = null;
        HumanCatalogueSpawnReady? ready = null;
        try
        {
            if (!ContentHash.TryParse(target.AppearanceBundleId, out var bundleId))
            {
                refusal = "its bundle id is not valid";
            }
            else
            {
                var download = await services.Transfer.DownloadBundleAsync(bundleId, cancellationToken, AppearanceManifestReadOptions.AllowHumanPackages)
                    .ConfigureAwait(false);
                if (download.Manifest is not { } manifest)
                    refusal = $"the bundle could not be fetched ({download.Result.Status})";
                else if (manifest.OwnerId != target.OwnerId)
                    refusal = "the bundle's owner is not the shared human's owner";
                else
                {
                    Say($"shared human {ShortId(target.ObjectId)}: bundle verified, {download.DownloadedBlobs} of {manifest.Blobs.Count} blob(s) downloaded " +
                        "(the rest were already verified in the cache).");
                    (ready, refusal) = await HumanSharedSpawnPreparation.PrepareAsync(manifest, services.Cache, read, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            ready?.Abandon();
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The type only: an exception message may name a path.
            ready?.Abandon();
            refusal = $"its preparation failed ({exception.GetType().Name})";
            ready = null;
        }
        lock (gate)
        {
            if (!shutDown && !cancellationToken.IsCancellationRequested)
            {
                if (ready is not null)
                    prepared = (target.ObjectId, ready);
                else
                    preparationRefused = (target.ObjectId, refusal ?? "it could not be prepared");
                return;
            }
        }
        ready?.Abandon();
    }

    /// <summary>Game thread: the holds are taken, then the baseline spawns with the received entry's plan at the shared placement.</summary>
    private void Start(Tracked followed, HumanCatalogueSpawnReady ready, HumanActorState target)
    {
        if (baseline.LiveCount != 0 || holds.Active + holds.Retained > 0)
        {
            ready.Abandon();
            current = null;
            return;
        }
        var entry = ready.Entry;
        var profile = HumanCatalogueSpawnPreparation.ProfilePayload(entry, false, out var profileRefusal);
        if (profileRefusal is not null)
        {
            ready.Abandon();
            current = null;
            Refuse(target.ObjectId, profileRefusal);
            return;
        }
        var plan = new HumanActorSpawnPlan(entry.Race, entry.Tribe, entry.Sex, entry.BodyType, ready.PhaseOneRequired,
            address => HumanCatalogueSkeletonSummary.BaseMismatch(entry.SkeletonSummary, ActorSkeleton(address)),
            address => HumanCatalogueSkeletonSummary.Mismatch(entry.SkeletonSummary, ActorSkeleton(address)),
            new HumanGlamourerPayload(entry.GlamourerState, HumanActorGlamourerStage.EquipmentAndCustomization, entry.GlamourerReference),
            ready.Deformer, TestScale: false, new HumanCatalogueDeformerRecheck(entry, ReadNow, followed.Cancel.Token), HumanPuppetNativeBackend.HumanDrawObject,
            profile, PlacementOf(target));
        var counts = entry.Counts;
        log(LogPrefix + $"spawning shared human {followed.Short}: versions as recorded; {ready.Holds.Count} file(s) held and verified in the cache; " +
            $"package {ready.Package.Redirects} redirect(s); skeleton group " +
            (entry.Skeleton is { } group ? $"c{group.RaceCode:D4} ({group.Members.Count}, deformer {(group.Deformer ? "yes" : "no")})" : "none") +
            $"; deformer {(ready.Deformer ? "equal to the player's current one" : "none, on the bundle or the player")}; " +
            $"by role: skeleton {counts.Skeleton}, physics {counts.Physics}, deformer {counts.Deformer}, other {counts.Other}; " +
            $"Customize+ {(profile is not null ? $"the owner's profile, {profile.Description}" : "detach only")}.");
        followed.State = target;
        followed.Started = true;
        if (!session.Start(ready, plan) && baseline.LiveCount == 0)
            EndedByItself(followed);
    }

    /// <summary>Game thread: a later revision of the shown human: moved, read back.</summary>
    private void Follow(Tracked shown, HumanActorState target)
    {
        var before = shown.State;
        shown.State = target;
        if (before.Position == target.Position && before.Yaw == target.Yaw)
            return;
        if (baseline.Move(PlacementOf(target)))
            log(LogPrefix + $"shared human {shown.Short} moved to revision {target.Revision.Value}.");
        else if (baseline.LiveCount == 0)
            EndedByItself(shown);
    }

    /// <summary>Game thread: the followed human is no longer wanted here; removed, and its holds follow the session.</summary>
    private void Drop(Tracked followed, string why)
    {
        followed.Cancel.Cancel();
        current = null;
        if (followed.Started)
        {
            baseline.Remove();
            session.Settle();
        }
        log(LogPrefix + $"shared human {followed.Short} {(followed.Started ? "removed" : "no longer prepared")}: {why}.");
    }

    /// <summary>Game thread: the actor ended without a drop (a stop or a refused spawn): the object is not retried until it is shared again.</summary>
    private void EndedByItself(Tracked followed)
    {
        current = null;
        session.Settle();
        Refuse(followed.State.ObjectId, baseline.Stop is { } stop ? "stopped at " + stop : "the spawn was refused");
    }

    /// <summary>Latches a refusal for one object (logged once) while it stays the drawable target.</summary>
    private void Refuse(RscObjectId objectId, string why)
    {
        if (current?.State.ObjectId == objectId && !current.Started)
            current = null;
        if (refused.TryAdd(objectId, why))
            log(LogPrefix + $"shared human {ShortId(objectId)} is not drawn: {why}. Nothing was created.");
    }

    private void Status()
    {
        string followed;
        lock (gate)
            followed = current is { } tracked ? $"{tracked.Short} ({(tracked.Started ? "spawned" : "preparing")})" : "none";
        log(baseline.Status() + $" Shared humans desired: {latest.Humans.Count}; followed: {followed}; not drawn: {refused.Count}. " +
            $"Holds: {holds.Active} active, {holds.Retained} retained.");
    }

    /// <summary>The caller's own shared human in the player's current scene (from the latest desired view), or null.</summary>
    private HumanActorState? OwnHuman(RscUserId self)
    {
        if (services.Here() is not { IsValid: true } here || LocationScenes.SceneFor(here) is not { } scene)
            return null;
        HumanActorDesiredView view;
        lock (gate)
            view = latest;
        return view.Humans.FirstOrDefault(human => human.OwnerId == self && human.SceneId == scene);
    }

    /// <summary>Game thread: in front of the player, facing them, and the place now; null with the refusal.</summary>
    private HumanActorPlacement? OwnPlacementNow(out SceneLocation? here, out string? refusal)
    {
        here = services.Here();
        refusal = null;
        if (here is not { IsValid: true })
        {
            refusal = "your location is not known";
            return null;
        }
        if (SafeSceneExit() is { } exit)
        {
            refusal = exit;
            return null;
        }
        var player = backend.LocalPlayer;
        if (player == 0 || backend.ReadPlacement(player) is not { } beside)
        {
            refusal = "the local player cannot be read";
            return null;
        }
        return HumanActorBaseline.InFrontOf(beside);
    }

    /// <summary>Off the framework thread: the local entry (read only) and its format-3 package, by label.</summary>
    private HumanSharedPackage? Package(string labelText, bool withProfile, RscUserId self, HumanCatalogueVersions running, out string? refusal)
    {
        if (HumanCatalogueLabel.Normalize(labelText, out var labelRefusal) is not { } label)
        {
            refusal = labelRefusal;
            return null;
        }
        if (store().FindEntry(label, out var unknown) is not { } entry)
        {
            refusal = "the label is unknown: " + unknown;
            return null;
        }
        return HumanSharedPackageBuilder.Build(entry, self, withProfile, running.Bounded(), out refusal);
    }

    private async Task ShareAsync(string label, bool withProfile, RscUserId self, HumanCatalogueVersions running, CancellationToken cancellationToken)
    {
        try
        {
            if (Package(label, withProfile, self, running, out var refusal) is not { } package)
            {
                Say($"share refused: {refusal}. Nothing was uploaded.");
                return;
            }
            Say((await HumanSharedPublisher.ShareAsync(package, store(), services.Cache, services.Transfer, cancellationToken).ConfigureAwait(false)).Line);
        }
        catch (OperationCanceledException)
        {
            Say("share cancelled; nothing was published.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Say($"share failed ({exception.GetType().Name}); nothing was published.");
        }
    }

    private async Task UnshareAsync(string label, bool withProfile, RscUserId self, HumanCatalogueVersions running, CancellationToken cancellationToken)
    {
        try
        {
            if (Package(label, withProfile, self, running, out var refusal) is not { } package)
            {
                Say($"unshare refused: {refusal}.");
                return;
            }
            Say((await HumanSharedPublisher.UnshareAsync(package.Manifest.BundleId, services.Transfer, cancellationToken).ConfigureAwait(false)).Line);
        }
        catch (OperationCanceledException)
        {
            Say("unshare cancelled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Say($"unshare failed ({exception.GetType().Name}).");
        }
    }

    private async Task PlaceAsync(string label, bool withProfile, RscUserId self, HumanCatalogueVersions running, SceneLocation? here,
        HumanActorPlacement placement, CancellationToken cancellationToken)
    {
        try
        {
            if (Package(label, withProfile, self, running, out var refusal) is not { } package)
            {
                Say($"place refused: {refusal}.");
                return;
            }
            var result = await services.Editor.PlaceHumanAsync(here, package.Manifest.BundleId.Value, placement.Position, placement.Rotation, cancellationToken)
                .ConfigureAwait(false);
            Say(result.Completed
                ? $"placed a shared human from bundle {package.Manifest.BundleId.Value[..8]}."
                : $"place refused: {Describe(result)}.");
        }
        catch (OperationCanceledException)
        {
            Say("place cancelled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Say($"place failed ({exception.GetType().Name}).");
        }
    }

    private async Task Report(string what, Task<ServerCommandResult> send, RscObjectId objectId)
    {
        try
        {
            var result = await send.ConfigureAwait(false);
            Say(result.Completed ? $"{what}: shared human {ShortId(objectId)} done." : $"{what} refused: {Describe(result)}.");
        }
        catch (OperationCanceledException)
        {
            Say($"{what} cancelled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Say($"{what} failed ({exception.GetType().Name}).");
        }
    }

    private static string Describe(ServerCommandResult result) =>
        result.RejectReason is { } reason ? $"{result.Status}, {reason}" : result.Detail is { Length: > 0 } detail ? $"{result.Status}, {detail.TrimEnd('.')}" : result.Status.ToString();

    private string? SafeSceneExit()
    {
        try
        {
            return sceneExit();
        }
        catch (Exception exception)
        {
            return $"the scene could not be read ({exception.GetType().Name})";
        }
    }

    /// <summary>The versions, the mod root and the player's resolved resources now (game thread); null when they could not be read.</summary>
    private HumanCatalogueSpawnRead? ReadNow()
    {
        try
        {
            var player = backend.LocalPlayer;
            var index = player == 0 ? -1 : backend.ReadFacts(player).GlobalIndex;
            return new HumanCatalogueSpawnRead(versions(), index < 0 ? null : penumbraIpc.ModDirectory(),
                index < 0 ? null : penumbraIpc.ResourcePaths(index));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static HumanActorPlacement PlacementOf(HumanActorState state) => new(FurnitureLocalObjectHost.ToVector(state.Position), state.Yaw);

    private static string ShortId(RscObjectId objectId) => objectId.Value.ToString("N")[..8];

    /// <summary>The Penumbra stage's package source: the session's current spawn.</summary>
    private (HumanActorPenumbraPackage? Package, string? Meta, string? Refusal) CurrentPackage() => session.CurrentPackage();

    private static IReadOnlyList<string> ActorSkeleton(nint address) =>
        HumanCatalogueSkeletonSummary.Normalize(HumanPuppetNativeBackend.SkeletonSummary(address, "actor"), "actor");

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => Interlocked.Increment(ref generation);

    private void Say(string line)
    {
        if (!shutDown)
            log(LogPrefix + line);
    }
}
#endif
