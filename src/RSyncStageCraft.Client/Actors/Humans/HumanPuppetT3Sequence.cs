#if RSC_DEVELOPMENT
using System.Collections.Concurrent;
using System.Globalization;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// T3-1's read-only run facts beside the host's own records: counts, flags and
/// hash prefixes only (R-5, R-6), never a name, a path, an address or binary
/// data. Game thread only.
/// </summary>
public interface IHumanT3Probe
{
    /// <summary>Every run fact, by a fixed key, read without writing.</summary>
    IReadOnlyList<KeyValuePair<string, string>> ReadRunFacts();

    /// <summary>The player's home world, or null when it cannot be read.</summary>
    ushort? PlayerHomeWorld();
}

/// <summary>
/// T3-1's fixed sequence for one hidden vanilla human (plan r1 §7), on the
/// Debug host with W-2 = World row 1. It composes the host and nothing else.
/// Steps arrive as text from any thread (<see cref="Submit"/>) and run one per
/// framework update, in order: <c>cp3</c>, <c>baseline</c>, <c>spawn</c>,
/// <c>draw</c>, <c>isolation</c>, <c>remove</c>, <c>report</c>. A step out of
/// order, after a stop, with the scene not quiet, after a provider change
/// since the baseline, or with the W-2 row refused, is refused and has no side
/// effect. Nothing is retried. Debug only: compiled out of Release.
/// </summary>
public sealed class HumanPuppetT3Sequence
{
    /// <summary>The steps, in their only order.</summary>
    public static readonly IReadOnlyList<string> Steps = ["cp3", "baseline", "spawn", "draw", "isolation", "remove", "report"];

    /// <summary>The draw observation limits the runner judges (plan r1 §7 step 4).</summary>
    public const int DrawUpdateLimit = 600;
    public const double DrawMillisecondLimit = 10_000;

    /// <summary>A frame longer than this is a hitch (S7 input).</summary>
    public const double HitchMilliseconds = 100;

    private const string Prefix = "RSC T3-1 ";
    private const int MaximumQueuedSteps = 8;

    private readonly IHumanT3Probe probe;
    private readonly Func<long> providerGeneration;
    private readonly Func<string?> sceneExit;
    private readonly Func<SceneLocation?> location;
    private readonly Action<string> log;
    private readonly ConcurrentQueue<string> submitted = new();
    private int queued;
    private int loggedReadings;
    private bool stopLogged;
    private HumanProviderBaseline? baseline;
    private IReadOnlyList<KeyValuePair<string, string>>? baselineFacts;
    private long baselineGeneration;
    private SceneLocation? baselineLocation;
    private bool baselineLocationKnown;
    private HumanPuppetHandle? handle;
    private int puppetSlot = -1;
    private long? drawUpdate;
    private double drawMilliseconds;
    private bool drawJudged;
    private double longestFrame;
    private int hitches;
    private int hitchesAfterAStep;
    private bool stepRanOnPreviousUpdate;

    public HumanPuppetT3Sequence(
        IHumanPuppetBackend backend,
        IHumanParentMapPort maps,
        IHumanPenumbraPort penumbra,
        IHumanGlamourerReadPort glamourer,
        IHumanT3Probe probe,
        HumanPuppetWorldRow? w2Row,
        Func<int> otherRscActors,
        Func<long> providerGeneration,
        Func<string?> sceneExit,
        Func<SceneLocation?> location,
        Action<string> log)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        this.providerGeneration = providerGeneration ?? throw new ArgumentNullException(nameof(providerGeneration));
        this.sceneExit = sceneExit ?? throw new ArgumentNullException(nameof(sceneExit));
        this.location = location ?? throw new ArgumentNullException(nameof(location));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        // W-2: the selected row, checked again now. No fallback: a mismatch refuses every step.
        WorldRefusal = HumanPuppetW2.RowMismatch(w2Row);
        Host = new HumanPuppetHost(backend, maps, penumbra, glamourer, HumanPuppetW2.World(w2Row), HumanPuppetKind.BattleNpc,
            otherRscActors, providerGeneration, LiveExit, puppetLimit: 1);
        log(WorldRefusal is null
            ? Prefix + $"W-2 is World row {HumanPuppetW2.WorldId}; its row check passed."
            : Prefix + $"W-2 refused: {WorldRefusal}. No step will run; there is no fallback.");
    }

    public HumanPuppetHost Host { get; }

    /// <summary>Why the W-2 row was refused at start-up, or null. It never clears.</summary>
    public string? WorldRefusal { get; private set; }

    /// <summary>The puppet's global index once spawned (it stays known after the deletion), or null.</summary>
    public int? PuppetSlot => puppetSlot >= 0 ? puppetSlot : null;

    /// <summary>The index in <see cref="Steps"/> of the next step.</summary>
    public int NextStep { get; private set; }

    /// <summary>Any thread: queues one step for the next framework update. False when the text is not a step or the queue is full.</summary>
    public bool Submit(string? text)
    {
        var step = text?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Steps.Contains(step))
        {
            log(Prefix + "unknown step; the steps are " + string.Join(", ", Steps) + ".");
            return false;
        }
        if (Interlocked.Increment(ref queued) > MaximumQueuedSteps)
        {
            Interlocked.Decrement(ref queued);
            log(Prefix + "too many queued steps; this one was dropped.");
            return false;
        }
        submitted.Enqueue(step);
        return true;
    }

    /// <summary>
    /// Game thread, once per framework update (strictly increasing). The host
    /// ticks first, so a step runs inside this update: a deletion or draw
    /// request is recorded against it, and the host's next check (F2, F2-R
    /// item 1) is on a later update.
    /// </summary>
    public void OnFrameworkUpdate(long update, double frameMilliseconds)
    {
        Host.OnFrameworkUpdate(update);
        var stepRan = false;
        if (submitted.TryDequeue(out var step))
        {
            Interlocked.Decrement(ref queued);
            stepRan = true;
            try
            {
                Run(step);
            }
            catch (Exception exception)
            {
                // A scene or probe read before the step itself; the step does not advance.
                Refuse(step, $"a read failed ({exception.GetType().Name})");
            }
        }
        Observe(update, frameMilliseconds);
        stepRanOnPreviousUpdate = stepRan;
    }

    /// <summary>H1-c's live exits as the host sees them: the scene's own, and a location other than the baseline's.</summary>
    private string? LiveExit()
    {
        if (sceneExit() is { } exit)
            return exit;
        if (baselineLocationKnown && !Equals(location(), baselineLocation))
            return "the location changed since the baseline";
        return null;
    }

    private void Run(string step)
    {
        var index = IndexOf(step);
        if (WorldRefusal is { } world)
        {
            Refuse(step, "W-2 was refused: " + world);
            return;
        }
        if (Host.Stop is not null)
        {
            Refuse(step, "the run has stopped");
            return;
        }
        if (index != NextStep)
        {
            Refuse(step, $"the next step is {Steps[NextStep]}");
            return;
        }
        if (LiveExit() is { } exit)
        {
            Refuse(step, exit);
            return;
        }
        if (baseline is not null && providerGeneration() != baselineGeneration)
        {
            Refuse(step, "a provider changed since the baseline");
            return;
        }
        // H-3 before every step, from the first: the player's own world must
        // differ from W-2. The plugin may load before login, so this is the
        // run's start-up check; the host checks it again before the spawn.
        if (HumanPuppetW2.PlayerWorldMismatch(probe.PlayerHomeWorld()) is { } player)
        {
            WorldRefusal = player;
            Refuse(step, player + "; there is no fallback");
            return;
        }
        bool done;
        try
        {
            done = step switch
            {
                "cp3" => RunCp3(),
                "baseline" => RunBaseline(),
                "spawn" => RunSpawn(),
                "draw" => RunDraw(),
                "isolation" => RunIsolation(),
                "remove" => RunRemove(),
                _ => RunReport(),
            };
        }
        catch (Exception exception)
        {
            // The host's own calls never throw (H1-d); this is a probe read. The step does not advance.
            done = false;
            Refuse(step, $"a read failed ({exception.GetType().Name})");
        }
        if (done)
            NextStep++;
        LogNewReadings();
        LogStopOnce();
    }

    private bool RunCp3()
    {
        var outcome = Host.CheckCustomizePlus();
        log(Prefix + (outcome.Done ? "cp3 passed." : "cp3 failed: " + Describe(outcome)));
        return outcome.Done;
    }

    private bool RunBaseline()
    {
        var captured = Host.CaptureBaseline();
        var facts = probe.ReadRunFacts();
        baseline = captured;
        baselineFacts = facts;
        baselineGeneration = providerGeneration();
        baselineLocation = location();
        baselineLocationKnown = true;
        LogBaseline("baseline", captured, facts);
        return true;
    }

    private bool RunSpawn()
    {
        var result = Host.Spawn();
        foreach (var copy in result.Copies)
            log(Prefix + $"copy {copy.Copy}: kind {copy.RawKind}, world is W-2 {copy.HomeWorld == HumanPuppetW2.WorldId}, " +
                $"name id set {copy.NameId != 0}, owner set {copy.OwnerId != HumanPuppetFacts.NoOwner}, human model {copy.HasHumanModel}, " +
                $"customize filled {copy.CustomizeFilled}, targetable {copy.Targetable}.");
        if (!result.Spawned)
        {
            log(Prefix + "spawn did not complete: " + (result.Stop is { } stop ? DescribeStop(stop) : result.Refusal.ToString()) + ".");
            return false;
        }
        handle = result.Handle;
        puppetSlot = Host.Snapshot().Single(item => item.Handle == result.Handle).GlobalIndex;
        log(Prefix + $"spawned at global index {puppetSlot}, hidden at alpha 0, untargetable, not drawn.");
        return true;
    }

    private bool RunDraw()
    {
        var outcome = Host.RequestDraw(handle!.Value);
        if (outcome.Done)
        {
            drawUpdate = Host.CurrentUpdate;
            drawMilliseconds = 0;
        }
        log(Prefix + (outcome.Done ? $"draw requested on update {Host.CurrentUpdate}, still at alpha 0." : "draw refused: " + Describe(outcome)));
        return outcome.Done;
    }

    private bool RunIsolation()
    {
        var outcome = Host.VerifyIsolation(handle!.Value);
        log(Prefix + (outcome.Done ? "isolation held (P6(a), P16)." : "isolation did not hold: " + Describe(outcome)));
        LogFactDifferences("isolation", probe.ReadRunFacts());
        return outcome.Done;
    }

    private bool RunRemove()
    {
        var outcome = Host.Remove(handle!.Value);
        var phase = Host.Snapshot().Single(item => item.Handle == handle.Value).Phase;
        log(Prefix + (outcome.Done ? $"remove: the puppet is {phase}." : "remove refused: " + Describe(outcome)));
        return outcome.Done && phase == HumanPuppetPhase.Deleted;
    }

    private bool RunReport()
    {
        // N4: only after the slot was read on an update after the deletion.
        var item1 = Host.Readings.LastOrDefault(reading => reading.GlobalIndex == puppetSlot &&
            reading.Phase is "F2-R item 1" or HumanPuppetHost.NotMeasuredPhase);
        if (item1 is null)
        {
            Refuse("report", "F2-R item 1 has not been read yet");
            return false;
        }
        var after = Host.CaptureBaseline();
        var facts = probe.ReadRunFacts();
        LogBaseline("report", after, facts);
        log(Prefix + (item1.Phase == HumanPuppetHost.NotMeasuredPhase
            ? "F2-R item 1: not measured (a provider did not answer)."
            : $"F2-R item 1: Penumbra {item1.Penumbra}, Customize+ {item1.CustomizePlus} on update {item1.Update}."));
        var changed = baseline!.ChangedSlots(after, new HashSet<int> { puppetSlot });
        log(Prefix + (changed.Count == 0
            ? "F2-R item 4: no other slot changed in either map."
            : "F2-R item 4: other slots changed: " + string.Join(", ", changed) + "."));
        LogFactDifferences("report", facts);
        log(Prefix + $"frames while a puppet was live: longest {Milliseconds(longestFrame, "F1")} ms, {hitches} over {Milliseconds(HitchMilliseconds, "F0")} ms, " +
            $"{hitchesAfterAStep} of them on the update after a step.");
        log(Prefix + $"residue: {Host.Residue.Count}." + (Host.Residue.Count == 0 ? "" : " " + string.Join("; ", Host.Residue.Select(item => $"{item.What} ({item.Reason})"))));
        log(Prefix + "report complete. The pass rule is the owner's to judge.");
        return true;
    }

    private void Observe(long update, double frameMilliseconds)
    {
        LogNewReadings();
        LogStopOnce();
        if (handle is not { } live)
            return;
        var snapshot = Host.Snapshot().SingleOrDefault(item => item.Handle == live);
        if (snapshot is null || snapshot.Phase is not (HumanPuppetPhase.Hidden or HumanPuppetPhase.DrawRequested))
            return;
        if (double.IsFinite(frameMilliseconds) && frameMilliseconds >= 0)
        {
            longestFrame = Math.Max(longestFrame, frameMilliseconds);
            if (frameMilliseconds > HitchMilliseconds)
            {
                hitches++;
                // A frame's delta includes the previous update's work, so a
                // step's own reads (isolation, report) are counted apart.
                if (stepRanOnPreviousUpdate)
                    hitchesAfterAStep++;
            }
            if (drawUpdate is not null)
                drawMilliseconds += frameMilliseconds;
        }
        if (drawUpdate is not { } requested || drawJudged)
            return;
        if (snapshot.DrawObservedUpdate is { } observed)
        {
            drawJudged = true;
            var updates = observed - requested;
            log(Prefix + $"draw observed {updates} update(s) and {Milliseconds(drawMilliseconds, "F0")} ms after the request; " +
                (updates <= DrawUpdateLimit && drawMilliseconds <= DrawMillisecondLimit ? "within" : "outside") + " the limits.");
        }
        else if (update - requested > DrawUpdateLimit || drawMilliseconds > DrawMillisecondLimit)
        {
            drawJudged = true;
            log(Prefix + $"draw not observed within {DrawUpdateLimit} updates or {Milliseconds(DrawMillisecondLimit / 1000, "F0")} s.");
        }
    }

    private void LogBaseline(string phase, HumanProviderBaseline captured, IReadOnlyList<KeyValuePair<string, string>> facts)
    {
        log(Prefix + $"{phase}: {HumanPuppetSlotRange.Count} slots; Penumbra unanswered {captured.Penumbra.Count(value => value is null)}, " +
            $"set {captured.Penumbra.Count(value => value is not null and not -1)}; Customize+ unanswered {captured.CustomizePlus.Count(value => value is null)}, " +
            $"set {captured.CustomizePlus.Count(value => value is not null and not -1)}; Customize+ API {Version(captured.CustomizePlusApiVersion)}; " +
            $"Glamourer API {Version(captured.GlamourerApiVersion)}; CanUnlock(0, 0) " +
            (captured.GlamourerCanUnlockBaseline is { } unlock ? $"locked {unlock.IsLocked}, can unlock {unlock.CanUnlock}" : "unanswered") + ".");
        foreach (var fact in facts)
            log(Prefix + $"{phase} {fact.Key}: {fact.Value}");
    }

    private void LogFactDifferences(string phase, IReadOnlyList<KeyValuePair<string, string>> facts)
    {
        if (baselineFacts is null)
            return;
        var before = baselineFacts.ToDictionary(fact => fact.Key, fact => fact.Value, StringComparer.Ordinal);
        // An unanswered read is never evidence: it is not measured, even when the baseline was unanswered too.
        var unmeasured = facts.Where(fact => Unanswered(fact.Value) || (before.TryGetValue(fact.Key, out var value) && Unanswered(value)))
            .Select(fact => fact.Key).Distinct().ToArray();
        var differing = facts.Where(fact => !unmeasured.Contains(fact.Key) && (!before.TryGetValue(fact.Key, out var value) || value != fact.Value))
            .Select(fact => fact.Key)
            .Concat(before.Keys.Where(key => facts.All(fact => fact.Key != key))).Distinct().ToArray();
        if (differing.Length == 0 && unmeasured.Length == 0)
            log(Prefix + $"{phase}: every run fact equals the baseline.");
        if (differing.Length > 0)
            log(Prefix + $"{phase}: run facts that differ from the baseline: {string.Join(", ", differing)}.");
        if (unmeasured.Length > 0)
            log(Prefix + $"{phase}: run facts not measured (a read was unanswered): {string.Join(", ", unmeasured)}.");
    }

    private void LogNewReadings()
    {
        var all = Host.Readings;
        for (; loggedReadings < all.Count; loggedReadings++)
        {
            var reading = all[loggedReadings];
            // Every update's F2 reading is logged only when it does not resolve; the rest are counted in the host.
            if (reading.Phase == "F2 tick" && reading.Resolved)
                continue;
            log(Prefix + $"reading {reading.Phase} on update {reading.Update}, slot {reading.GlobalIndex}: " +
                $"Penumbra {Value(reading.Penumbra)}, Customize+ {Value(reading.CustomizePlus)}.");
        }
    }

    private void LogStopOnce()
    {
        if (stopLogged || Host.Stop is not { } stop)
            return;
        stopLogged = true;
        log(Prefix + "STOP " + DescribeStop(stop) + $". Residue: {Host.Residue.Count}. Nothing is retried.");
    }

    private void Refuse(string step, string reason) => log(Prefix + $"{step} refused: {reason}.");

    private static int IndexOf(string step)
    {
        for (var i = 0; i < Steps.Count; i++)
            if (Steps[i] == step)
                return i;
        return -1;
    }

    private static string Describe(HumanPuppetOutcome outcome) =>
        outcome.Stop is { } stop ? DescribeStop(stop) : outcome.Refusal.ToString();

    private static string DescribeStop(HumanPuppetStopRecord stop) => $"{stop.Rule} at {stop.Step} on update {stop.Update} ({stop.Detail})";

    private static bool Unanswered(string value) =>
        value.StartsWith("unanswered", StringComparison.Ordinal) || value.StartsWith("unreadable", StringComparison.Ordinal);

    private static string Version((int Major, int Minor)? version) => version is { } value ? $"{value.Major}.{value.Minor}" : "unanswered";

    private static string Value(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unanswered";

    private static string Milliseconds(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
#endif
