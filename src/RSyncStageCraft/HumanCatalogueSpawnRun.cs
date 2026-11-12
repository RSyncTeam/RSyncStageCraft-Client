#if RSC_DEVELOPMENT
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft;

/// <summary>
/// HV-5 M2 only: one spawn from a local catalogue entry, composed for and
/// driven by <see cref="HumanCatalogueRun"/> (the <c>/rschv5</c> command's
/// <c>spawn &lt;label&gt;</c>, <c>spawn-test-scale &lt;label&gt;</c>, <c>remove</c>
/// and <c>status</c>).
/// It composes HV-1's baseline with its native adapter, HV-2's Penumbra
/// stage (split, two-phase, with the entry's package, HV-5's own tag and
/// collection name, releasing by deletion), HV-3's Glamourer stage (the
/// entry's state, Equipment and Customization, no lock), the Customize+ stage
/// (the detach always; HV-4's fixed profile only with <c>test-scale</c>) and
/// W-3. It exists only in a Debug build loaded as a dev plugin.
/// <list type="bullet">
/// <item>Before anything is created: the versions now and S-1, the player's
/// resolved resources and the mod root (one framework read); then, off the
/// framework thread, the entry (read only), D7, the deformer comparison, the
/// held leases and the package.</item>
/// <item>The holds are released last, and only after the actor's deletion
/// and the collection's removal are both confirmed; otherwise they are
/// retained with the journal, and at unload detached so they stay open until
/// the game exits.</item>
/// </list>
/// Its records go to the Dalamud log with no name, address, path, key or
/// full receipt. The label never reaches the actor or a provider. No server
/// call is made. Its only subscription is Dalamud's plugin-list change
/// (<see cref="Subscribe"/>), which only moves a counter.
/// </summary>
internal sealed class HumanCatalogueSpawnRun
{
    internal const string LogPrefix = "RSC HV-5 ";

    /// <summary>BC-9: HV-5's own temporary mod tag and collection name.</summary>
    internal const string PenumbraName = "RSC HV-5";

    private readonly IFramework framework;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Func<HumanCatalogueStore> store;
    private readonly Func<HumanCatalogueVersions> versions;
    private readonly Action<string> log;
    private readonly HumanActorNativeBackend backend = new();
    private readonly HumanActorPenumbraIpc penumbraIpc;
    private readonly HumanActorBaseline baseline;
    private readonly HumanCatalogueHoldSet holds;
    private readonly HumanCatalogueSpawnSession session;
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private Task? preparing;
    private (HumanCatalogueSpawnReady Ready, bool TestScale)? prepared;
    private volatile bool shutDown;
    private long generation;
    private bool subscribed;

    internal HumanCatalogueSpawnRun(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IDataManager data,
        IPlayerState playerState,
        Func<string?> sceneExit,
        Func<HumanCatalogueStore> store,
        Func<HumanCatalogueVersions> versions,
        string catalogueRoot,
        HumanCatalogueProcess process,
        Action<string> log)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(playerState);
        ArgumentNullException.ThrowIfNull(sceneExit);
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.versions = versions ?? throw new ArgumentNullException(nameof(versions));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
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
        baseline = new HumanActorBaseline(backend, sceneExit, log, penumbra, glamourer, customizePlus, new HumanActorWorldData(data, playerState),
            LogPrefix);
        holds = new HumanCatalogueHoldSet(catalogueRoot, process, log, LogPrefix);
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

    /// <summary>1 while the baseline holds an actor; read by the other runners' scene checks.</summary>
    internal int LiveCount => baseline.LiveCount;

    /// <summary>True while a spawn is being prepared, an actor exists, or any hold is active or retained (a capture is then refused).</summary>
    internal bool Busy
    {
        get
        {
            lock (gate)
            {
                if (preparing is { IsCompleted: false } || prepared is not null)
                    return true;
            }
            return baseline.LiveCount != 0 || holds.Active + holds.Retained > 0;
        }
    }

    /// <summary>Game thread: <c>spawn &lt;label&gt;</c>, <c>spawn-test-scale &lt;label&gt;</c>, <c>remove</c> or <c>status</c>.</summary>
    internal void Step(string step)
    {
        if (shutDown)
            return;
        if (step == "remove")
        {
            baseline.Remove();
            session.Settle();
            return;
        }
        if (step == "status")
        {
            log(baseline.Status() + $" Holds: {holds.Active} active, {holds.Retained} retained.");
            return;
        }
        if (HumanCatalogueSpawnCommand.TryParse(step, out var label, out var testScale))
            Begin(label, testScale);
    }

    /// <summary>Game thread, every framework update: a prepared spawn first, then the draw steps, then the holds.</summary>
    internal void Tick()
    {
        if (shutDown)
            return;
        (HumanCatalogueSpawnReady Ready, bool TestScale)? next;
        lock (gate)
        {
            next = prepared;
            prepared = null;
        }
        if (next is { } spawn)
            Start(spawn.Ready, spawn.TestScale);
        baseline.OnFrameworkUpdate();
        session.Settle();
    }

    /// <summary>
    /// Plugin unload, after the command was removed, within the caller's one
    /// 5-second bound (<paramref name="remaining"/>): any preparation is
    /// cancelled (a preparation that finishes later closes its own holds, as
    /// unloading has begun); a prepared spawn's holds are closed; then the
    /// Customize+ deletion, the Glamourer revert, the collection and the actor
    /// (by the baseline) on the game thread within what is left of the bound;
    /// then the holds: released if both confirmations hold, otherwise retained
    /// and detached. The preparation itself is not waited for here.
    /// </summary>
    internal void Shutdown(Func<TimeSpan> remaining)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        if (shutDown)
            return;
        lock (gate)
        {
            shutDown = true;
            prepared?.Ready.Abandon();
            prepared = null;
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
        }
        catch (Exception exception)
        {
            log(LogPrefix + $"unload failed ({exception.GetType().Name}); any actor, collection, Glamourer state or Customize+ profile left is residue; CLEANUP FAILURE.");
        }
        finally
        {
            if (!finished)
                holds.Retain("the unload did not finish");
            // Anything still held is unconfirmed: detached, so it stays open until the game exits.
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
    }

    /// <summary>
    /// The versions, the mod root and the player's resolved resources now (S-1;
    /// game thread, one read each); null when they could not be read.
    /// </summary>
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

    /// <summary>Game thread. The quick refusals, then one framework read, then the preparation off the framework thread.</summary>
    private void Begin(string label, bool testScale)
    {
        lock (gate)
        {
            if (preparing is { IsCompleted: false } || prepared is not null)
            {
                log(LogPrefix + "spawn refused: a spawn is already being prepared.");
                return;
            }
        }
        if (baseline.LiveCount != 0)
        {
            log(LogPrefix + "spawn refused: an actor already exists; remove it first.");
            return;
        }
        if (holds.Retained > 0)
        {
            log(LogPrefix + $"spawn refused: {holds.Retained} hold(s) are retained after an unconfirmed cleanup; no HV-5 spawn until unload.");
            return;
        }
        if (baseline.Stop is not null)
        {
            log(LogPrefix + "spawn refused: an earlier step stopped; nothing is retried until unload.");
            return;
        }
        if (ReadNow() is not { } read)
        {
            log(LogPrefix + "spawn refused: the versions or the player's resources could not be read.");
            return;
        }
        var token = stopping.Token;
        lock (gate)
            preparing = Task.Run(() => PrepareAsync(label, testScale, read, token), token);
    }

    private async Task PrepareAsync(string label, bool testScale, HumanCatalogueSpawnRead read, CancellationToken cancellationToken)
    {
        try
        {
            var (ready, refusal) = await HumanCatalogueSpawnPreparation.PrepareAsync(label, testScale, store(), read, cancellationToken)
                .ConfigureAwait(false);
            if (ready is null)
            {
                Say($"spawn refused: {refusal}. Nothing was created.");
                return;
            }
            lock (gate)
            {
                if (!shutDown)
                {
                    prepared = (ready, testScale);
                    return;
                }
            }
            ready.Abandon();
        }
        catch (OperationCanceledException)
        {
            Say("spawn cancelled before anything was created.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The type only: an exception message may name a path.
            Say($"spawn preparation failed ({exception.GetType().Name}); nothing was created.");
        }
    }

    /// <summary>Game thread: the holds are taken, then the baseline spawns with the entry's plan.</summary>
    private void Start(HumanCatalogueSpawnReady ready, bool testScale)
    {
        if (baseline.LiveCount != 0 || holds.Active + holds.Retained > 0)
        {
            ready.Abandon();
            log(LogPrefix + "spawn refused: an actor or a hold appeared while the spawn was prepared; nothing was created.");
            return;
        }
        var entry = ready.Entry;
        var counts = entry.Counts;
        var plan = new HumanActorSpawnPlan(entry.Race, entry.Tribe, entry.Sex, entry.BodyType, ready.PhaseOneRequired,
            address => HumanCatalogueSkeletonSummary.BaseMismatch(entry.SkeletonSummary, ActorSkeleton(address)),
            address => HumanCatalogueSkeletonSummary.Mismatch(entry.SkeletonSummary, ActorSkeleton(address)),
            new HumanGlamourerPayload(entry.GlamourerState, HumanActorGlamourerStage.EquipmentAndCustomization, entry.GlamourerReference),
            ready.Deformer, testScale, new HumanCatalogueDeformerRecheck(entry, ReadNow, stopping.Token), HumanPuppetNativeBackend.HumanDrawObject);
        log(LogPrefix + $"spawning entry {entry.ShortId}: versions as recorded; {ready.Holds.Count} stored file(s) held and verified; " +
            $"package {ready.Package.Redirects} redirect(s), {ready.Package.Swaps} swap(s); skeleton group " +
            (entry.Skeleton is { } group ? $"c{group.RaceCode:D4} ({group.Members.Count}, deformer {(group.Deformer ? "yes" : "no")})" : "none") +
            $"; deformer {(ready.Deformer ? "equal to the player's current one" : "none, on the entry or the player")}; " +
            $"by role: skeleton {counts.Skeleton}, physics {counts.Physics}, deformer {counts.Deformer}, other {counts.Other}; " +
            $"Customize+ {(testScale ? "test scale" : "detach only")}.");
        session.Start(ready, plan);
    }

    /// <summary>The Penumbra stage's package source: the session's current spawn, read when the split apply runs.</summary>
    private (HumanActorPenumbraPackage? Package, string? Meta, string? Refusal) CurrentPackage() => session.CurrentPackage();

    /// <summary>The actor's skeleton summary as the capture stored the player's.</summary>
    private static IReadOnlyList<string> ActorSkeleton(nint address) =>
        HumanCatalogueSkeletonSummary.Normalize(HumanPuppetNativeBackend.SkeletonSummary(address, "actor"), "actor");

    /// <summary>Any plugin load or unload moves the generation; nothing else happens here.</summary>
    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => Interlocked.Increment(ref generation);

    /// <summary>Logs a line unless unloading has begun.</summary>
    private void Say(string line)
    {
        if (!shutDown)
            log(LogPrefix + line);
    }
}
#endif
