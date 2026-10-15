#nullable enable
using System.Numerics;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;
using FakeBackend = RSyncStageCraft.Tests.ActorLocalObjectHostTests.FakeBackend;
using LiveObject = RSyncStageCraft.Tests.ActorLocalObjectHostTests.LiveObject;

namespace RSyncStageCraft.Tests;

/// <summary>
/// Loops-only animation through the actor host, against fake backends.
/// The reserved freeze and look-at fields are refused (RSC has neither).
/// </summary>
public sealed class ActorControlTests
{
    private static readonly ReceiverSessionKey Session = new(1, 1, 1);
    /// <summary>The shipped allowlist's verified loop (battle/idle) for BNpcBase 2 and 71.</summary>
    private const ushort BattleIdle = 34;
    private const ushort Loop = 600;
    private const ushort OtherLoop = 601;
    private const ushort Once = 700;
    private const ushort OtherOnce = 701;
    /// <summary><c>emote/dance_male_loop</c>: on a monster in game it sat in a slot while the body stood stuck.</summary>
    private const ushort DanceMaleLoop = 570;
    /// <summary><c>emote/dance_female_start</c> (a one-shot) on the monster, a known negative.</summary>
    private const ushort DanceFemaleStart = 571;

    private static readonly string[] StartLoop = ["Mode:AnimLock", $"BaseOverride:{Loop}", $"Play:{Loop}"];
    private static readonly string[] Clear = ["BaseOverride:0", "Mode:Normal"];

    [Fact]
    public void DefaultControlsAlwaysDrawEvenWhenNoControlPathIsAvailable()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend) { Unresolved = "not resolved" };
        var host = Host(backend, controls, ActorControlVerification.Current);
        var actor = Actor();
        Draw(host, backend, actor);
        Assert.True(host.IsDrawn(actor.ObjectId));
        Assert.DoesNotContain(backend.Calls, IsControlWrite);
    }

    [Fact]
    public void AnAnimationNotVerifiedInGameIsRefusedAndTheActorIsNeverCreated()
    {
        // The path resolved but is not verified in game.
        var backend = new FakeBackend();
        var host = Host(backend, new FakeControls(backend), ActorControlVerification.None);
        var actor = Actor() with { Controls = Controls("loop") };

        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(actor)).Status);
        Assert.Equal(ActorLocalObjectHost.AnimationUnavailableReason, Assert.Single(host.Unsupported).Reason);
        Assert.Empty(backend.Objects);
        Assert.DoesNotContain(backend.Calls, call => call == "Create");
    }

    [Fact]
    public void ThisBuildVerifiesAllowlistedLoopsOnlyAndHasNoFreeze()
    {
        // Freeze and look-at removed; animation
        // verified for the allowlisted pairs only.
        Assert.Equal(new ActorControlVerification(Animation: true), ActorControlVerification.Current);
        var backend = new FakeBackend();
        var host = Host(backend, new FakeControls(backend), ActorControlVerification.Current);
        var drawn = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        Draw(host, backend, drawn);
        Assert.True(host.IsDrawn(drawn.ObjectId));
        Assert.Equal((Loop, (byte)8), (Assert.Single(backend.Objects.Values).BaseOverride, backend.Objects.Values.Single().Mode));
        Assert.Equal(new ActorControlAvailability(null), host.ControlAvailability());
        Assert.DoesNotContain("look", host.DescribeControlPaths(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("freeze removed (reserved field)", host.DescribeControlPaths());
        Assert.DoesNotContain(backend.Calls, call => call.Contains("Speed"));
    }

    [Fact]
    public void AnUnresolvedAnimationPathIsRefusedEvenWhenVerifiedAndDefaultActorsStillDraw()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend) { Unresolved = "missing on this version" };
        var host = Host(backend, controls, ActorControlVerification.All);
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(Actor() with { Controls = Controls("loop") })).Status);
        Assert.Equal("missing on this version", Assert.Single(host.Unsupported).Reason);

        var drawn = Actor();
        Draw(host, backend, drawn);
        Assert.True(host.IsDrawn(drawn.ObjectId));
    }

    [Fact]
    public void AStandingLoopIsAppliedWhileInvisibleWithTheProofBeforeEveryNativeWrite()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 3) };

        Draw(host, backend, actor);

        var live = Assert.Single(backend.Objects.Values);
        Assert.Equal((Loop, (byte)8), (live.BaseOverride, live.Mode));
        Assert.Equal(1f, live.Alpha);
        // Every single native write or call is preceded by the full proof.
        var calls = backend.Calls.ToArray();
        ActorLocalObjectHostTests.AssertEveryNativeWriteFollowsAFullProof(calls);
        Assert.All(backend.Touched, item => Assert.Equal(live.Address, item.Address));
        // An allowlisted loop, while invisible, then alpha 1 once it held in
        // its slot; no runtime check (the allowlist is the proof).
        ActorLocalObjectHostTests.AssertEachWriteFollowsAFullProof(calls,
            ["SetAlpha:0", "Setup:100", "SetTransform", "SetAlpha:0", "EnableDraw", .. StartLoop, "SetAlpha:1"]);
    }

    [Theory]
    [InlineData("Mode:AnimLock")]
    [InlineData("BaseOverride:600")]
    [InlineData("Play:600")]
    public void AReplacementBetweenTwoControlWritesWhileApplyingStopsEveryLaterWrite(string afterStep)
    {
        var backend = new FakeBackend { ReuseAddresses = true, FixedEntityId = 0xE000_0000 };
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        var (now, mark) = ReplaceAfter(backend, live, afterStep);

        // The last step has no later write to stop; the earlier ones do.
        if (afterStep == "Play:600")
            host.ApplyOwned(handle, actor with { Revision = new(2), Controls = new(false, null, Loop, 1) });
        else
            Assert.Throws<InvalidOperationException>(() => host.ApplyOwned(handle,
                actor with { Revision = new(2), Controls = new(false, null, Loop, 1) }));
        backend.AfterWrite = null;

        Assert.True(mark() >= 0);
        ActorLocalObjectHostTests.AssertNoWriteAfter(backend, mark());
        Assert.Same(now(), Assert.Single(backend.Objects.Values));
        if (afterStep != "Play:600")
            Assert.Equal(1, host.AbandonedCount);
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        ActorLocalObjectHostTests.AssertNoWriteAfter(backend, mark());
    }

    [Theory]
    [InlineData("Mode:AnimLock")]
    [InlineData("BaseOverride:600")]
    public void AReplacementBetweenTwoStepsOfALoopReplayStopsEveryLaterWrite(string afterStep)
    {
        var backend = new FakeBackend { ReuseAddresses = true, FixedEntityId = 0xE000_0000 };
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var retired = 0;
        host.InstanceRetired += () => retired++;
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        // The game drops the loop: the next check plays it again.
        live.ActiveTimelines.Remove(Loop);
        var (now, mark) = ReplaceAfter(backend, live, afterStep);

        for (var frame = 0; frame <= ActorLocalObjectHost.LoopCheckIntervalFrames; frame++)
            host.Tick();
        backend.AfterWrite = null;

        Assert.True(mark() >= 0);
        ActorLocalObjectHostTests.AssertNoWriteAfter(backend, mark());
        Assert.Same(now(), Assert.Single(backend.Objects.Values));
        Assert.Equal(1, retired);
        Assert.Equal(1, host.AbandonedCount);
    }

    [Theory]
    [InlineData("BaseOverride:0")]
    [InlineData("Mode:Normal")]
    public void AReplacementBetweenTwoResetStepsOnReleaseStopsEveryLaterStepAndTheDelete(string afterStep)
    {
        var backend = new FakeBackend { ReuseAddresses = true, FixedEntityId = 0xE000_0000 };
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 1) };
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        var comIndex = live.ComIndex;
        var (now, mark) = ReplaceAfter(backend, live, afterStep);

        var released = host.ReleaseOwned(handle);
        backend.AfterWrite = null;

        Assert.True(mark() >= 0);
        Assert.Equal(ActorLocalObjectHost.AbandonedNotice + ".", released.Error);
        ActorLocalObjectHostTests.AssertNoWriteAfter(backend, mark());
        Assert.DoesNotContain(backend.Calls, call => call == $"Delete:{comIndex}");
        Assert.Same(now(), Assert.Single(backend.Objects.Values));
        Assert.Equal(1, host.AbandonedCount);
    }

    [Fact]
    public void AReplacementBetweenTheTwoStepsOfALiveClearStopsTheLaterWrite()
    {
        var backend = new FakeBackend { ReuseAddresses = true, FixedEntityId = 0xE000_0000 };
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 1) };
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        var (now, mark) = ReplaceAfter(backend, live, "BaseOverride:0");

        Assert.Throws<InvalidOperationException>(() => host.ApplyOwned(handle,
            actor with { Revision = new(2), Controls = new ActorControlState(false, null, null, 1) }));
        backend.AfterWrite = null;

        Assert.True(mark() >= 0);
        ActorLocalObjectHostTests.AssertNoWriteAfter(backend, mark());
        Assert.Same(now(), Assert.Single(backend.Objects.Values));
        Assert.Equal(1, host.AbandonedCount);
    }

    [Fact]
    public void NoPerFrameWriteTouchesADrawnActorWhoseLoopHolds()
    {
        // No freeze: a drawn actor's frames only read (the loop check), never write.
        var backend = new FakeBackend();
        var host = Host(backend, new FakeControls(backend), ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        Draw(host, backend, actor);
        var calls = backend.Calls.Count;

        for (var frame = 0; frame < 100; frame++)
            host.Tick();

        Assert.DoesNotContain(backend.Calls.Skip(calls), ActorLocalObjectHostTests.IsNativeWrite);
        Assert.True(host.IsDrawn(actor.ObjectId));
    }

    // ---- Production: the curated allowlist is the only proof ---------------

    [Fact]
    public void AnAllowlistedRowPlaysAtOnceWithoutHidingOrARuntimeCheck()
    {
        var backend = new FakeBackend();
        var host = Host(backend, new FakeControls(backend), ActorControlVerification.All);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        var calls = backend.Calls.Count;

        host.ApplyOwned(handle, actor with { Revision = new(2), Controls = new(false, null, Loop, 1) });
        for (var frame = 0; frame < 5; frame++)
            host.Tick();

        Assert.Equal(StartLoop, backend.Calls.Skip(calls).Where(ActorLocalObjectHostTests.IsNativeWrite).ToArray());
        Assert.Equal(1f, live.Alpha);
        Assert.Equal(new[] { Loop, OtherLoop }, host.AllowedAnimations(100).Select(row => row.TimelineId));
        Assert.All(host.AllowedAnimations(100), row => Assert.True(row.IsLoop));
        Assert.Equal(new[] { Loop }, host.AllowedAnimations(101).Select(row => row.TimelineId));
        Assert.Empty(host.AllowedAnimations(999));
    }

    [Fact]
    public void ARowNotOnTheAllowlistIsRefusedAtSpawnAndLiveRow570Included()
    {
        var backend = new FakeBackend();
        var host = Host(backend, new FakeControls(backend), ActorControlVerification.All);
        // Row 570 (emote/dance_male_loop) on a monster: never allowlisted.
        var dance = Actor() with { Controls = new ActorControlState(false, null, DanceMaleLoop, 1) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(dance)).Status);
        Assert.Equal(ActorLocalObjectHost.AnimationNotAllowedReason, Assert.Single(host.Unsupported).Reason);
        Assert.Empty(backend.Objects);

        // An allowlisted row for another creature is not allowed for this one.
        var other = Actor() with { BNpcBaseId = 101, Controls = new ActorControlState(false, null, OtherLoop, 0) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(other)).Status);
        Assert.Contains(host.Unsupported, report => report.ObjectId == other.ObjectId &&
            report.Reason == ActorLocalObjectHost.AnimationNotAllowedReason);

        // Live: a drawn actor given a row not on the allowlist is faulted (released, reported).
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var calls = backend.Calls.Count;
        Assert.Throws<InvalidOperationException>(() =>
            host.ApplyOwned(handle, actor with { Revision = new(2), Controls = new(false, null, DanceMaleLoop, 1) }));
        Assert.DoesNotContain(backend.Calls.Skip(calls), ActorLocalObjectHostTests.IsNativeWrite);
        Assert.Contains(host.Unsupported, report => report.ObjectId == actor.ObjectId &&
            report.Reason == ActorLocalObjectHost.AnimationNotAllowedReason);
    }

    [Fact]
    public void AnotherGameVersionDisablesAnimationOnlyAndDefaultActorsStillDraw()
    {
        var backend = new FakeBackend();
        var host = Host(backend, new FakeControls(backend), ActorControlVerification.All, gameVersion: "2099.01.01.0000.0000");
        var looping = Actor() with { Controls = new ActorControlState(false, null, Loop, 1) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(looping)).Status);
        Assert.Equal(ActorLocalObjectHost.AnimationNotVerifiedForVersionReason, Assert.Single(host.Unsupported).Reason);
        Assert.Empty(host.AllowedAnimations(100));
        Assert.Equal(ActorLocalObjectHost.AnimationNotVerifiedForVersionReason, host.AnimationUnavailableReasonForUi());

        var plain = Actor();
        Draw(host, backend, plain);
        Assert.True(host.IsDrawn(plain.ObjectId));
        // A frozen actor is refused for the reserved field, whatever the version.
        var frozen = Actor() with { Controls = new ActorControlState(true, null, null, 0) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(frozen)).Status);
        Assert.Contains(host.Unsupported, report => report.ObjectId == frozen.ObjectId &&
            report.Reason == ActorLocalObjectHost.FreezeNotSupportedReason);
    }

    [Fact]
    public void ARowWhoseGameDataNoLongerMatchesTheVerifiedBehaviourIsRefused()
    {
        var backend = new FakeBackend();
        // Verified as a loop, but this client's game data says one-shot.
        var changed = new ActorAnimationAllowlist(TestGameVersion, [new(100, Once, true)]);
        var host = Host(backend, new FakeControls(backend), ActorControlVerification.All, changed);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Once, 1) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(actor)).Status);
        Assert.Equal(ActorLocalObjectHost.AnimationDataChangedReason, Assert.Single(host.Unsupported).Reason);
        Assert.Empty(host.AllowedAnimations(100));
    }

    [Fact]
    public void TheAllowlistIsPinnedWellFormedAndRejectsBadEntries()
    {
        Assert.Equal("2026.09.15.0000.0000", ActorAnimationAllowlist.PinnedGameVersion);
        Assert.Equal(ActorAnimationAllowlist.PinnedGameVersion, ActorAnimationAllowlist.Current.GameVersion);
        Assert.True(ActorAnimationAllowlist.Current.MatchesGameVersion("2026.09.15.0000.0000"));
        Assert.False(ActorAnimationAllowlist.Current.MatchesGameVersion("2026.09.15.0000.0001"));
        Assert.False(ActorAnimationAllowlist.Current.MatchesGameVersion(null));
        Assert.Throws<ArgumentException>(() => new ActorAnimationAllowlist("v", [new(0, 1, true)]));
        Assert.Throws<ArgumentException>(() => new ActorAnimationAllowlist("v", [new(1, 0, true)]));
        Assert.Throws<ArgumentException>(() => new ActorAnimationAllowlist("v",
            [new(1, 2, true), new(1, 2, true)]));
        Assert.Throws<ArgumentException>(() => new ActorAnimationAllowlist(" ", []));
        // Loops only: a one-shot cannot be listed.
        var oneShot = Assert.Throws<ArgumentException>(() => new ActorAnimationAllowlist("v", [new(1, 2, false)]));
        Assert.Contains("one-shots are deferred", oneShot.Message);
        Assert.True(TestAllowlist.Allows(100, Loop));
        Assert.False(TestAllowlist.Allows(100, DanceMaleLoop));
        Assert.False(TestAllowlist.Allows(101, OtherLoop));
        Assert.False(TestAllowlist.Allows(100, Once));
    }

    [Fact]
    public void TheShippedAllowlistHoldsExactlyTheTwoVerifiedLoops()
    {
        var shipped = ActorAnimationAllowlist.Current;
        Assert.Equal(2, shipped.Count);
        var monster = shipped.Find(2, 34)!;
        var demihuman = shipped.Find(71, 34)!;
        Assert.True(monster.IsLoop);
        Assert.True(demihuman.IsLoop);
        // Known negatives for the tested monster stay refused.
        Assert.False(shipped.Allows(2, DanceMaleLoop));
        Assert.False(shipped.Allows(2, DanceFemaleStart));
        Assert.Empty(shipped.ForCreature(1));
    }

    [Theory]
    [InlineData(Once)]
    [InlineData(DanceFemaleStart)]
    public void AOneShotIsRefusedAtSpawnAndLiveAndIsNeverPlayed(ushort row)
    {
        // No one-shot is played (no animation and
        // so no attached sound).
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var request = new ActorControlState(false, null, row, 0);

        // From a snapshot: never created.
        var spawned = Actor() with { Controls = request };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(spawned)).Status);
        Assert.Equal(ActorLocalObjectHost.OneShotUnavailableReason, Assert.Single(host.Unsupported).Reason);
        Assert.Empty(backend.Objects);
        Assert.DoesNotContain(backend.Calls, call => call == "Create");

        // Live: refused before any write.
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        var calls = backend.Calls.Count;
        Assert.Throws<InvalidOperationException>(() =>
            host.ApplyOwned(handle, actor with { Revision = new(2), Controls = request }));
        host.Tick();
        Assert.DoesNotContain(backend.Calls.Skip(calls), ActorLocalObjectHostTests.IsNativeWrite);
        Assert.Empty(live.Played);
        Assert.Contains(host.Unsupported, report => report.ObjectId == actor.ObjectId &&
            report.Reason == ActorLocalObjectHost.OneShotUnavailableReason);
    }

    [Fact]
    public void ALoopIsStandingStateOnEveryDrawAndClearingUsesNoIdleTimeline()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 2) };
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        Assert.Equal((Loop, (byte)8), (live.BaseOverride, live.Mode));

        // A transform change does not restart the loop, and neither does the
        // reserved sequence (it has no production behaviour).
        var calls = backend.Calls.Count;
        host.ApplyOwned(handle, actor with { Revision = new(2), Yaw = 1f });
        host.ApplyOwned(handle, actor with { Revision = new(3), Yaw = 1f, Controls = new(false, null, Loop, 9) });
        Assert.DoesNotContain(backend.Calls.Skip(calls), IsControlWrite);
        Assert.Equal(new ushort[] { Loop }, live.Played);

        // Another allowlisted loop: clear, then start the new one.
        host.ApplyOwned(handle, actor with { Revision = new(4), Controls = new(false, null, OtherLoop, 0) });
        string[] switched = [.. Clear, "Mode:AnimLock", $"BaseOverride:{OtherLoop}", $"Play:{OtherLoop}"];
        Assert.Equal(switched, backend.Calls.Skip(calls).Where(IsControlWrite).ToArray());

        // Clear: base override 0, then normal mode; nothing is played.
        calls = backend.Calls.Count;
        host.ApplyOwned(handle, actor with { Revision = new(5), Controls = new(false, null, null, 0) });
        Assert.Equal(((ushort)0, (byte)1), (live.BaseOverride, live.Mode));
        Assert.Equal(Clear, backend.Calls.Skip(calls).Where(IsControlWrite).ToArray());

        // Drawn again (reconnect): the loop is applied again on that draw.
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Draw(host, backend, actor with { Revision = new(6) });
        Assert.Equal(Loop, Assert.Single(backend.Objects.Values).BaseOverride);
    }

    [Fact]
    public void ReleaseClearsControlStateFirstThenResetsEachSingleStepBehindTheProof()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 1) };
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        var calls = backend.Calls.Count;

        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);

        ActorLocalObjectHostTests.AssertEachWriteFollowsAFullProof(backend.Calls.Skip(calls).ToArray(),
            [.. Clear, "SetAlpha:0", "DisableDraw", "Delete:5"]);
        Assert.Equal(((ushort)0, (byte)1), (live.BaseOverride, live.Mode));
        // No per-frame step after the release.
        calls = backend.Calls.Count;
        host.Tick();
        Assert.Equal(calls, backend.Calls.Count);
    }

    [Fact]
    public void AControlStepThatOnlyThrowsDoesNotStopTheRestOfTheReleaseWhileTheProofHolds()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        var handle = Draw(host, backend, actor);
        controls.FailCall = "Mode:Normal";
        var calls = backend.Calls.Count;

        var released = host.ReleaseOwned(handle);

        Assert.NotNull(released.Error);
        Assert.NotEqual(ActorLocalObjectHost.AbandonedNotice + ".", released.Error);
        ActorLocalObjectHostTests.AssertEachWriteFollowsAFullProof(backend.Calls.Skip(calls).ToArray(),
            [.. Clear, "SetAlpha:0", "DisableDraw", "Delete:5"]);
        Assert.Empty(backend.Objects);
    }

    [Fact]
    public void AProofLostBetweenFramesStopsEveryControlStepAndAbandonsTheRecord()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var retired = 0;
        host.InstanceRetired += () => retired++;
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        // Another party's object now holds the slot, address and ids, with another tag.
        backend.DeleteExternally(live);
        var other = backend.PlaceForeign(live.Address, live.ComIndex, live.GlobalIndex, live.EntityId, ActorOwnershipTag.Create());
        var touched = backend.Touched.Count;

        for (var frame = 0; frame <= ActorLocalObjectHost.LoopCheckIntervalFrames; frame++)
            host.Tick();

        Assert.Equal(1, retired);
        Assert.Equal(touched, backend.Touched.Count);
        Assert.Empty(other.Played);
        Assert.Equal(1, host.AbandonedCount);
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Assert.Equal(touched, backend.Touched.Count);
        Assert.Same(other, Assert.Single(backend.Objects.Values));
    }

    [Fact]
    public void AControlFaultReleasesOnlyThatInstanceAndUnknownRowsAreRefused()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var retired = 0;
        host.InstanceRetired += () => retired++;
        var kept = Actor();
        Draw(host, backend, kept);
        var looping = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        Draw(host, backend, looping);
        var live = backend.Objects.Values.Single(item => item.Played.Count > 0);
        live.ActiveTimelines.Remove(Loop);
        controls.FailCall = $"Play:{Loop}";

        for (var frame = 0; frame <= ActorLocalObjectHost.LoopCheckIntervalFrames && retired == 0; frame++)
            host.Tick();

        Assert.Equal(1, retired);
        Assert.True(host.IsDrawn(kept.ObjectId));
        Assert.False(host.IsDrawn(looping.ObjectId));
        Assert.Single(backend.Objects);

        // An ActionTimeline row this client does not know is never guessed.
        var unknown = Actor() with { Controls = new ActorControlState(false, null, 9999, 1) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(unknown)).Status);
        Assert.Contains(host.Unsupported, report => report.ObjectId == unknown.ObjectId &&
            report.Reason == ActorLocalObjectHost.UnknownAnimationReason);
    }

    [Fact]
    public void ALiveControlThisClientCannotApplyFaultsTheDrawnActorAndIsReported()
    {
        var backend = new FakeBackend();
        var host = Host(backend, new FakeControls(backend), ActorControlVerification.None);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var calls = backend.Calls.Count;
        Assert.Throws<InvalidOperationException>(() =>
            host.ApplyOwned(handle, actor with { Revision = new(2), Controls = new(false, null, Loop, 0) }));
        Assert.DoesNotContain(backend.Calls.Skip(calls), ActorLocalObjectHostTests.IsNativeWrite);
        Assert.Contains(host.Unsupported, report => report.Reason == ActorLocalObjectHost.AnimationUnavailableReason);
    }

    // ---- Drawing an actor with a standing loop ------------------------------

    [Fact]
    public void AStandingLoopWaitsForTheCreaturesAnimationToRunSteadilyAndToHoldBeforeTheActorIsShown()
    {
        // A non-owner's client redrawing from a snapshot: the actor's owner is
        // someone else; drawing never depends on ownership. The creature's
        // animation flickers at first: it must run on consecutive frames.
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        foreach (var ready in new[] { false, false, false, true, true, true, true, true, false })
            controls.ReadySequence.Enqueue(ready);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { OwnerId = new("someone-else"), Controls = new ActorControlState(false, null, Loop, 0) };
        var (handle, live) = Begin(host, backend, actor);

        var polls = 0;
        var heldFrames = 0;
        while (host.ApplyOwned(handle, actor).Status != ApplyPortStatus.Succeeded)
        {
            // Never shown, and nothing played, until the animation ran steadily.
            Assert.NotEqual(1f, live.Alpha);
            if (backend.Calls.Count(call => call == "AnimationReady") < 9 + ActorLocalObjectHost.ReadyStableFrames)
                Assert.DoesNotContain(backend.Calls, IsControlWrite);
            if (live.ActiveTimelines.Contains(Loop))
                heldFrames++;
            host.Tick();
            Assert.True(++polls < 100);
        }

        var calls = backend.Calls.ToArray();
        // 9 flickering reads, then the stable run.
        Assert.Equal(9 + ActorLocalObjectHost.ReadyStableFrames, calls.Count(call => call == "AnimationReady"));
        Assert.True(Array.LastIndexOf(calls, "AnimationReady") < Array.IndexOf(calls, "Mode:AnimLock"));
        // Shown only after the loop held in its slot on consecutive frames.
        Assert.True(heldFrames >= ActorLocalObjectHost.LoopHoldFrames);
        Assert.True(Array.IndexOf(calls, $"Play:{Loop}") < Array.IndexOf(calls, "SetAlpha:1"));
        Assert.Equal((Loop, (byte)8, 1f), (live.BaseOverride, live.Mode, live.Alpha));
        ActorLocalObjectHostTests.AssertEveryNativeWriteFollowsAFullProof(calls);
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ALoopWhoseFirstPlaysDoNotLandIsPlayedAgainUntilItHoldsAndOnlyThenShown(int ignoredPlays)
    {
        // Observed in game: the demihuman's loop did not take on a fresh actor.
        // A play the game ignores is repeated (at most MaximumLoopReplays
        // times) while the actor stays hidden.
        var backend = new FakeBackend();
        var controls = new FakeControls(backend) { IgnoreFirstPlays = ignoredPlays };
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        var (handle, live) = Begin(host, backend, actor);

        var polls = 0;
        while (host.ApplyOwned(handle, actor).Status != ApplyPortStatus.Succeeded)
        {
            Assert.NotEqual(1f, live.Alpha);
            host.Tick();
            Assert.True(++polls < ActorLocalObjectHost.StandingControlsDeadlineFrames + ActorLocalObjectHost.LoopConfirmDeadlineFrames);
        }

        Assert.Equal(Enumerable.Repeat(Loop, ignoredPlays + 1), live.Played);
        Assert.Contains(Loop, live.ActiveTimelines);
        Assert.Equal(1f, live.Alpha);
        Assert.Empty(host.ControlFailures);
        ActorLocalObjectHostTests.AssertEveryNativeWriteFollowsAFullProof(backend.Calls.ToArray());
    }

    [Fact]
    public void ALoopTheGameDropsRightAfterItLandsIsNotShownUntilItHoldsAgain()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend) { DropLoopOnceAfterReads = 3 };
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        var (handle, live) = Begin(host, backend, actor);

        var polls = 0;
        while (host.ApplyOwned(handle, actor).Status != ApplyPortStatus.Succeeded)
        {
            Assert.NotEqual(1f, live.Alpha);
            host.Tick();
            Assert.True(++polls < 200);
        }

        Assert.Equal(new ushort[] { Loop, Loop }, live.Played);
        Assert.Contains(Loop, live.ActiveTimelines);
        Assert.Equal(1f, live.Alpha);
    }

    [Fact]
    public void ACreatureWhoseAnimationNeverRunsIsReleasedListedTriedOnceMoreThenRefused()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend) { NeverReady = true };
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };

        for (var attempt = 1; attempt <= ActorLocalObjectHost.StandingControlsAttempts; attempt++)
        {
            var (handle, live) = Begin(host, backend, actor);
            Assert.True(FailsClosed(host, handle, actor, live, ActorLocalObjectHost.StandingControlsDeadlineFrames + 20));
            // Released by RSC before it was listed: hidden, undrawn, deleted.
            Assert.Empty(backend.Objects);
            Assert.Equal((0f, false), (live.Alpha, live.DrawEnabled));
            var failure = host.ControlFailures[^1];
            Assert.Equal((attempt, false, true), (failure.Attempt, failure.WasShown, failure.Released));
            Assert.Contains("its animation was not running on", failure.Detail);
            Assert.Equal("receipt matched yes; control reset yes, hidden yes, undrawn yes, deleted yes", failure.Release);
            Assert.Equal(attempt < ActorLocalObjectHost.StandingControlsAttempts
                    ? ActorLocalObjectHost.StandingControlsRetryingReason
                    : ActorLocalObjectHost.StandingControlsFailedReason,
                Assert.Single(host.Unsupported, report => report.ObjectId == actor.ObjectId).Reason);
            // The receiver's release finds nothing left.
            Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        }
        Assert.DoesNotContain(backend.Calls, IsControlWrite);
        Assert.DoesNotContain("SetAlpha:1", backend.Calls);
        // Refused with the reason until its controls or the location change.
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(actor)).Status);
        // An actor with default controls does not wait at all.
        controls.NeverReady = false;
        var plain = Actor();
        Draw(host, backend, plain);
        Assert.True(host.IsDrawn(plain.ObjectId));
        // Other controls are tried again; so is everything after a location change.
        Assert.Equal(ApplyPortStatus.AwaitingPayload,
            host.SpawnOwned(Request(actor with { Revision = new(2), Controls = new ActorControlState(false, null, OtherLoop, 0) })).Status);
        host.ClearUnsupported();
        Assert.Equal(ApplyPortStatus.AwaitingPayload, host.SpawnOwned(Request(Actor() with { Controls = actor.Controls })).Status);
    }

    [Fact]
    public void ALoopThatNeverHoldsIsPlayedAgainAtMostFourTimesThenReleasedAndListedWithoutAVisiblePuppet()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend) { Missing = { Loop } };
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        var (handle, live) = Begin(host, backend, actor);

        Assert.True(FailsClosed(host, handle, actor, live,
            ActorLocalObjectHost.StandingControlsDeadlineFrames + ActorLocalObjectHost.LoopConfirmDeadlineFrames));

        Assert.Equal(Enumerable.Repeat(Loop, 1 + ActorLocalObjectHost.MaximumLoopReplays), live.Played);
        Assert.Empty(backend.Objects);
        Assert.Equal((0f, false), (live.Alpha, live.DrawEnabled));
        var failure = Assert.Single(host.ControlFailures);
        Assert.True(failure.Released);
        Assert.Contains($"its loop (row {Loop}) did not hold in a timeline slot within", failure.Detail);
        Assert.Contains($"in a slot on 0 frame(s), longest run 0 (needs {ActorLocalObjectHost.LoopHoldFrames}), " +
            $"{ActorLocalObjectHost.MaximumLoopReplays} replay(s); rows in slots []", failure.Detail);
        Assert.Contains($"row {Loop} in no slot", failure.Detail);
        Assert.Equal(ActorLocalObjectHost.StandingControlsRetryingReason,
            Assert.Single(host.Unsupported, report => report.ObjectId == actor.ObjectId).Reason);
        ActorLocalObjectHostTests.AssertEveryNativeWriteFollowsAFullProof(backend.Calls.ToArray());
    }

    [Fact]
    public void ADrawnActorWhoseLoopTheGameDroppedIsPlayedAgainOnTheNextCheck()
    {
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        Assert.Equal(new ushort[] { Loop }, live.Played);

        live.ActiveTimelines.Remove(Loop);
        for (var frame = 0; frame <= ActorLocalObjectHost.LoopCheckIntervalFrames; frame++)
            host.Tick();

        Assert.Equal(new ushort[] { Loop, Loop }, live.Played);
        Assert.Contains(Loop, live.ActiveTimelines);
        Assert.Equal(1f, live.Alpha);
        ActorLocalObjectHostTests.AssertEveryNativeWriteFollowsAFullProof(backend.Calls.ToArray());
    }

    [Fact]
    public void AnEarlierObjectLeftUntouchedStaysListedWhenALaterInstanceFailsItsLoop()
    {
        // Observed in a retest: A's monster was listed "not drawn" while a
        // puppet stayed visible. RSC never touches an object it can no longer
        // prove is its own, so the listing must keep saying so.
        var backend = new FakeBackend();
        var controls = new FakeControls(backend);
        var host = Host(backend, controls, ActorControlVerification.All);
        var actor = Actor() with { Controls = new ActorControlState(false, null, Loop, 0) };
        Draw(host, backend, actor);
        var earlier = Assert.Single(backend.Objects.Values);
        ActorLocalObjectHostTests.Mutate(backend, earlier, "tag", setUp: true);
        // The next loop check notices the lost proof.
        for (var frame = 0; frame <= ActorLocalObjectHost.LoopCheckIntervalFrames; frame++)
            host.Tick();
        var orphan = Assert.Single(host.Abandoned);
        Assert.Equal("its RSC tag no longer matches", orphan.ProofFailure);
        var touched = backend.Touched.Count(item => item.Address == earlier.Address);

        // A new instance of the same actor cannot hold its loop.
        controls.Missing.Add(Loop);
        var later = actor with { Revision = new(2) };
        var (handle, live) = Begin(host, backend, later);
        Assert.True(FailsClosed(host, handle, later, live,
            ActorLocalObjectHost.StandingControlsDeadlineFrames + ActorLocalObjectHost.LoopConfirmDeadlineFrames));

        var report = Assert.Single(host.Unsupported, item => item.ObjectId == actor.ObjectId);
        Assert.Equal($"{ActorLocalObjectHost.StandingControlsRetryingReason}; {ActorLocalObjectHost.UntouchedEarlierObjectNote}", report.Reason);
        // The earlier object was never touched again; the failed instance was released.
        Assert.Equal(touched, backend.Touched.Count(item => item.Address == earlier.Address));
        Assert.Equal(1f, earlier.Alpha);
        Assert.Equal(0f, live.Alpha);
        Assert.True(Assert.Single(host.ControlFailures).Released);
    }

    /// <summary>Polls and runs frames until the apply fails closed (true) or the budget runs out; never shown meanwhile.</summary>
    private static bool FailsClosed(ActorLocalObjectHost host, LocalObjectInstanceHandle handle, ActorState actor, LiveObject live, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            try
            {
                Assert.Equal(ApplyPortStatus.AwaitingPayload, host.ApplyOwned(handle, actor).Status);
            }
            catch (InvalidOperationException)
            {
                Assert.NotEqual(1f, live.Alpha);
                return true;
            }
            Assert.NotEqual(1f, live.Alpha);
            host.Tick();
        }
        return false;
    }

    // ---- Production composition (the plugin's host and panel) ---------------

    [Theory]
    [InlineData(2u)]   // Monster m0024 b1 v1
    [InlineData(71u)]  // Demihuman d1005 b1 v1
    public void TheProductionCompositionOffersRow34OnThePinnedVersionAndNoFreeze(uint creature)
    {
        // The same composition the plugin uses (verified paths, shipped
        // allowlist), with only the native backends faked; dev tools off.
        var backend = new FakeBackend();
        var host = ProductionHost(backend, ActorAnimationAllowlist.PinnedGameVersion);

        var offer = host.ControlOfferFor(creature);
        Assert.Null(offer.AnimationUnavailable);
        var loop = Assert.Single(offer.Animations);
        Assert.Equal((creature, BattleIdle, true), (loop.BNpcBaseId, loop.TimelineId, loop.IsLoop));
        Assert.Contains("freeze removed (reserved field); animation available (verified)", host.DescribeControlPaths());

        // A synchronized loop-only actor draws with row 34.
        var looping = Actor() with { BNpcBaseId = creature, Controls = new ActorControlState(false, null, BattleIdle, 0) };
        Draw(host, backend, looping);
        var live = Assert.Single(backend.Objects.Values);
        Assert.Equal(new ushort[] { BattleIdle }, live.Played);
        Assert.Equal((BattleIdle, 1f), (live.BaseOverride, live.Alpha));

        // Frozen (reserved), one-shots and rows 570/571 are refused for this creature.
        var frozen = Actor() with { BNpcBaseId = creature, Controls = new ActorControlState(true, null, BattleIdle, 0) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(frozen)).Status);
        Assert.Contains(host.Unsupported, report => report.ObjectId == frozen.ObjectId &&
            report.Reason == ActorLocalObjectHost.FreezeNotSupportedReason);
        foreach (var row in new ushort[] { DanceMaleLoop, DanceFemaleStart, Once, OtherOnce, Loop })
        {
            Assert.DoesNotContain(offer.Animations, allowed => allowed.TimelineId == row);
            var refused = Actor() with { BNpcBaseId = creature, Controls = new ActorControlState(false, null, row, 0) };
            Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(refused)).Status);
        }
        Assert.Single(backend.Objects);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(71u)]
    public void AnotherGameVersionDisablesTheAnimationEntriesAndDefaultActorsStillDraw(uint creature)
    {
        var backend = new FakeBackend();
        var host = ProductionHost(backend, "2026.09.22.0000.0000");

        var offer = host.ControlOfferFor(creature);
        Assert.Equal(ActorLocalObjectHost.AnimationNotVerifiedForVersionReason, offer.AnimationUnavailable);
        Assert.Empty(offer.Animations);
        var plain = Actor() with { BNpcBaseId = creature };
        Draw(host, backend, plain);
        Assert.True(host.IsDrawn(plain.ObjectId));
        var looping = Actor() with { BNpcBaseId = creature, Controls = new ActorControlState(false, null, BattleIdle, 0) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(looping)).Status);
        Assert.Equal(ActorLocalObjectHost.AnimationNotVerifiedForVersionReason, Assert.Single(host.Unsupported).Reason);
    }

    [Fact]
    public void ACreatureWithoutAllowlistedLoopsOffersNoneAndAnUnknownVersionDisablesAnimation()
    {
        var backend = new FakeBackend();
        var offer = ProductionHost(backend, ActorAnimationAllowlist.PinnedGameVersion).ControlOfferFor(4242);
        Assert.Null(offer.AnimationUnavailable);
        Assert.Empty(offer.Animations);
        var unknown = ProductionHost(new FakeBackend(), null).ControlOfferFor(2);
        Assert.Equal(ActorLocalObjectHost.AnimationNotVerifiedForVersionReason, unknown.AnimationUnavailable);
    }

    [Fact]
    public void NoFreezeOrLookAtControlOrRuntimePathExists()
    {
        // The panel's offer, the availability, the verification, the control
        // backend expose no freeze, speed or look-at member;
        // the host only names the two refusals.
        static IEnumerable<string> Names(Type type) => type.GetMembers().Select(member => member.Name);
        static bool Reserved(string name) =>
            name.Contains("Look", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Freeze", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Frozen", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Speed", StringComparison.OrdinalIgnoreCase);
        foreach (var type in new[] { typeof(ActorControlOffer), typeof(ActorControlAvailability), typeof(ActorControlVerification),
                     typeof(IActorControlBackend) })
            Assert.DoesNotContain(Names(type), Reserved);
        Assert.Equal(new[] { nameof(ActorLocalObjectHost.FreezeNotSupportedReason), nameof(ActorLocalObjectHost.LookAtNotSupportedReason) },
            Names(typeof(ActorLocalObjectHost)).Where(Reserved).Order().ToArray());
        // A received look-at value is refused by the production composition too.
        var backend = new FakeBackend();
        var host = ProductionHost(backend, ActorAnimationAllowlist.PinnedGameVersion);
        var looking = Actor() with { BNpcBaseId = 2, Controls = new ActorControlState(true, new(1, 1, 1), BattleIdle, 0) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(looking)).Status);
        Assert.Equal(ActorLocalObjectHost.LookAtNotSupportedReason, Assert.Single(host.Unsupported).Reason);
        Assert.Empty(backend.Objects);
    }

    // ---- The development switch in the production composition

    [Fact]
    public void ADevPluginLoadGetsDevelopmentToolsOnlyInADebugBuild()
    {
        // Exactly what PluginConnection passes: DevelopmentBuild.ToolsEnabled(IsDev).
        var backend = new FakeBackend();
        var devPluginLoad = ProductionHost(backend, ActorAnimationAllowlist.PinnedGameVersion,
            DevelopmentBuild.ToolsEnabled(isDevPlugin: true));
        var normalLoad = ProductionHost(backend, ActorAnimationAllowlist.PinnedGameVersion,
            DevelopmentBuild.ToolsEnabled(isDevPlugin: false));
        Assert.False(normalLoad.DevelopmentToolsEnabled);
#if RSC_DEVELOPMENT
        Assert.True(DevelopmentBuild.Enabled);
        Assert.True(devPluginLoad.DevelopmentToolsEnabled);
#else
        // A Release build loaded as a dev plugin gets no development tools.
        Assert.False(DevelopmentBuild.Enabled);
        Assert.False(devPluginLoad.DevelopmentToolsEnabled);
#endif
    }

    private static ActorLocalObjectHost ProductionHost(FakeBackend backend, string? gameVersion, bool developmentTools) =>
        ActorHostComposition.Create(backend, id => new ActorDrawPlanResult(
                new ActorDrawPlan(id, ActorModelKind.Monster, 7, 24, 1, 1, 1.5f, 0, null), null),
            new FakeControls(backend), new FakeTimelines(), gameVersion, developmentTools: developmentTools, activeLimit: 8);

    private static ActorLocalObjectHost ProductionHost(FakeBackend backend, string? gameVersion) =>
        ActorHostComposition.Create(backend, id => new ActorDrawPlanResult(
                new ActorDrawPlan(id, ActorModelKind.Monster, 7, 24, 1, 1, 1.5f, 0, null), null),
            new FakeControls(backend), new FakeTimelines(), gameVersion, developmentTools: false, activeLimit: 8);

    // ---- The reserved fields: RSC has no look-at and no freeze

    [Theory]
    [InlineData("alone", true)]
    [InlineData("freeze", true)]
    [InlineData("loop", true)]
    [InlineData("alone", false)] // a client without any control backend
    public void AReceivedLookAtIsRefusedBeforeAnythingIsTouchedOrDrawn(string with, bool hasControls)
    {
        var backend = new FakeBackend();
        var host = Host(backend, hasControls ? new FakeControls(backend) : null, ActorControlVerification.All);
        var controls = with switch
        {
            "freeze" => new ActorControlState(true, new(1, 1, 1), null, 0),
            "loop" => new ActorControlState(false, new(1, 1, 1), Loop, 0),
            _ => Controls("lookat"),
        };
        AssertRefusedBeforeAnythingIsTouched(host, backend, controls, ActorLocalObjectHost.LookAtNotSupportedReason);
    }

    [Theory]
    [InlineData("alone", true)]
    [InlineData("loop", true)]
    [InlineData("alone", false)] // a client without any control backend
    public void AReceivedFreezeIsRefusedBeforeAnythingIsTouchedOrDrawn(string with, bool hasControls)
    {
        var backend = new FakeBackend();
        var host = Host(backend, hasControls ? new FakeControls(backend) : null, ActorControlVerification.All);
        var controls = with == "loop" ? new ActorControlState(true, null, Loop, 0) : Controls("freeze");
        AssertRefusedBeforeAnythingIsTouched(host, backend, controls, ActorLocalObjectHost.FreezeNotSupportedReason);
    }

    /// <summary>From a snapshot: refused before creation. Live: a drawn actor receiving it is refused before any write.</summary>
    private static void AssertRefusedBeforeAnythingIsTouched(ActorLocalObjectHost host, FakeBackend backend,
        ActorControlState controls, string reason)
    {
        var spawned = Actor() with { Controls = controls };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(spawned)).Status);
        Assert.Equal(reason, Assert.Single(host.Unsupported).Reason);
        Assert.Empty(backend.Objects);
        Assert.DoesNotContain(backend.Calls, call => call == "Create");

        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        var calls = backend.Calls.Count;
        Assert.Throws<InvalidOperationException>(() =>
            host.ApplyOwned(handle, actor with { Revision = new(2), Controls = controls }));
        host.Tick();
        Assert.DoesNotContain(backend.Calls.Skip(calls), ActorLocalObjectHostTests.IsNativeWrite);
        Assert.Empty(live.Played);
        Assert.Equal(((ushort)0, (byte)1), (live.BaseOverride, live.Mode));
        Assert.Contains(host.Unsupported, report => report.ObjectId == actor.ObjectId && report.Reason == reason);
    }

    [Fact]
    public async Task TheEditorNeverSendsTheReservedLookAtOrFreezeFields()
    {
        var sent = 0;
        var editor = new RSyncStageCraft.Core.Furniture.FurnitureEditor((_, _) =>
        {
            sent++;
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Completed));
        });
        var actor = Actor();
        foreach (var patch in new[]
        {
            new ActorControlPatch(null, new(1, 2, 3), false, null, false),
            new ActorControlPatch(true, new(1, 2, 3), false, null, false),
            new ActorControlPatch(null, null, true, null, false),
            new ActorControlPatch(null, new(1, 2, 3), false, Loop, false),
            new ActorControlPatch(true, null, false, null, false),
            new ActorControlPatch(false, null, false, null, false),
            new ActorControlPatch(true, null, false, Loop, false),
        })
        {
            var result = await editor.SetActorControlsAsync(actor, patch);
            Assert.Equal(ServerCommandStatus.Invalid, result.Status);
        }
        Assert.Equal(0, sent);
        Assert.Equal("Freeze is not supported.",
            (await editor.SetActorControlsAsync(actor, new ActorControlPatch(true, null, false, Loop, false))).Detail);
        // A loop is still sent, and its clear.
        Assert.Equal(ServerCommandStatus.Completed,
            (await editor.SetActorControlsAsync(actor, new ActorControlPatch(null, null, false, Loop, false))).Status);
        Assert.Equal(1, sent);
    }

    private static bool IsControlWrite(string call) =>
        ActorLocalObjectHostTests.IsNativeWrite(call) && !call.StartsWith("SetTransform") && !call.StartsWith("SetAlpha") &&
        call != "EnableDraw" && call != "DisableDraw" && !call.StartsWith("Setup") && !call.StartsWith("Delete") && call != "Create";

    /// <summary>
    /// Right after the first <paramref name="afterStep"/> write, another party
    /// replaces the object (same address, COM slot, global index and ids,
    /// another tag). Returns the replacement and the call count at that moment.
    /// </summary>
    private static (Func<LiveObject?> Now, Func<int> Mark) ReplaceAfter(FakeBackend backend, LiveObject live, string afterStep)
    {
        LiveObject? now = null;
        var mark = -1;
        backend.AfterWrite = call =>
        {
            if (call != afterStep || now is not null)
                return;
            now = ActorLocalObjectHostTests.Mutate(backend, live, "replaced-same-ids-other-tag", setUp: true);
            mark = backend.Calls.Count;
        };
        return (() => now, () => mark);
    }

    private static ActorControlState Controls(string control) => control switch
    {
        "freeze" => new(true, null, null, 0),
        "lookat" => new(false, new(1, 1, 1), null, 0),
        "loop" => new(false, null, Loop, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    private const string TestGameVersion = "test-version";

    /// <summary>The tests' allowlist (loops only): Loop and OtherLoop for creature 100, Loop for 101; never rows 570 or 571.</summary>
    private static readonly ActorAnimationAllowlist TestAllowlist = new(TestGameVersion,
    [
        new(100, Loop, true),
        new(100, OtherLoop, true),
        new(101, Loop, true),
    ]);

    private static ActorLocalObjectHost Host(FakeBackend backend, FakeControls? controls, ActorControlVerification verification,
        ActorAnimationAllowlist? allowlist = null, string gameVersion = TestGameVersion) =>
        new(backend, id => new ActorDrawPlanResult(
                new ActorDrawPlan(id, ActorModelKind.Monster, 7, 24, 1, 1, 1.5f, 0, null), null),
            activeLimit: 8, controls: controls, timelines: new FakeTimelines(), verification: verification,
            allowlist: allowlist ?? TestAllowlist, gameVersion: gameVersion);

    private static ActorState Actor() => new(new(Guid.NewGuid()), new(1), new("owner"), null, 100,
        new(1, 2, 3), 0.25f, 1, ActorControlState.Default);

    private static LocalObjectSpawnRequest Request(ActorState actor) => new(
        new LocalObjectApplyToken(Session, actor.ObjectId, new ApplyGeneration(1)),
        LocalObjectSpawnIdentity.FromState(actor),
        1,
        actor);

    /// <summary>Spawns the actor and makes its object ready; the caller polls.</summary>
    private static (LocalObjectInstanceHandle Handle, LiveObject Live) Begin(ActorLocalObjectHost host, FakeBackend backend, ActorState actor)
    {
        var spawned = host.SpawnOwned(Request(actor));
        Assert.Equal(ApplyPortStatus.AwaitingPayload, spawned.Status);
        var live = backend.Objects.Values.Single(item => item.ObjectHandle == Guid.Empty && !item.Foreign);
        live.ObjectHandle = spawned.Value.OwnershipId;
        live.ReadyToDraw = true;
        live.ModelLoaded = true;
        return (spawned.Value, live);
    }

    /// <summary>Spawns and builds the actor, running a frame between polls as the game does.</summary>
    private static LocalObjectInstanceHandle Draw(ActorLocalObjectHost host, FakeBackend backend, ActorState actor)
    {
        var (handle, _) = Begin(host, backend, actor);
        // Enough frames for the drawing steps and the dev check.
        for (var poll = 0; poll < 400; poll++)
        {
            if (host.ApplyOwned(handle, actor).Status == ApplyPortStatus.Succeeded)
                return handle;
            host.Tick();
        }
        throw new InvalidOperationException("The actor did not draw.");
    }

    private sealed class FakeTimelines : IActorTimelineData
    {
        private static readonly ActorTimelineRow[] Rows =
        [
            new(BattleIdle, "battle/idle", true),
            new(Loop, "loop", true),
            new(OtherLoop, "idle2", true),
            new(Once, "once", false),
            new(OtherOnce, "other", false),
            new(DanceMaleLoop, "emote/dance_male_loop", true),
            new(DanceFemaleStart, "emote/dance_female_start", false),
        ];

        public bool TryGetTimeline(ushort timelineId, out ActorTimelineRow row)
        {
            row = Rows.FirstOrDefault(item => item.Id == timelineId)!;
            return row is not null;
        }

        // As the plugin's data: looping rows only.
        public IReadOnlyList<ActorTimelineRow> Search(string text, int limit) =>
            Rows.Where(row => row.IsLoop)
                .Where(row => row.Key.Contains(text, StringComparison.OrdinalIgnoreCase) || row.Id.ToString() == text)
                .Take(limit).ToArray();
    }

    /// <summary>The animation primitives on the fake scene's objects: one logged write or call each, with its address.</summary>
    internal sealed class FakeControls(FakeBackend scene) : IActorControlBackend
    {
        /// <summary>The animation path is unresolved with this reason (null: resolved).</summary>
        public string? Unresolved { get; set; }
        /// <summary>This call is recorded, then throws (a native step that fails while the proof holds).</summary>
        public string? FailCall { get; set; }
        /// <summary>Rows this creature's skeleton lacks: playing them shows nothing in any slot.</summary>
        public HashSet<ushort> Missing { get; } = new();
        /// <summary>Per object, the first this-many plays land in no slot (a freshly drawn creature ignoring them).</summary>
        public int IgnoreFirstPlays { get; set; }
        /// <summary>Per object, once: after a row was read in its slot this many times, the game drops it.</summary>
        public int? DropLoopOnceAfterReads { get; set; }
        /// <summary>Readiness answers given first, in order (then the settings below).</summary>
        public Queue<bool> ReadySequence { get; } = new();
        /// <summary>Reads before the creature's animation counts as started.</summary>
        public int NotReadyReads { get; set; }
        /// <summary>The creature's animation never starts.</summary>
        public bool NeverReady { get; set; }
        private ulong nextControl = 1000;
        private readonly Dictionary<nint, int> plays = new();
        private readonly Dictionary<(nint, ushort), int> slotReads = new();
        private readonly HashSet<nint> dropped = new();

        public ActorControlAvailability Availability => new(Unresolved);

        private void Call(string call, nint address, Action change)
        {
            scene.RecordWrite(call, address);
            if (FailCall == call)
                throw new InvalidOperationException("native step failed");
            change();
            scene.Wrote(call);
        }

        public void SetAnimationLock(nint address, bool locked) =>
            Call(locked ? "Mode:AnimLock" : "Mode:Normal", address, () => Live(address).Mode = locked ? (byte)8 : (byte)1);

        public void SetBaseOverride(nint address, ushort timelineId) => Call($"BaseOverride:{timelineId}", address, () =>
        {
            var live = Live(address);
            if (timelineId == 0)
                live.ActiveTimelines.Remove(live.BaseOverride);
            live.BaseOverride = timelineId;
        });

        public void PlayTimeline(nint address, ushort timelineId) => Call($"Play:{timelineId}", address, () =>
        {
            var live = Live(address);
            live.Played.Add(timelineId);
            live.Timestamps[timelineId] = 0f;
            var played = plays[address] = plays.GetValueOrDefault(address) + 1;
            if (played <= IgnoreFirstPlays)
                return;
            // Each play of a playable row starts a new body control.
            if (!Missing.Contains(timelineId))
            {
                live.RowControls[timelineId] = ++nextControl;
                live.ActiveTimelines.Add(timelineId);
            }
            else
                live.RowControls.Remove(timelineId);
        });

        public IReadOnlyList<ushort> ReadActiveTimelines(nint address)
        {
            var live = Live(address);
            if (DropLoopOnceAfterReads is { } after && !dropped.Contains(address))
            {
                foreach (var row in live.ActiveTimelines.ToArray())
                {
                    var reads = slotReads[(address, row)] = slotReads.GetValueOrDefault((address, row)) + 1;
                    if (reads >= after)
                    {
                        dropped.Add(address);
                        live.ActiveTimelines.Remove(row);
                    }
                }
            }
            return live.ActiveTimelines.Order().ToArray();
        }

        public bool IsAnimationReady(nint address)
        {
            scene.RecordRead("AnimationReady");
            _ = Live(address);
            if (ReadySequence.Count > 0)
                return ReadySequence.Dequeue();
            if (NeverReady)
                return false;
            if (NotReadyReads > 0)
            {
                NotReadyReads--;
                return false;
            }
            return true;
        }

        /// <summary>
        /// One read per frame: each active row's own scheduler timestamp advances.
        /// </summary>
        public IReadOnlyList<ActorTimelineSlotState> ReadTimelineSlots(nint address)
        {
            var live = Live(address);
            foreach (var id in live.ActiveTimelines)
                live.Timestamps[id] = live.Timestamps.GetValueOrDefault(id) + 0.016f;
            return live.ActiveTimelines.Order()
                .Select((id, slot) => new ActorTimelineSlotState(slot, id, true, $"key{id}",
                    live.Timestamps.GetValueOrDefault(id), "fake.tmb", 7, true))
                .ToArray();
        }

        /// <summary>
        /// The body's idle (identity 1) keeps advancing whatever row was played,
        /// as seen in game; each playable row's own control (a new identity per
        /// play) follows that row's timeline.
        /// </summary>
        public ActorAnimationSample ReadAnimationSample(nint address)
        {
            var live = Live(address);
            live.Clock += 0.016f;
            var controls = new List<ActorAnimationControlState> { new(1, live.Clock, 1f, 1f, true, 3f) };
            foreach (var (row, identity) in live.RowControls)
            {
                if (live.ActiveTimelines.Contains(row))
                    controls.Add(new(identity, live.Timestamps.GetValueOrDefault(row), 1f, 1f, true, 2f));
            }
            return new ActorAnimationSample(true, controls);
        }

        private LiveObject Live(nint address) => scene.Objects[address];
    }
}
