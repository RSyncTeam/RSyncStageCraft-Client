#nullable enable
using System.Numerics;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Lights;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

/// <summary>The actor host against a fake ClientObjectManager backend.</summary>
public sealed class ActorLocalObjectHostTests
{
    private static readonly ReceiverSessionKey Session = new(1, 1, 1);

    [Fact]
    public void AnAnimationSampleNeverNamesAControlIdentity()
    {
        var sample = new ActorAnimationSample(true,
            [new ActorAnimationControlState(22, 1.6f, 1f, 1f, true, 3f), new ActorAnimationControlState(11, 0.5f, 1f, 1f, true, 3f)]);

        Assert.DoesNotContain("22", sample.Describe());
        Assert.DoesNotContain("11", sample.Describe());
    }

    [Fact]
    public void SpawnVerifiesTheCreatedObjectAndBuildsItHiddenUntilDrawn()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor(scale: 2);

        var spawned = host.SpawnOwned(Request(actor));
        Assert.Equal(ApplyPortStatus.AwaitingPayload, spawned.Status);
        var handle = spawned.Value;
        Assert.Equal(RscObjectKind.Actor, handle.Kind);
        var created = Assert.Single(backend.Objects.Values);
        Assert.Equal(0f, created.Alpha);
        Assert.False(created.DrawEnabled);
        Assert.Equal(100u, created.SetupBase);
        Assert.False(host.IsDrawn(actor.ObjectId));

        // Not ready to draw yet: still waiting, still invisible.
        Assert.Equal(ApplyPortStatus.AwaitingPayload, host.ApplyOwned(handle, actor).Status);
        created.ReadyToDraw = true;
        Assert.Equal(ApplyPortStatus.AwaitingPayload, host.ApplyOwned(handle, actor).Status);
        Assert.True(created.DrawEnabled);
        Assert.Equal(0f, created.Alpha);
        // Drawn, but the body is not the creature's yet.
        Assert.Equal(ApplyPortStatus.AwaitingPayload, host.ApplyOwned(handle, actor).Status);
        created.ModelLoaded = true;
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(handle, actor).Status);
        Assert.Equal(1f, created.Alpha);
        Assert.True(host.IsDrawn(actor.ObjectId));
        // Row scale (1.5) times wire scale (2).
        Assert.Equal(3f, created.Scale);
        Assert.Equal(new Vector3(1, 2, 3), created.Position);
        Assert.DoesNotContain("SetAlpha:1", backend.Calls.TakeWhile(call => call != "EnableDraw"));
    }

    [Theory]
    [InlineData(199)]
    [InlineData(449)]
    [InlineData(0)]
    public void AnOutOfRangeGlobalIndexDeletesOnlyThatComObjectAndIsNotRetried(int globalIndex)
    {
        var backend = new FakeBackend { NextGlobalIndex = globalIndex };
        var bystander = backend.PlaceForeign(0x6600, 40, 260, 0xE000_0200, "Bystander");
        var host = Host(backend);
        var spawned = host.SpawnOwned(Request(Actor()));

        // Deterministic: the allocator would return the same object again, so
        // the refusal is permanent and nothing loops.
        Assert.Equal(ApplyPortStatus.PermanentFailure, spawned.Status);
        Assert.False(spawned.Value.IsValid);
        Assert.Same(bystander, Assert.Single(backend.Objects.Values));
        Assert.Equal(new[] { "Create", "Delete:5" }, backend.Calls.Where(call => call.StartsWith("Create") || call.StartsWith("Delete")));
        var attempt = Assert.Single(host.CreationAttempts);
        Assert.Equal("the actual global index is outside 200–448", attempt.FailedGate);
        Assert.True(attempt.Permanent);
        Assert.DoesNotContain(backend.Calls, call => call.StartsWith("Setup") || call.StartsWith("SetAlpha") || call.StartsWith("SetTransform"));
        Assert.Equal(0, host.OwnedCount);
    }

    [Fact]
    public void TheAutomaticAllocatorsReturnedIndexIsUsedEvenWhenNoSlotWasPredicted()
    {
        // The allocator picks COM index 137, not the first free slot; RSC
        // retrieves exactly that object and checks its actual global index.
        var backend = new FakeBackend { AllocatorSlot = 137 };
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        Assert.Equal(137, live.ComIndex);
        Assert.Equal(337, live.GlobalIndex);
        var attempt = Assert.Single(host.CreationAttempts);
        Assert.True(attempt.Succeeded);
        Assert.Equal((137, true, true, 137, 337, true, true, false, "BattleNpc (2)"),
            (attempt.ReturnedComIndex, attempt.ComIndexInRange, attempt.ObjectReturned, attempt.IndexRoundTrip,
             attempt.GlobalIndex, attempt.GlobalIndexInRange, attempt.TableMatches, attempt.IsLocalPlayer, attempt.Kind));
        Assert.DoesNotContain(backend.Calls, call => call.StartsWith("Next"));
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Assert.Contains("Delete:137", backend.Calls);
        Assert.Empty(backend.Objects);
    }

    [Fact]
    public void TheFirstClientObjectSlotAtGlobalIndex200IsAccepted()
    {
        // In-game testing: the automatic allocator returns COM slot 0 at global index 200.
        var backend = new FakeBackend { AllocatorSlot = 0 };
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        Assert.Equal((0, 200), (live.ComIndex, live.GlobalIndex));
        var attempt = Assert.Single(host.CreationAttempts);
        Assert.True(attempt.Succeeded);
        Assert.Equal((0, 200, true, "BattleNpc (2)"), (attempt.ReturnedComIndex, attempt.GlobalIndex, attempt.GlobalIndexInRange, attempt.Kind));
        Assert.Single(backend.Calls, call => call == "Create");
        Assert.DoesNotContain(backend.Calls, call => call.StartsWith("Delete"));
        Assert.True(host.IsDrawn(actor.ObjectId));
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Assert.Contains("Delete:0", backend.Calls);
        Assert.Equal(200, ActorSlotRange.MinimumGlobalIndex);
        Assert.True(ActorSlotRange.IsAcceptedGlobalIndex(200));
        Assert.False(ActorSlotRange.IsAcceptedGlobalIndex(199));
        Assert.False(ActorSlotRange.IsAcceptedGlobalIndex(449));
    }

    [Fact]
    public void AFreshEventNpcAtCom0Global200BecomesATaggedBattleNpcAndIsDrawn()
    {
        // In-game testing: the fresh object is EventNpc (3) at COM 0 / global 200.
        var backend = new FakeBackend { AllocatorSlot = 0, CreatedRawKind = 3 };
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        Assert.Equal((0, 200, 2), (live.ComIndex, live.GlobalIndex, live.RawKind));
        Assert.True(ActorOwnershipTag.IsWellFormed(live.Name));
        var attempt = Assert.Single(host.CreationAttempts);
        Assert.True(attempt.Succeeded);
        Assert.Equal(("EventNpc (3)", true, true), (attempt.Kind, attempt.SlotWasEmpty, attempt.RawEntryMatches));
        Assert.True(host.IsDrawn(actor.ObjectId));
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Assert.Contains("Delete:0", backend.Calls);
    }

    [Fact]
    public void EventNpcIsAcceptedOnlyBeforeSetupAndNeverForLaterWritesOrDeletion()
    {
        var backend = new FakeBackend { AllocatorSlot = 0, CreatedRawKind = 3 };
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        // After setup, an object that is (again) an EventNpc is not RSC's any
        // more, even with this spawn's tag: no write, no delete.
        Assert.True(ActorOwnershipTag.IsWellFormed(live.Name));
        live.RawKind = 3;
        AssertRefusedUntouched(host, backend, handle, actor);
        Assert.Same(live, Assert.Single(backend.Objects.Values));
    }

    [Theory]
    [InlineData(3)]   // still EventNpc
    [InlineData(0)]   // None
    [InlineData(1)]   // Pc
    public void SetupLeavingAFreshEventNpcAnythingButABattleNpcReleasesOnlyThatAllocation(int kindAfterSetup)
    {
        var backend = new FakeBackend { AllocatorSlot = 0, CreatedRawKind = 3, SetupResultKind = kindAfterSetup };
        var bystander = backend.PlaceForeign(0x6600, 40, 260, 0xE000_0200, "Event NPC");
        bystander.RawKind = 3;
        var host = Host(backend);
        var actor = Actor();
        var spawned = host.SpawnOwned(Request(actor));
        Assert.Equal(ApplyPortStatus.TransientFailure, spawned.Status);
        Assert.False(spawned.Value.IsValid);
        Assert.Equal(0, host.OwnedCount);
        Assert.Equal(new[] { "Delete:0" }, backend.Calls.Where(call => call.StartsWith("Delete")));
        Assert.Same(bystander, Assert.Single(backend.Objects.Values));
        Assert.DoesNotContain(backend.Calls, call => call.StartsWith("SetTransform") || call == "EnableDraw" || call == "SetAlpha:1");
        Assert.Contains(host.Unsupported, report => report.ObjectId == actor.ObjectId &&
            report.Reason.StartsWith(ActorLocalObjectHost.SetupFailedReason, StringComparison.Ordinal));
    }

    [Fact]
    public void APreExistingOrMismatchedEventNpcIsNeverAdoptedOrDeleted()
    {
        // A pre-existing event NPC stays untouched through a whole spawn and release.
        var backend = new FakeBackend { CreatedRawKind = 3 };
        var bystander = backend.PlaceForeign(0x6600, 40, 260, 0xE000_0200, "Event NPC");
        bystander.RawKind = 3;
        var host = Host(backend);
        var handle = Draw(host, backend, Actor());
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Assert.Same(bystander, Assert.Single(backend.Objects.Values));
        Assert.DoesNotContain("Delete:40", backend.Calls);

        // The object table names another event NPC at the global index: RSC's
        // own allocation is deleted, the other event NPC is untouched.
        var mismatched = new FakeBackend { CreatedRawKind = 3, TableMismatch = true };
        var other = mismatched.PlaceForeign(0x6700, 41, 205, 0xE000_0300, "Event NPC");
        other.RawKind = 3;
        var refusing = Host(mismatched);
        Assert.False(refusing.SpawnOwned(Request(Actor())).Value.IsValid);
        Assert.Equal("the object table entry at that global index is another object",
            Assert.Single(refusing.CreationAttempts).FailedGate);
        Assert.Same(other, Assert.Single(mismatched.Objects.Values));
        Assert.Equal(new[] { "Delete:5" }, mismatched.Calls.Where(call => call.StartsWith("Delete")));
    }

    [Fact]
    public void GlobalIndex200AliasingTheLocalPlayerIsRefusedWithoutDeletion()
    {
        var backend = new FakeBackend { AllocatorSlot = 0, CreatedIsLocalPlayer = true };
        var host = Host(backend);
        var spawned = host.SpawnOwned(Request(Actor()));
        Assert.False(spawned.Value.IsValid);
        var attempt = Assert.Single(host.CreationAttempts);
        Assert.Equal(200, attempt.GlobalIndex);
        Assert.Equal("the object is the local player", attempt.FailedGate);
        Assert.False(attempt.Deleted);
        Assert.DoesNotContain(backend.Calls, call => call.StartsWith("Delete") || call.StartsWith("Setup"));
        Assert.True(backend.Objects.ContainsKey(backend.LocalPlayer));
    }

    [Theory]
    [InlineData(1)]   // Pc
    [InlineData(3)]   // EventNpc
    [InlineData(0)]   // still None
    public void AfterSetupOnlyATaggedBattleNpcIsKept(int kindAfterSetup)
    {
        var backend = new FakeBackend { CreatedIsBattleNpc = false, CreatedKindUnset = true, SetupResultKind = kindAfterSetup };
        var host = Host(backend);
        var spawned = host.SpawnOwned(Request(Actor()));
        Assert.Equal(ApplyPortStatus.TransientFailure, spawned.Status);
        Assert.False(spawned.Value.IsValid);
        Assert.Equal(0, host.OwnedCount);
        Assert.Equal(new[] { "Delete:5" }, backend.Calls.Where(call => call.StartsWith("Delete")));
        Assert.DoesNotContain(backend.Calls, call => call.StartsWith("SetTransform") || call == "EnableDraw" || call == "SetAlpha:1");
    }

    [Theory]
    [InlineData("no-index", "the allocator returned no COM index in 0–248", false)]
    [InlineData("index-out-of-range", "the allocator returned no COM index in 0–248", false)]
    [InlineData("null-object", "GetObjectByIndex returned no object for that COM index", false)]
    [InlineData("round-trip", "GetIndexByObject does not give back the returned COM index", false)]
    [InlineData("table", "the object table entry at that global index is another object", true)]
    [InlineData("player", "the object is the local player", false)]
    [InlineData("kind", "the fresh object's kind is Pc (1), not an approved pre-setup kind (BattleNpc, None or EventNpc)", true)]
    [InlineData("kind-unknown", "the fresh object's kind is unknown (200), not an approved pre-setup kind (BattleNpc, None or EventNpc)", true)]
    [InlineData("kind-retainer", "the fresh object's kind is Retainer (10), not an approved pre-setup kind (BattleNpc, None or EventNpc)", true)]
    [InlineData("occupied", "the returned COM slot already held an object before the call", false)]
    [InlineData("raw-entry", "the raw COM entry does not hold the returned object", false)]
    public void EachCreationGateFailsClosedAndDeletesOnlyTheExactProvenObject(string fault, string gate, bool deleted)
    {
        var backend = new FakeBackend();
        switch (fault)
        {
            case "no-index": backend.ReturnIndex = -1; break;
            case "index-out-of-range": backend.ReturnIndex = 249; break;
            case "null-object": backend.ObjectMissing = true; break;
            case "round-trip": backend.RoundTripMismatch = true; break;
            case "table": backend.TableMismatch = true; break;
            case "player": backend.CreatedIsLocalPlayer = true; break;
            case "kind": backend.CreatedRawKind = 1; break;
            case "kind-unknown": backend.CreatedRawKind = 200; break;
            case "kind-retainer": backend.CreatedRawKind = 10; break;
            case "occupied":
                // A pre-existing event NPC in the slot the allocator names.
                backend.PlaceForeign(0x6600, 40, 260, 0xE000_0200, "Bystander").RawKind = 3;
                backend.ReturnIndex = 40;
                break;
            case "raw-entry": backend.RawEntryMismatch = true; break;
        }
        var host = Host(backend);
        var actor = Actor();
        var spawned = host.SpawnOwned(Request(actor));

        // Only a policy refusal (kind) is permanent; the others are retried.
        Assert.Equal(fault.StartsWith("kind", StringComparison.Ordinal) ? ApplyPortStatus.PermanentFailure : ApplyPortStatus.TransientFailure,
            spawned.Status);
        Assert.Single(backend.Calls, call => call == "Create");
        Assert.False(spawned.Value.IsValid);
        Assert.Equal(0, host.OwnedCount);
        Assert.DoesNotContain(backend.Calls, call => call.StartsWith("Setup") || call.StartsWith("SetAlpha") ||
            call.StartsWith("SetTransform"));
        var attempt = Assert.Single(host.CreationAttempts);
        Assert.Equal(gate, attempt.FailedGate);
        Assert.Equal(deleted, attempt.Deleted);
        Assert.Equal(deleted ? 1 : 0, backend.Calls.Count(call => call.StartsWith("Delete")));
        if (deleted)
            Assert.Empty(backend.Objects);
        if (fault == "player")
            Assert.True(backend.Objects.ContainsKey(backend.LocalPlayer));
        if (fault == "occupied")
            Assert.Equal(3, Assert.Single(backend.Objects.Values).RawKind);
        // The not-drawn text does not name
        // the gate (it stays verified in the attempt above and is logged): a
        // permanent check gives the permanent text, the others the retrying text.
        Assert.Contains(host.Unsupported, report => report.ObjectId == actor.ObjectId &&
            report.Reason == (fault.StartsWith("kind", StringComparison.Ordinal)
                ? "This client cannot draw this actor safely (the game data did not match what RSC expects). " +
                  "Update the plugin; Retry all scene objects in Scene here tries once more."
                : "Could not verify a safe slot to draw this actor yet; retrying."));
    }

    [Fact]
    public void ATransientCreationCheckThatKeepsFailingGivesTheExhaustedTextWithoutTheGate()
    {
        // Retrying and exhausted texts name no gate; the gate stays in the attempt history.
        var backend = new FakeBackend { ReturnIndex = -1 };
        var host = new ActorLocalObjectHost(backend, id => new ActorDrawPlanResult(
            new ActorDrawPlan(id, ActorModelKind.Monster, 7, 24, 1, 1, 1.5f, 0, null), null), creationAttempts: 2, activeLimit: 8);
        var actor = Actor();
        var request = Request(actor);

        Assert.Equal(ApplyPortStatus.TransientFailure, host.SpawnOwned(request).Status);
        var retrying = Assert.Single(host.Unsupported, report => report.ObjectId == actor.ObjectId);
        Assert.Equal(LocalObjectReportKind.Retrying, retrying.Kind);
        Assert.Equal("Could not verify a safe slot to draw this actor yet; retrying.", retrying.Reason);

        Assert.Equal(ApplyPortStatus.TransientFailure, host.SpawnOwned(request).Status);
        var exhausted = Assert.Single(host.Unsupported, report => report.ObjectId == actor.ObjectId);
        Assert.Equal(LocalObjectReportKind.Failed, exhausted.Kind);
        Assert.Equal("This actor could not be drawn after several tries. Use Retry all scene objects in Scene here.",
            exhausted.Reason);
        Assert.All(host.CreationAttempts, attempt =>
            Assert.Equal("the allocator returned no COM index in 0–248", attempt.FailedGate));
        Assert.DoesNotContain("allocator", exhausted.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedCreationCheckIsLoggedByItsGateInEveryBuild()
    {
        // Without the development tools only the failed gate is
        // logged; with them, the full attempt as before; nothing for a success.
        var backend = new FakeBackend { ReturnIndex = -1 };
        var host = Host(backend);
        host.SpawnOwned(Request(Actor()));
        var failed = Assert.Single(host.CreationAttempts);

        Assert.Equal($"RSC actor creation attempt #{failed.Sequence} failed a check: the allocator returned no COM index in 0–248.",
            failed.LogLine(developmentTools: false));
        Assert.Equal("RSC actor creation " + failed.Describe(), failed.LogLine(developmentTools: true));

        var permanent = failed with { Permanent = true };
        Assert.EndsWith("(not retried).", permanent.LogLine(developmentTools: false), StringComparison.Ordinal);
        var verified = failed with { FailedGate = null };
        Assert.Null(verified.LogLine(developmentTools: false));
    }

    [Fact]
    public void AFreshObjectWithoutAKindYetIsAcceptedAndOnlyAPlayerKindIsRefused()
    {
        var backend = new FakeBackend { CreatedIsBattleNpc = false, CreatedKindUnset = true };
        var host = Host(backend);
        var actor = Actor();
        Assert.Equal(ApplyPortStatus.AwaitingPayload, host.SpawnOwned(Request(actor)).Status);
        Assert.Equal(1, host.OwnedCount);
        Assert.Equal("None (0)", Assert.Single(host.CreationAttempts).Kind);
    }

    [Fact]
    public void NoFixedComToGlobalMappingIsAssumed()
    {
        // COM slot 5 maps to global 300 here, not 205: the host reads it.
        var backend = new FakeBackend { NextGlobalIndex = 300 };
        var host = Host(backend);
        var spawned = host.SpawnOwned(Request(Actor()));
        Assert.Equal(ApplyPortStatus.AwaitingPayload, spawned.Status);
        Assert.Equal(300, Assert.Single(backend.Objects.Values).GlobalIndex);
    }

    [Fact]
    public void OwnershipIsRecheckedBeforeEveryWriteAndTheTagAloneNeverMatches()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        Assert.True(ActorOwnershipTag.IsWellFormed(live.Name));

        // The same address with this spawn's tag but another EntityId is not
        // RSC's: nothing is written, nothing deleted.
        live.EntityId = 999;
        var calls = backend.Calls.Count;
        Assert.Throws<InvalidOperationException>(() => host.ApplyOwned(handle, actor with { Position = new(9, 9, 9) }));
        Assert.DoesNotContain(backend.Calls.Skip(calls), call => call.StartsWith("SetTransform") || call.StartsWith("Delete"));
        Assert.Equal(0, host.OwnedCount);
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Assert.DoesNotContain(backend.Calls.Skip(calls), call => call.StartsWith("Delete"));
        Assert.Single(backend.Objects);
        Assert.Contains(host.Unsupported, report => report.ObjectId == actor.ObjectId &&
            report.Reason == ActorLocalObjectHost.ReplacedReason);
    }

    [Fact]
    public void ReleaseClearsControlStateFirstAndDeletesThroughTheCreationSlot()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        Assert.True(host.IsOwnedAddress(live.Address));
        var calls = backend.Calls.Count;
        backend.BeforeEachCall = call =>
        {
            // Every native free step happens after the control/hook state is gone.
            if (call.StartsWith("SetAlpha") || call.StartsWith("DisableDraw") || call.StartsWith("Delete"))
                Assert.False(host.IsOwnedAddress(live.Address));
        };
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        backend.BeforeEachCall = null;
        var release = backend.Calls.Skip(calls).Where(call => !call.StartsWith("Read") && !call.StartsWith("Table") &&
            !call.StartsWith("ComEntry") && !call.StartsWith("ComObject") && !call.StartsWith("ComIndexOf")).ToArray();
        Assert.Equal(new[] { "SetAlpha:0", "DisableDraw", "Delete:5" }, release);
        Assert.Equal(0, backend.OrphanedDrawObjects);
        // The slot holding the address and the address being in that slot are
        // both re-read immediately before the delete.
        var beforeDelete = backend.Calls.Skip(calls).TakeWhile(call => call != "Delete:5").Reverse().Take(2).ToArray();
        Assert.Contains("ComObject:5", beforeDelete);
        Assert.Contains("ComIndexOf", beforeDelete);
        Assert.Empty(backend.Objects);
        Assert.False(host.IsDrawn(actor.ObjectId));
    }

    [Fact]
    public void ASlotReusedByAnotherObjectIsNeverWrittenOrDeleted()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        backend.DeleteExternally(live);
        var other = backend.PlaceForeign(0x7700, live.ComIndex, live.GlobalIndex, 0xE000_0100, "Other");

        AssertRefusedUntouched(host, backend, handle, actor);
        Assert.Same(other, Assert.Single(backend.Objects.Values));
    }

    [Fact]
    public void AllocatorAddressReuseWithIdenticalNativeFactsIsRefusedByTheTag()
    {
        var backend = new FakeBackend { FixedEntityId = 0xE000_0000 };
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        backend.DeleteExternally(live);
        // Same address, COM slot, global index, GameObjectId and EntityId; even
        // a name that looks like RSC's is not this spawn's tag.
        var other = backend.PlaceForeign(live.Address, live.ComIndex, live.GlobalIndex, live.EntityId, "RSC");

        AssertRefusedUntouched(host, backend, handle, actor);
        Assert.Same(other, Assert.Single(backend.Objects.Values));
    }

    [Fact]
    public void APointerMismatchInTheCreationSlotIsRefused()
    {
        foreach (var fault in new[] { "slot-holds-other", "object-in-other-slot" })
        {
            var backend = new FakeBackend();
            var host = Host(backend);
            var actor = Actor();
            var handle = Draw(host, backend, actor);
            var live = Assert.Single(backend.Objects.Values);
            if (fault == "slot-holds-other")
                backend.SetComEntry(live.ComIndex, 0x7777);
            else
                backend.MoveToComSlot(live, 17);

            AssertRefusedUntouched(host, backend, handle, actor);
            Assert.Single(backend.Objects);
        }
    }

    [Fact]
    public void AGlobalIndexReplacementIsRefused()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        backend.SetTableEntry(live.GlobalIndex, 0x8888);

        AssertRefusedUntouched(host, backend, handle, actor);
        Assert.Single(backend.Objects);
    }

    [Fact]
    public void ANewerRscGenerationTakingOverTheSameSlotIsNeverTouchedByTheOldOne()
    {
        // COM objects can share an EntityId and the allocator reuses addresses.
        var backend = new FakeBackend { ReuseAddresses = true, FixedEntityId = 0xE000_0000 };
        var older = Host(backend);
        var oldActor = Actor();
        var oldHandle = Draw(older, backend, oldActor);
        var oldLive = Assert.Single(backend.Objects.Values);
        var (address, comIndex, globalIndex, oldTag) = (oldLive.Address, oldLive.ComIndex, oldLive.GlobalIndex, oldLive.Name);
        backend.DeleteExternally(oldLive);

        var newer = Host(backend);
        var newActor = Actor();
        var newHandle = Draw(newer, backend, newActor);
        var newLive = Assert.Single(backend.Objects.Values);
        Assert.Equal((address, comIndex, globalIndex), (newLive.Address, newLive.ComIndex, newLive.GlobalIndex));
        Assert.NotEqual(oldTag, newLive.Name);

        AssertRefusedUntouched(older, backend, oldHandle, oldActor);
        Assert.Same(newLive, Assert.Single(backend.Objects.Values));
        Assert.Equal(1f, newLive.Alpha);
        Assert.True(newLive.DrawEnabled);
        Assert.Equal(0, older.DestroyAll());
        // The newer generation still frees its own.
        Assert.Equal(ApplyPortStatus.Succeeded, newer.ReleaseOwned(newHandle).Status);
        Assert.Empty(backend.Objects);
    }

    [Fact]
    public void AnActorThatIsNotABattleNpcWithItsTagAfterSetupReleasesOnlyThatAllocation()
    {
        foreach (var fault in new[] { "kind-still-unset", "no-tag" })
        {
            var backend = new FakeBackend { CreatedIsBattleNpc = false, CreatedKindUnset = true };
            if (fault == "kind-still-unset")
                backend.SetupLeavesKindUnset = true;
            else
                backend.SetupWritesNoTag = true;
            var bystander = backend.PlaceForeign(0x6600, 40, 260, 0xE000_0200, "Bystander");
            var host = Host(backend);
            var actor = Actor();
            var spawned = host.SpawnOwned(Request(actor));

            Assert.Equal(ApplyPortStatus.TransientFailure, spawned.Status);
            Assert.False(spawned.Value.IsValid);
            Assert.Equal(0, host.OwnedCount);
            Assert.Same(bystander, Assert.Single(backend.Objects.Values));
            Assert.Equal(new[] { "Delete:5" }, backend.Calls.Where(call => call.StartsWith("Delete")));
            Assert.DoesNotContain(backend.Calls, call => call.StartsWith("SetTransform") || call == "EnableDraw" || call == "SetAlpha:1");
            Assert.Contains(host.Unsupported, report => report.ObjectId == actor.ObjectId &&
                report.Reason.StartsWith(ActorLocalObjectHost.SetupFailedReason, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AKindChangeAfterSetupIsRefused()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        Assert.Single(backend.Objects.Values).IsBattleNpc = false;
        AssertRefusedUntouched(host, backend, handle, actor);
    }

    [Fact]
    public void OwnershipTagsAreRandomAndWellFormed()
    {
        var first = ActorOwnershipTag.Create();
        Assert.True(ActorOwnershipTag.IsWellFormed(first));
        Assert.Equal(ActorOwnershipTag.Length, first.Length);
        // 128 random bits as 32 hex digits after the prefix; with its terminator
        // it fits the game's 64-byte name buffer.
        Assert.Equal(128, ActorOwnershipTag.RandomBits);
        Assert.Equal(36, ActorOwnershipTag.Prefix.Length + ActorOwnershipTag.RandomBits / 4);
        Assert.True(ActorOwnershipTag.Length + 1 <= ActorOwnershipTag.GameNameBufferBytes);
        Assert.Equal(1000, Enumerable.Range(0, 1000).Select(_ => ActorOwnershipTag.Create()).Distinct().Count());
        Assert.NotEqual(first, ActorOwnershipTag.Create());
        Assert.False(ActorOwnershipTag.IsWellFormed("RSC"));
        Assert.False(ActorOwnershipTag.IsWellFormed("RSC#0123456789ABCDEF"));
        Assert.False(ActorOwnershipTag.IsWellFormed(null));
    }

    [Fact]
    public void AFailedSetupDeletesTheAllocationOnlyWhileItsIdentityStillHolds()
    {
        // Kind or model setup fails, but the creation slot, table entry and
        // address still identify the exact new allocation: it is deleted.
        var intact = new FakeBackend { FailSetup = true };
        var host = Host(intact);
        var spawned = host.SpawnOwned(Request(Actor()));
        Assert.Equal(ApplyPortStatus.TransientFailure, spawned.Status);
        Assert.False(spawned.Value.IsValid);
        Assert.Equal(new[] { "Delete:5" }, intact.Calls.Where(call => call.StartsWith("Delete")));
        Assert.Empty(intact.Objects);
        Assert.Equal(0, host.AbandonedCount);

        // The slot, pointer or table identity itself changed during setup: the
        // plugin can no longer prove which object it would delete, so it
        // deletes nothing and reports the orphan.
        foreach (var change in new[] { "slot-pointer", "object-moved", "table", "ids" })
        {
            var backend = new FakeBackend { FailSetup = change == "ids" };
            backend.DuringSetup = live =>
            {
                switch (change)
                {
                    case "slot-pointer": backend.SetComEntry(live.ComIndex, 0x7777); break;
                    case "object-moved": backend.MoveToComSlot(live, 17); break;
                    case "table": backend.SetTableEntry(live.GlobalIndex, 0x8888); break;
                    default: live.EntityId = 999; break;
                }
            };
            var changedHost = Host(backend);
            var actor = Actor();
            var result = changedHost.SpawnOwned(Request(actor));
            Assert.Equal(ApplyPortStatus.TransientFailure, result.Status);
            Assert.False(result.Value.IsValid);
            Assert.Equal(0, changedHost.OwnedCount);
            Assert.DoesNotContain(backend.Calls, call => call.StartsWith("Delete") || call.StartsWith("SetTransform") ||
                call == "EnableDraw" || call == "DisableDraw");
            Assert.Single(backend.Objects);
            Assert.Equal(1, changedHost.AbandonedCount);
            Assert.Equal(actor.ObjectId, Assert.Single(changedHost.Abandoned).ObjectId);
        }
    }

    [Fact]
    public void ALostOrOverwrittenTagStopsEveryWriteAndDeleteAndCleanupNeverCountsItFreed()
    {
        foreach (var tag in new string?[] { null, "Some Name", "RSC#" + new string('0', 32) })
        {
            var backend = new FakeBackend();
            var host = Host(backend);
            var lost = Actor();
            var lostHandle = Draw(host, backend, lost);
            var lostLive = backend.Objects.Values.Single(item => item.ObjectHandle == lostHandle.OwnershipId);
            var kept = Actor();
            Draw(host, backend, kept);
            lostLive.Name = tag;

            var calls = backend.Calls.Count;
            if (tag is null)
            {
                // A later update finds the tag gone: refused before any write.
                Assert.Throws<InvalidOperationException>(() => host.ApplyOwned(lostHandle, lost with { Revision = new(2), Yaw = 1f }));
                Assert.Equal(1, host.DestroyAll());
            }
            else
            {
                // Location change or unload finds it first: only the provable
                // actor is freed and counted; the other is retired untouched.
                Assert.Equal(1, host.DestroyAll());
                Assert.NotEqual(ApplyPortStatus.Succeeded,
                    host.ApplyOwned(lostHandle, lost with { Revision = new(2), Yaw = 1f }).Status);
            }
            Assert.Equal(lost.Yaw, lostLive.Yaw);
            Assert.Same(lostLive, Assert.Single(backend.Objects.Values));
            Assert.Equal(1f, lostLive.Alpha);
            Assert.True(lostLive.DrawEnabled);
            Assert.DoesNotContain(backend.Calls.Skip(calls), call => call == $"Delete:{lostLive.ComIndex}");
            Assert.Equal(1, host.AbandonedCount);
            Assert.Equal(lost.ObjectId, Assert.Single(host.Abandoned).ObjectId);
            // Kept across report clearing (a location change clears reports).
            host.ClearUnsupported();
            Assert.Equal(1, host.AbandonedCount);
            Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(lostHandle).Status);
            Assert.Same(lostLive, Assert.Single(backend.Objects.Values));
        }
    }

    [Fact]
    public void AbandonedRecordsAreBoundedPrunedReadOnlyAndDroppedHistoryIsCounted()
    {
        var backend = new FakeBackend();
        var host = Host(backend, activeLimit: 30);
        // Far more tag losses than the record limit.
        const int losses = 40;
        for (var index = 0; index < losses; index++)
        {
            var actor = Actor();
            var handle = Draw(host, backend, actor);
            backend.Objects.Values.Single(item => item.ObjectHandle == handle.OwnershipId).Name = "lost";
            Assert.Throws<InvalidOperationException>(() => host.ApplyOwned(handle, actor with { Revision = new(2), Yaw = 1f }));
        }
        Assert.Equal(ActorLocalObjectHost.MaximumAbandoned, host.Abandoned.Count);
        Assert.Equal(losses, host.AbandonedCount);
        Assert.Equal(losses - ActorLocalObjectHost.MaximumAbandoned, host.DroppedAbandonedRecords);
        Assert.Equal(Enumerable.Range(losses - ActorLocalObjectHost.MaximumAbandoned + 1, ActorLocalObjectHost.MaximumAbandoned)
            .Select(value => (long)value), host.Abandoned.Select(item => item.Sequence));

        // Objects still in place keep their records; pruning only reads.
        var calls = backend.Calls.Count;
        Assert.Equal(0, host.PruneAbandoned());
        Assert.All(backend.Calls.Skip(calls), call => Assert.True(call.StartsWith("ComObject") || call == "Table", call));
        Assert.Equal(ActorLocalObjectHost.MaximumAbandoned, host.Abandoned.Count);

        // Once the game (or whoever owns it now) clears an object, its record goes.
        var tracked = host.Abandoned.Take(5).ToArray();
        foreach (var record in tracked)
            backend.DeleteExternally(backend.Objects.Values.Single(item => item.GlobalIndex == record.GlobalIndex));
        calls = backend.Calls.Count;
        Assert.Equal(5, host.PruneAbandoned());
        Assert.All(backend.Calls.Skip(calls), call => Assert.True(call.StartsWith("ComObject") || call == "Table", call));
        Assert.Equal(ActorLocalObjectHost.MaximumAbandoned - 5, host.Abandoned.Count);
        Assert.DoesNotContain(host.Abandoned, item => tracked.Contains(item));

        // A location change prunes too; shutdown ends the host's records.
        foreach (var item in backend.Objects.Values.Where(item => item.Name == "lost").ToArray())
            backend.DeleteExternally(item);
        host.DestroyAll();
        Assert.Empty(host.Abandoned);
        Assert.Equal(losses, host.AbandonedCount);
        host.Shutdown();
        Assert.Empty(host.Abandoned);
    }

    // The full ownership proof runs immediately before
    // every native write and the delete, during construction and cleanup. A
    // change between two steps stops every later step; the record is
    // abandoned and the object now there is never written or deleted.

    [Theory]
    [InlineData("SetAlpha:0", "replaced-same-ids-other-tag")]
    [InlineData("SetAlpha:0", "tag")]
    [InlineData("SetAlpha:0", "kind")]
    [InlineData("SetAlpha:0", "entity")]
    [InlineData("SetAlpha:0", "table")]
    [InlineData("SetAlpha:0", "com")]
    [InlineData("SetAlpha:0", "replaced-other-object")]
    [InlineData("DisableDraw", "replaced-same-ids-other-tag")]
    [InlineData("DisableDraw", "tag")]
    [InlineData("DisableDraw", "kind")]
    [InlineData("DisableDraw", "entity")]
    [InlineData("DisableDraw", "table")]
    [InlineData("DisableDraw", "com")]
    [InlineData("DisableDraw", "replaced-other-object")]
    public void ReleaseStopsTheMomentTheProofFailsBetweenSteps(string afterStep, string mutation)
    {
        var backend = new FakeBackend { ReuseAddresses = true, FixedEntityId = 0xE000_0000 };
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var live = Assert.Single(backend.Objects.Values);
        var comIndex = live.ComIndex;
        LiveObject? now = null;
        var mark = -1;
        backend.AfterWrite = call =>
        {
            if (call != afterStep || now is not null)
                return;
            now = Mutate(backend, live, mutation, setUp: true);
            mark = backend.Calls.Count;
        };

        var released = host.ReleaseOwned(handle);
        backend.AfterWrite = null;

        Assert.NotNull(now);
        Assert.Equal(ApplyPortStatus.Succeeded, released.Status);
        Assert.Equal(ActorLocalObjectHost.AbandonedNotice + ".", released.Error);
        AssertNoWriteAfter(backend, mark);
        Assert.DoesNotContain(backend.Calls, call => call == $"Delete:{comIndex}");
        Assert.Contains(now, backend.Objects.Values);
        if (afterStep == "SetAlpha:0" && ReferenceEquals(now, live))
            Assert.True(live.DrawEnabled); // DisableDraw never reached it
        Assert.Equal(1, host.AbandonedCount);
        Assert.Equal(actor.ObjectId, Assert.Single(host.Abandoned).ObjectId);
        Assert.Contains(host.Unsupported, report => report.ObjectId == actor.ObjectId &&
            report.Reason == ActorLocalObjectHost.ReplacedReason);
        Assert.Equal(0, host.OwnedCount);
        Assert.False(host.IsOwnedAddress(live.Address));
    }

    [Fact]
    public void ALocationChangeCountsAnActorReplacedBetweenDisableDrawAndDeleteAsAbandonedNotFreed()
    {
        var backend = new FakeBackend { ReuseAddresses = true, FixedEntityId = 0xE000_0000 };
        var host = Host(backend);
        var replaced = Actor();
        var replacedHandle = Draw(host, backend, replaced);
        var replacedLive = backend.Objects.Values.Single(item => item.ObjectHandle == replacedHandle.OwnershipId);
        var kept = Actor();
        Draw(host, backend, kept);
        var keptLive = backend.Objects.Values.Single(item => !ReferenceEquals(item, replacedLive));
        LiveObject? now = null;
        var mark = -1;
        backend.AfterWrite = call =>
        {
            // Right after the replaced actor's own DisableDraw (whichever
            // actor is freed first).
            if (call != "DisableDraw" || now is not null || replacedLive.DrawEnabled)
                return;
            now = Mutate(backend, replacedLive, "replaced-same-ids-other-tag", setUp: true);
            mark = backend.Touched.Count;
        };

        Assert.Equal(1, host.DestroyAll());
        backend.AfterWrite = null;

        Assert.NotNull(now);
        Assert.Same(now, Assert.Single(backend.Objects.Values));
        // The kept actor was freed in full; nothing after the replacement
        // reached the address the replacement now occupies.
        Assert.Contains(("Delete", keptLive.Address), backend.Touched);
        Assert.DoesNotContain(backend.Touched.Skip(mark), item => item.Address == now.Address);
        Assert.Equal(1f, now.Alpha);
        Assert.Equal(1, host.AbandonedCount);
        Assert.Equal(replaced.ObjectId, Assert.Single(host.Abandoned).ObjectId);
    }

    [Fact]
    public void AStepThatOnlyThrowsDoesNotStopCleanupWhileTheProofHoldsAndEachStepIsProvedFirst()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var handle = Draw(host, backend, Actor());
        backend.FailAlpha = true;
        var calls = backend.Calls.Count;

        var result = host.ReleaseOwned(handle);

        Assert.Equal(ApplyPortStatus.Succeeded, result.Status);
        Assert.NotNull(result.Error);
        Assert.NotEqual(ActorLocalObjectHost.AbandonedNotice + ".", result.Error);
        Assert.Empty(backend.Objects);
        Assert.Equal(0, host.AbandonedCount);
        AssertEachWriteFollowsAFullProof(backend.Calls.Skip(calls).ToArray(), "SetAlpha:0", "DisableDraw", "Delete:5");
    }

    [Theory]
    [InlineData("SetAlpha:0", "kind")]
    [InlineData("SetAlpha:0", "entity")]
    [InlineData("SetAlpha:0", "table")]
    [InlineData("SetAlpha:0", "com")]
    [InlineData("SetAlpha:0", "replaced-other-object")]
    [InlineData("Setup", "entity")]
    [InlineData("Setup", "table")]
    [InlineData("Setup", "com")]
    [InlineData("Setup", "replaced-other-object")]
    public void SpawnStopsTheMomentTheProofFailsBetweenConstructionSteps(string afterStep, string mutation)
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor();
        LiveObject? now = null;
        var mark = -1;
        backend.AfterWrite = call =>
        {
            if (call != afterStep || now is not null)
                return;
            now = Mutate(backend, backend.Objects.Values.Single(), mutation, setUp: false);
            mark = backend.Calls.Count;
        };

        var spawned = host.SpawnOwned(Request(actor));
        backend.AfterWrite = null;

        Assert.NotNull(now);
        Assert.Equal(ApplyPortStatus.TransientFailure, spawned.Status);
        Assert.False(spawned.Value.IsValid);
        AssertNoWriteAfter(backend, mark);
        Assert.Contains(now, backend.Objects.Values);
        if (afterStep == "SetAlpha:0")
            Assert.DoesNotContain(backend.Calls, call => call.StartsWith("Setup"));
        Assert.Equal(1, host.AbandonedCount);
        Assert.Equal(actor.ObjectId, Assert.Single(host.Abandoned).ObjectId);
        Assert.Equal(0, host.OwnedCount);
    }

    [Theory]
    [InlineData(1, "SetTransform", "replaced-same-ids-other-tag")]
    [InlineData(1, "SetTransform", "tag")]
    [InlineData(1, "SetTransform", "kind")]
    [InlineData(1, "SetTransform", "entity")]
    [InlineData(1, "SetTransform", "table")]
    [InlineData(1, "SetAlpha:0", "replaced-same-ids-other-tag")]
    [InlineData(1, "SetAlpha:0", "tag")]
    [InlineData(1, "SetAlpha:0", "kind")]
    [InlineData(1, "SetAlpha:0", "com")]
    [InlineData(2, "SetTransform", "replaced-same-ids-other-tag")]
    [InlineData(2, "SetTransform", "tag")]
    [InlineData(2, "SetTransform", "entity")]
    [InlineData(2, "SetTransform", "table")]
    public void DrawingStopsTheMomentTheProofFailsBetweenConstructionSteps(int phase, string afterStep, string mutation)
    {
        var backend = new FakeBackend { ReuseAddresses = true, FixedEntityId = 0xE000_0000 };
        var host = Host(backend);
        var actor = Actor();
        var handle = host.SpawnOwned(Request(actor)).Value;
        var live = Assert.Single(backend.Objects.Values);
        live.ReadyToDraw = true;
        if (phase == 2)
        {
            // Phase 1 (placed, alpha 0, drawing enabled) completes first.
            Assert.Equal(ApplyPortStatus.AwaitingPayload, host.ApplyOwned(handle, actor).Status);
            Assert.True(live.DrawEnabled);
            live.ModelLoaded = true;
        }
        LiveObject? now = null;
        var mark = -1;
        backend.AfterWrite = call =>
        {
            if (call != afterStep || now is not null)
                return;
            now = Mutate(backend, live, mutation, setUp: true);
            mark = backend.Calls.Count;
        };

        // A move, so the transform is written before the next construction step.
        var moved = actor with { Revision = new(2), Position = new(4, 5, 6) };
        Assert.Throws<InvalidOperationException>(() => host.ApplyOwned(handle, moved));
        backend.AfterWrite = null;

        Assert.NotNull(now);
        AssertNoWriteAfter(backend, mark);
        Assert.Contains(now, backend.Objects.Values);
        if (phase == 1)
            Assert.DoesNotContain(backend.Calls, call => call == "EnableDraw");
        Assert.DoesNotContain(backend.Calls, call => call == "SetAlpha:1");
        Assert.False(host.IsDrawn(actor.ObjectId));
        Assert.Equal(1, host.AbandonedCount);
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        AssertNoWriteAfter(backend, mark);
        Assert.Equal(1, host.AbandonedCount);
    }

    [Fact]
    public void ConstructionProvesOwnershipImmediatelyBeforeEachWrite()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor();
        var handle = host.SpawnOwned(Request(actor)).Value;
        var live = Assert.Single(backend.Objects.Values);
        live.ReadyToDraw = true;
        Assert.Equal(ApplyPortStatus.AwaitingPayload, host.ApplyOwned(handle, actor with { Position = new(4, 5, 6) }).Status);
        live.ModelLoaded = true;
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(handle, actor with { Position = new(7, 8, 9) }).Status);
        AssertEachWriteFollowsAFullProof(backend.Calls.ToArray(),
            "SetAlpha:0", "Setup:100", "SetTransform", "SetTransform", "SetAlpha:0", "EnableDraw", "SetTransform", "SetAlpha:1");
    }

    /// <summary>Another party changes the actor between two steps; returns the object now there.</summary>
    internal static LiveObject Mutate(FakeBackend backend, LiveObject live, string mutation, bool setUp)
    {
        switch (mutation)
        {
            case "replaced-same-ids-other-tag":
                // Same address, COM slot, global index, GameObjectId and EntityId; another tag.
                backend.DeleteExternally(live);
                return backend.PlaceForeign(live.Address, live.ComIndex, live.GlobalIndex, live.EntityId,
                    ActorOwnershipTag.Create());
            case "replaced-other-object":
                backend.DeleteExternally(live);
                return backend.PlaceForeign(0x7700, live.ComIndex, live.GlobalIndex, 0xE000_0100, "Other");
            case "tag":
                live.Name = ActorOwnershipTag.Create();
                return live;
            case "kind":
                // After setup any kind but BattleNpc fails; before it, a player kind does.
                live.RawKind = setUp ? 3 : 1;
                return live;
            case "entity":
                live.EntityId = 999;
                return live;
            case "table":
                backend.SetTableEntry(live.GlobalIndex, 0x8800);
                return live;
            case "com":
                backend.SetComEntry(live.ComIndex, 0x8800);
                return live;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    internal static bool IsNativeWrite(string call) =>
        call.StartsWith("SetAlpha") || call == "DisableDraw" || call == "EnableDraw" || call.StartsWith("SetTransform") ||
        call.StartsWith("Setup") || call.StartsWith("Delete") || call == "Create" ||
        call.StartsWith("Speed:") || call.StartsWith("SlotSpeed:") || call.StartsWith("Mode:") ||
        call.StartsWith("BaseOverride:") || call.StartsWith("Play:");

    internal static void AssertNoWriteAfter(FakeBackend backend, int mark)
    {
        Assert.True(mark >= 0);
        Assert.DoesNotContain(backend.Calls.Skip(mark), IsNativeWrite);
    }

    /// <summary>
    /// Each listed write (in order) is preceded, since the previous write, by
    /// the full proof's reads: table entry, COM slot, COM index and the facts.
    /// </summary>
    /// <summary>Every native write in <paramref name="calls"/> (creation aside) follows a full proof.</summary>
    internal static void AssertEveryNativeWriteFollowsAFullProof(string[] calls) =>
        AssertEachWriteFollowsAFullProof(calls, calls.Where(IsNativeWrite).Where(call => call != "Create").ToArray());

    internal static void AssertEachWriteFollowsAFullProof(string[] calls, params string[] writes)
    {
        var observed = calls.Where(IsNativeWrite).Where(call => call != "Create").ToArray();
        Assert.Equal(writes, observed);
        var previous = -1;
        foreach (var write in writes)
        {
            var index = Array.FindIndex(calls, previous + 1, call => call == write);
            var since = calls[(previous + 1)..index];
            Assert.Contains("Table", since);
            Assert.Contains(since, call => call.StartsWith("ComObject:"));
            Assert.Contains("ComIndexOf", since);
            if (!write.StartsWith("Delete"))
                Assert.Equal("Read", since[^1]);
            previous = index;
        }
    }

    /// <summary>
    /// The receipt no longer proves ownership: a write is refused, the record
    /// is forgotten and reported, and neither a write nor a delete reaches the
    /// object now at that slot, address or index.
    /// </summary>
    private static void AssertRefusedUntouched(ActorLocalObjectHost host, FakeBackend backend,
        LocalObjectInstanceHandle handle, ActorState actor)
    {
        var calls = backend.Calls.Count;
        Assert.Throws<InvalidOperationException>(() =>
            host.ApplyOwned(handle, actor with { Revision = new(9), Position = new(9, 9, 9) }));
        Assert.Equal(0, host.OwnedCount);
        Assert.False(host.IsDrawn(actor.ObjectId));
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Assert.DoesNotContain(backend.Calls.Skip(calls), call =>
            call.StartsWith("SetTransform") || call.StartsWith("SetAlpha") || call.StartsWith("Delete") ||
            call == "DisableDraw" || call == "EnableDraw" || call.StartsWith("Setup"));
        Assert.Contains(host.Unsupported, report => report.ObjectId == actor.ObjectId &&
            report.Reason == ActorLocalObjectHost.ReplacedReason);
    }

    [Fact]
    public void CleanupStepsAreBestEffortAndDeletionIsAlwaysAttempted()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var handle = Draw(host, backend, Actor());
        backend.FailAlpha = true;
        backend.FailDisableDraw = true;
        var result = host.ReleaseOwned(handle);
        Assert.Equal(ApplyPortStatus.Succeeded, result.Status);
        Assert.NotNull(result.Error);
        Assert.Empty(backend.Objects);
    }

    [Fact]
    public void TheActiveClientLimitComesFromThePolicyAndRefusesTheNext()
    {
        var backend = new FakeBackend();
        var host = Host(backend, activeLimit: null);
        var handles = new List<LocalObjectInstanceHandle>();
        for (var index = 0; index < ActorCapacityPolicy.ActiveMaximumActorsPerScene; index++)
        {
            var result0 = host.SpawnOwned(Request(Actor()));
            Assert.Equal(ApplyPortStatus.AwaitingPayload, result0.Status);
            handles.Add(result0.Value);
        }
        var refused = Actor();
        var result = host.SpawnOwned(Request(refused));
        Assert.Equal(ApplyPortStatus.PermanentFailure, result.Status);
        Assert.Contains(host.Unsupported, report => report.ObjectId == refused.ObjectId &&
            report.Reason == ActorLocalObjectHost.LimitReason);
        var freed = 0;
        host.CapacityFreed += () => freed++;
        host.ReleaseOwned(handles[0]);
        Assert.Equal(1, freed);
    }

    [Fact]
    public void UnsupportedCreaturesAndUnavailableVersionsAreNotDrawnWithTheReason()
    {
        var backend = new FakeBackend();
        var host = new ActorLocalObjectHost(backend, _ => ActorDrawPlanResult.NotDrawn(ActorAppearanceResolver.NotMonsterOrDemihuman));
        var human = Actor();
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(human)).Status);
        Assert.Equal(ActorAppearanceResolver.NotMonsterOrDemihuman, Assert.Single(host.Unsupported).Reason);

        var unavailable = new FakeBackend { Available = false };
        var gated = Host(unavailable);
        Assert.Equal(ApplyPortStatus.PermanentFailure, gated.SpawnOwned(Request(Actor())).Status);
        Assert.Equal(ActorLocalObjectHost.UnavailableReason, Assert.Single(gated.Unsupported).Reason);
        Assert.DoesNotContain(unavailable.Calls, call => call.StartsWith("Create"));
    }

    [Fact]
    public void AnActorWithAControlThisClientCannotApplyIsNeverShown()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var frozen = Actor() with { Controls = new ActorControlState(false, null, 600, 0) };
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(Request(frozen)).Status);
        Assert.Equal(ActorLocalObjectHost.ControlsUnavailableReason, Assert.Single(host.Unsupported).Reason);
        Assert.Empty(backend.Objects);

        // A drawn actor that is later given a control is hidden: the fault makes
        // the receiver release that exact instance.
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        Assert.Throws<InvalidOperationException>(() =>
            host.ApplyOwned(handle, actor with { Revision = new(2), Controls = new ActorControlState(false, new(1, 1, 1), null, 0) }));
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Assert.Empty(backend.Objects);
    }

    [Fact]
    public void TransformChangesWriteOnlyOnChangeAndKeepTheIdentity()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor();
        var handle = Draw(host, backend, actor);
        var writes = backend.Calls.Count(call => call.StartsWith("SetTransform"));
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(handle, actor with { Revision = new(2) }).Status);
        Assert.Equal(writes, backend.Calls.Count(call => call.StartsWith("SetTransform")));
        Assert.Equal(ApplyPortStatus.Succeeded,
            host.ApplyOwned(handle, actor with { Revision = new(3), Yaw = 1f, Scale = 0.5f }).Status);
        var live = Assert.Single(backend.Objects.Values);
        Assert.Equal(1f, live.Yaw);
        Assert.Equal(0.75f, live.Scale);
        Assert.Equal(LocalObjectSpawnIdentity.ForActor(100), LocalObjectSpawnIdentity.FromState(actor with { Scale = 3 }));
        Assert.NotEqual(LocalObjectSpawnIdentity.ForActor(100), LocalObjectSpawnIdentity.ForActor(101));
        Assert.True(LocalObjectSpawnIdentity.ForActor(100).IsValid);
        Assert.False(new LocalObjectSpawnIdentity(RscObjectKind.Actor, "bnpc:100").IsValid);
    }

    [Fact]
    public void GizmoPreviewTurnsOnlyTheYawAndRestoresOnCancel()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        var actor = Actor();
        Draw(host, backend, actor);
        var started = host.BeginPreview(actor);
        Assert.True(started.Started);
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f);
        Assert.True(host.MovePreview(started.Id, new Vector3(4, 5, 6), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f)));
        var live = Assert.Single(backend.Objects.Values);
        Assert.Equal(new Vector3(4, 5, 6), live.Position);
        Assert.Equal(1f, live.Yaw, 4);
        host.CancelPreview(started.Id);
        Assert.Equal(new Vector3(1, 2, 3), live.Position);
        Assert.Equal(actor.Yaw, live.Yaw, 4);
        Assert.Equal(1f, ActorLocalObjectHost.YawOf(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f)), 4);
        Assert.True(float.IsFinite(ActorLocalObjectHost.YawOf(turned)));
    }

    [Fact]
    public void DestroyAllFreesEveryActorForGPoseCutsceneOrLocationChange()
    {
        var backend = new FakeBackend();
        var host = Host(backend);
        Draw(host, backend, Actor());
        Draw(host, backend, Actor());
        Assert.Equal(2, host.DestroyAll());
        Assert.Empty(backend.Objects);
        Assert.Equal(0, host.OwnedCount);
    }

    [Fact]
    public void ThePlacesReadinessOpensOnlyTheLocalObjectDomainAndNeverTheDormantOne()
    {
        var missing = new[]
        {
            new RSyncStageCraft.Core.Dependencies.DependencyHealth("Penumbra", false, false),
            new RSyncStageCraft.Core.Dependencies.DependencyHealth("Glamourer", false, false),
        };
        var known = RSyncStageCraft.Core.Connection.ConnectionDependencies.ForCurrentPlace(missing, sceneKnown: true);
        Assert.True(known.CanConnect);
        Assert.True(known.CanApplyLocalObjects);
        Assert.False(known.PenumbraReady);
        var unknown = RSyncStageCraft.Core.Connection.ConnectionDependencies.ForCurrentPlace(missing, sceneKnown: false);
        Assert.True(unknown.CanConnect);
        Assert.False(unknown.CanApplyLocalObjects);
    }

    [Fact]
    public void SuppressionNamesTheConditionsThatHideActors()
    {
        Assert.False(ActorSuppression.None.Suppressed);
        Assert.Equal("no", ActorSuppression.None.Describe());
        Assert.Equal("yes (GPose, WatchingCutscene78)", new ActorSuppression(true, false, false, true).Describe());
        Assert.True(ActorSuppression.Failed("InvalidOperationException").Suppressed);
        Assert.Contains("could not be read", ActorSuppression.Failed("X").Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void SuppressedVisibilityHidesOnlyActors()
    {
        var suppressed = true;
        var visibility = new ActorSuppressingVisibility(new AlwaysVisible(), () => suppressed);
        var light = new LightState(new(Guid.NewGuid()), new(1), new("creator"), null, LightIdentity.Point, new(0, 0, 0), new(1, 1, 1));
        Assert.False(visibility.IsVisibleHere(Actor()));
        Assert.True(visibility.IsVisibleHere(light));
        suppressed = false;
        Assert.True(visibility.IsVisibleHere(Actor()));
    }

    [Fact]
    public void TheCompositeHostDispatchesActorsToTheActorHostOnly()
    {
        var backend = new FakeBackend();
        var actors = Host(backend);
        var composite = new LocalObjectHost(
            new FurnitureLocalObjectHost(new FurnitureSupportResolver(new FurnitureCatalog([]), new NoAssets()), new NoFurniture()),
            new LightLocalObjectHost(new NoLights()),
            actors);
        var actor = Actor();
        var spawned = composite.SpawnOwned(Request(actor));
        Assert.Equal(ApplyPortStatus.AwaitingPayload, spawned.Status);
        Assert.Equal(1, actors.OwnedCount);
        Assert.Equal(1, composite.OwnedCount);
        Assert.Equal(ApplyPortStatus.Succeeded, composite.ReleaseOwned(spawned.Value).Status);
        Assert.Empty(backend.Objects);

        var withoutActors = new LocalObjectHost(
            new FurnitureLocalObjectHost(new FurnitureSupportResolver(new FurnitureCatalog([]), new NoAssets()), new NoFurniture()),
            new LightLocalObjectHost(new NoLights()));
        Assert.Equal(ApplyPortStatus.PermanentFailure, withoutActors.SpawnOwned(Request(Actor())).Status);
    }

    private static ActorLocalObjectHost Host(FakeBackend backend, int? activeLimit = 8) =>
        new(backend, id => new ActorDrawPlanResult(
            new ActorDrawPlan(id, ActorModelKind.Monster, 7, 24, 1, 1, 1.5f, 0, null), null), activeLimit: activeLimit);

    private static ActorState Actor(float scale = 1) => new(new(Guid.NewGuid()), new(1), new("owner"), null, 100,
        new(1, 2, 3), 0.25f, scale, ActorControlState.Default);

    private static LocalObjectSpawnRequest Request(ActorState actor) => new(
        new LocalObjectApplyToken(Session, actor.ObjectId, new ApplyGeneration(1)),
        LocalObjectSpawnIdentity.FromState(actor),
        1,
        actor);

    private static LocalObjectInstanceHandle Draw(ActorLocalObjectHost host, FakeBackend backend, ActorState actor)
    {
        var handle = host.SpawnOwned(Request(actor)).Value;
        var live = backend.Objects.Values.Single(item => item.ObjectHandle == Guid.Empty && !item.Foreign);
        live.ObjectHandle = handle.OwnershipId;
        live.ReadyToDraw = true;
        live.ModelLoaded = true;
        for (var poll = 0; poll < 3; poll++)
        {
            if (host.ApplyOwned(handle, actor).Status == ApplyPortStatus.Succeeded)
                return handle;
        }
        throw new InvalidOperationException("The actor did not draw.");
    }

    private sealed class AlwaysVisible : ILocalObjectVisibility
    {
        public bool IsVisibleHere(AuthoritativeObjectState state) => true;
    }

    private sealed class NoAssets : IFurnitureAssetReader
    {
        public bool FileExists(string gamePath) => false;
        public IReadOnlyList<FurnitureLayoutInstance>? ReadLayout(string gamePath) => null;
        public byte ReadDefaultStain(string gamePath) => 0;
    }

    private sealed class NoFurniture : IFurnitureSceneBackend
    {
        public nint Create(string modelPath) => 0;
        public bool IsLoaded(nint instance) => false;
        public void SetTransform(nint instance, Vector3 position, Quaternion rotation, bool loaded) { }
        public void Show(nint instance) { }
        public bool TrySetStain(nint instance, FurnitureStainColor color) => false;
        public void Destroy(nint instance) { }
    }

    private sealed class NoLights : ILightSceneBackend
    {
        public LightCreateResult Create() => new(LightCreateStatus.Failed, 0);
        public void Apply(nint instance, NativeLightSettings settings) { }
        public void Show(nint instance) { }
        public void Hide(nint instance) { }
        public void CleanupRender(nint instance) { }
        public void Free(nint instance) { }
    }

    /// <summary>A ClientObjectManager and object table in memory.</summary>
    internal sealed class FakeBackend : IActorSceneBackend
    {
        private const nint PlayerAddress = 0x1000;
        private nint nextAddress = 0x2000;
        private uint nextEntity = 0xE000_0001;
        private readonly Dictionary<int, nint> com = new();
        private readonly Dictionary<int, nint> table = new() { [0] = PlayerAddress };
        private readonly List<(Guid Handle, LiveObject Object)> spawned = new();

        public bool Available { get; set; } = true;
        public int NextGlobalIndex { get; set; } = 205;
        public bool TableMismatch { get; set; }
        public bool CreatedIsLocalPlayer { get; set; }
        public bool CreatedIsBattleNpc { get; set; } = true;
        public bool CreatedKindUnset { get; set; }
        /// <summary>A raw ObjectKind for new objects (overrides the two flags above).</summary>
        public int? CreatedRawKind { get; set; }
        /// <summary>The raw kind Setup leaves the object with (default: None becomes BattleNpc).</summary>
        public int? SetupResultKind { get; set; }
        /// <summary>The COM index the automatic allocator uses (any valid one), instead of the first free slot.</summary>
        public int? AllocatorSlot { get; set; }
        /// <summary>A raw allocator return without creating anything (−1, or out of range).</summary>
        public int? ReturnIndex { get; set; }
        /// <summary>The allocator returns an index, but GetObjectByIndex finds no object there.</summary>
        public bool ObjectMissing { get; set; }
        /// <summary>GetIndexByObject gives a different COM index than the allocator returned.</summary>
        public bool RoundTripMismatch { get; set; }
        /// <summary>The raw COM array entry disagrees with GetObjectByIndex.</summary>
        public bool RawEntryMismatch { get; set; }
        public bool FailAlpha { get; set; }
        public bool FailDisableDraw { get; set; }
        public bool SetupLeavesKindUnset { get; set; }
        public bool SetupWritesNoTag { get; set; }
        public bool FailSetup { get; set; }
        /// <summary>Runs inside Setup, after its writes (another party acting meanwhile).</summary>
        public Action<LiveObject>? DuringSetup { get; set; }
        /// <summary>Freed addresses are handed out again (allocator reuse).</summary>
        public bool ReuseAddresses { get; set; }
        /// <summary>Every new object gets this EntityId (COM objects can share one).</summary>
        public uint? FixedEntityId { get; set; }
        private readonly Stack<nint> freed = new();
        public Action<string>? BeforeEachCall { get; set; }
        /// <summary>Runs after a native write completed (another party acting between two steps).</summary>
        public Action<string>? AfterWrite { get; set; }
        /// <summary>The address each native write or delete was aimed at, in order.</summary>
        public List<(string Call, nint Address)> Touched { get; } = new();
        public Dictionary<nint, LiveObject> Objects { get; } = new();
        public List<string> Calls { get; } = new();

        public bool IsAvailable => Available;

        public nint LocalPlayer => PlayerAddress;

        public nint ComEntry(int comIndex)
        {
            Record($"ComEntry:{comIndex}");
            if (RawEntryMismatch && com.ContainsKey(comIndex))
                return 0x5555;
            return com.TryGetValue(comIndex, out var address) ? address : 0;
        }

        public nint[] ComEntriesSnapshot()
        {
            Record("ComSnapshot");
            var entries = new nint[ActorSlotRange.ComSlotCount];
            foreach (var (index, address) in com)
            {
                if (index is >= 0 and < ActorSlotRange.ComSlotCount)
                    entries[index] = address;
            }
            return entries;
        }

        /// <summary>The automatic allocator: the first free COM slot from 5, or <see cref="AllocatorSlot"/>.</summary>
        public int CreateBattleCharacter()
        {
            Record("Create");
            if (ReturnIndex is { } forced)
                return forced;
            var slot = AllocatorSlot ?? Enumerable.Range(5, 244).First(index => !com.ContainsKey(index));
            if (ObjectMissing)
                return slot;
            var address = CreatedIsLocalPlayer ? PlayerAddress
                : ReuseAddresses && freed.Count > 0 ? freed.Pop()
                : nextAddress += 0x100;
            // The global index is read from the object; tests choose it.
            var globalIndex = NextGlobalIndex + (slot - 5);
            var entityId = FixedEntityId ?? nextEntity++;
            var live = new LiveObject(address, globalIndex, entityId)
            {
                ComIndex = RoundTripMismatch ? slot + 1 : slot,
                Foreign = CreatedIsLocalPlayer,
                RawKind = CreatedRawKind ?? (CreatedKindUnset ? 0 : CreatedIsBattleNpc ? 2 : 1),
            };
            Objects[address] = live;
            com[slot] = address;
            // A mismatch keeps whatever the table already names there (or another address).
            table[globalIndex] = TableMismatch
                ? table.TryGetValue(globalIndex, out var existing) ? existing : address + 8
                : address;
            return slot;
        }

        public nint ComObjectByIndex(int comIndex)
        {
            Record($"ComObject:{comIndex}");
            return com.TryGetValue(comIndex, out var address) ? address : 0;
        }

        public int ComIndexOf(nint address)
        {
            Record("ComIndexOf");
            return Objects.TryGetValue(address, out var live) ? live.ComIndex : -1;
        }

        /// <summary>Objects deleted while still drawn: their draw object would be left behind in game.</summary>
        public int OrphanedDrawObjects { get; private set; }

        public void DeleteComObject(int comIndex)
        {
            Record($"Delete:{comIndex}");
            if (!com.Remove(comIndex, out var address))
                return;
            Touched.Add(("Delete", address));
            if (Objects.TryGetValue(address, out var drawn) && drawn.DrawEnabled)
                OrphanedDrawObjects++;
            if (Objects.Remove(address, out var live))
                table.Remove(live.GlobalIndex);
            freed.Push(address);
        }

        /// <summary>Another party deletes the object (RSC is not told).</summary>
        public void DeleteExternally(LiveObject live)
        {
            com.Remove(live.ComIndex);
            Objects.Remove(live.Address);
            table.Remove(live.GlobalIndex);
            freed.Push(live.Address);
        }

        /// <summary>Another party puts its own object in a COM slot and table index.</summary>
        public LiveObject PlaceForeign(nint address, int comIndex, int globalIndex, uint entityId, string? name)
        {
            var live = new LiveObject(address, globalIndex, entityId) { ComIndex = comIndex, Foreign = true, Name = name, IsBattleNpc = true };
            Objects[address] = live;
            com[comIndex] = address;
            table[globalIndex] = address;
            return live;
        }

        public void SetTableEntry(int globalIndex, nint address) => table[globalIndex] = address;

        public void SetComEntry(int comIndex, nint address) => com[comIndex] = address;

        public nint ObjectTableEntry(int globalIndex)
        {
            Record("Table");
            return table.TryGetValue(globalIndex, out var address) ? address : 0;
        }

        public ActorObjectFacts ReadFacts(nint address)
        {
            Record("Read");
            return Objects.TryGetValue(address, out var live)
                ? new(live.GlobalIndex, live.GameObjectId, live.EntityId, live.IsBattleNpc, live.KindUnset, live.Name, live.RawKind)
                : new(-1, 0, 0, false);
        }

        public void Setup(nint address, ActorDrawPlan plan, string tag)
        {
            Record($"Setup:{plan.BNpcBaseId}");
            Touched.Add(("Setup", address));
            var live = Objects[address];
            live.SetupBase = plan.BNpcBaseId;
            if (!SetupWritesNoTag)
                live.Name = tag;
            if (SetupResultKind is { } result)
                live.RawKind = result;
            else if ((live.KindUnset || live.RawKind == 3) && !SetupLeavesKindUnset)
                live.RawKind = 2;
            DuringSetup?.Invoke(live);
            if (FailSetup)
                throw new InvalidOperationException("model setup");
            AfterWrite?.Invoke("Setup");
        }

        public void SetTransform(nint address, Vector3 position, float yaw, float scale)
        {
            Record("SetTransform");
            Touched.Add(("SetTransform", address));
            var live = Objects[address];
            live.Position = position;
            live.Yaw = yaw;
            live.Scale = scale;
            AfterWrite?.Invoke("SetTransform");
        }

        public void SetAlpha(nint address, float alpha)
        {
            Record($"SetAlpha:{alpha}");
            Touched.Add(("SetAlpha", address));
            if (FailAlpha)
                throw new InvalidOperationException("alpha");
            Objects[address].Alpha = alpha;
            AfterWrite?.Invoke($"SetAlpha:{alpha}");
        }

        public bool IsReadyToDraw(nint address) => Objects[address].ReadyToDraw;

        public void EnableDraw(nint address)
        {
            Record("EnableDraw");
            Touched.Add(("EnableDraw", address));
            Objects[address].DrawEnabled = true;
            AfterWrite?.Invoke("EnableDraw");
        }

        public void DisableDraw(nint address)
        {
            Record("DisableDraw");
            Touched.Add(("DisableDraw", address));
            if (FailDisableDraw)
                throw new InvalidOperationException("disable");
            Objects[address].DrawEnabled = false;
            AfterWrite?.Invoke("DisableDraw");
        }

        public bool HasDrawnModel(nint address, uint modelCharaId) =>
            Objects[address].DrawEnabled && Objects[address].ModelLoaded && modelCharaId == 7;

        public void MoveToComSlot(LiveObject live, int slot)
        {
            com.Remove(live.ComIndex);
            live.ComIndex = slot;
            com[slot] = live.Address;
        }

        private void Record(string call)
        {
            BeforeEachCall?.Invoke(call);
            Calls.Add(call);
        }

        /// <summary>A read by another fake (the control backend) on this scene.</summary>
        public void RecordRead(string call) => Record(call);

        /// <summary>A native write by another fake (the control backend) on this scene.</summary>
        public void RecordWrite(string call, nint address)
        {
            Record(call);
            Touched.Add((call, address));
        }

        /// <summary>Signals that a write by another fake completed.</summary>
        public void Wrote(string call) => AfterWrite?.Invoke(call);
    }

    internal sealed class LiveObject(nint address, int globalIndex, uint entityId)
    {
        public nint Address { get; } = address;
        public int GlobalIndex { get; } = globalIndex;
        public uint EntityId { get; set; } = entityId;
        public ulong GameObjectId => ((ulong)2 << 32) | (uint)GlobalIndex;
        public int ComIndex { get; set; }
        public bool Foreign { get; set; }
        public Guid ObjectHandle { get; set; }
        public uint SetupBase { get; set; }
        public string? Name { get; set; }
        /// <summary>The raw ObjectKind (2 BattleNpc, 0 None, 1 Pc, ...).</summary>
        public int RawKind { get; set; } = 2;
        public bool IsBattleNpc
        {
            get => RawKind == 2;
            set => RawKind = value ? 2 : RawKind == 2 ? 1 : RawKind;
        }
        public bool KindUnset => RawKind == 0;
        public float Alpha { get; set; } = 1f;
        public bool ReadyToDraw { get; set; }
        public bool DrawEnabled { get; set; }
        public bool ModelLoaded { get; set; }
        public Vector3 Position { get; set; }
        public float Yaw { get; set; }
        public float Scale { get; set; }
        /// <summary>The base-animation override (0 = none).</summary>
        public ushort BaseOverride { get; set; }
        /// <summary>CharacterModes: 1 Normal, 8 AnimLock.</summary>
        public byte Mode { get; set; } = 1;
        public HashSet<ushort> ActiveTimelines { get; } = new();
        public List<ushort> Played { get; } = new();
        /// <summary>The body's animation local time, advanced on each sample while running.</summary>
        public float Clock { get; set; }
        /// <summary>Each active row's own scheduler timestamp.</summary>
        public Dictionary<ushort, float> Timestamps { get; } = new();
        /// <summary>The body control each play of a row started (a new identity per play).</summary>
        public Dictionary<ushort, ulong> RowControls { get; } = new();
    }
}
