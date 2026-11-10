#if RSC_DEVELOPMENT
using System.Reflection;
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The stage-3 Debug human puppet host against a synthetic scene: M-1 in one
/// call, both maps, the O1 scan, P5 checks 1–7, P6(a) and P16, the non-forced
/// temporary assignment, P13 with D1 and D2, the unload order and the stop
/// latch. Synthetic identities and fakes only: no game, no IPC.
/// </summary>
public sealed class HumanPuppetHostTests
{
    private const string FixedName = "Rsc Qabcdefghijklmn";
    private static readonly HumanPuppetWorld World = new(9000, F11Verified: true);

    private readonly FakeHumanScene scene = new();

    private HumanPuppetHost Host(int limit = 1, HumanPuppetKind kind = HumanPuppetKind.BattleNpc, HumanPuppetWorld? world = null, int others = 0) =>
        new(scene, scene, scene, scene, world ?? World, kind, () => others, () => 0, () => null, limit);

    private HumanPuppetHost Ready(int limit = 1, HumanPuppetKind kind = HumanPuppetKind.BattleNpc, int others = 0)
    {
        var host = Host(limit, kind, others: others);
        Assert.True(host.CheckCustomizePlus().Done);
        host.OnFrameworkUpdate(1);
        return host;
    }

    private (HumanPuppetHost Host, HumanPuppetHandle Handle) Spawned(HumanPuppetKind kind = HumanPuppetKind.BattleNpc)
    {
        var host = Ready(kind: kind);
        var result = host.Spawn();
        Assert.True(result.Spawned, result.Stop?.Detail);
        return (host, result.Handle!.Value);
    }

    // As live, the human model is read only once a draw object exists, so
    // isolation and the assignment run after the draw request.
    private (HumanPuppetHost Host, HumanPuppetHandle Handle) Drawn()
    {
        var (host, handle) = Spawned();
        Assert.True(host.RequestDraw(handle).Done);
        return (host, handle);
    }

    private static int IndexOfCall(List<string> calls, string call)
    {
        var index = calls.IndexOf(call);
        Assert.True(index >= 0, $"missing call: {call}");
        return index;
    }

    // ---- Identity and keys (P2, P3, P7) ----

    [Fact]
    public void NamesFollowP2AndPuppetsInOneRunHaveDistinctSurnameInitials()
    {
        var used = new HashSet<char>();
        for (var i = 0; i < 26; i++)
        {
            var name = HumanPuppetName.Create(used);
            Assert.True(HumanPuppetName.IsWellFormed(name), name);
            Assert.Equal(HumanPuppetName.Length, name.Length);
            Assert.StartsWith("Rsc ", name, StringComparison.Ordinal);
            Assert.True(used.Add(HumanPuppetName.SurnameInitial(name)));
        }
        Assert.Throws<InvalidOperationException>(() => HumanPuppetName.Create(used));
        Assert.InRange(HumanPuppetName.Length + 1, 1, HumanPuppetName.GameNameBufferBytes);
    }

    [Theory]
    [InlineData("Rsc abcdefghijklmno")]
    [InlineData("Rsc ABcdefghijklmn")]
    [InlineData("Rsc Abcdefghijklm")]
    [InlineData("Abc Qabcdefghijklmn")]
    [InlineData("RSC#00000000000000000000000000000000")]
    [InlineData(null)]
    public void NamesOutsideP2AreNotWellFormed(string? name) => Assert.False(HumanPuppetName.IsWellFormed(name));

    [Fact]
    public void KeysAreNeverZeroOr1337()
    {
        var draws = new Queue<uint>([0u, 1337u, 0u, 42u]);
        Assert.Equal(42u, HumanPuppetKey.Create(() => draws.Dequeue()));
        Assert.False(HumanPuppetKey.IsPermitted(0));
        Assert.False(HumanPuppetKey.IsPermitted(1337));
        for (var i = 0; i < 1000; i++)
            Assert.True(HumanPuppetKey.IsPermitted(HumanPuppetKey.Create()));
    }

    [Fact]
    public void ASpawnRecordsAPermittedKey()
    {
        var (host, handle) = Spawned();
        Assert.True(HumanPuppetKey.IsPermitted(host.KeyOf(handle)!.Value));
    }

    [Theory]
    [InlineData((ushort)9000, false)]
    [InlineData((ushort)0, true)]
    [InlineData(HumanPuppetWorld.AnyWorld, true)]
    public void AWorldWithoutF11OrOutsideTheRowsIsRefusedWithoutFallback(ushort worldId, bool verified)
    {
        var host = Host(world: new HumanPuppetWorld(worldId, verified));
        Assert.True(host.CheckCustomizePlus().Done);
        var result = host.Spawn();
        Assert.Equal(HumanPuppetRefusal.WorldUnverified, result.Refusal);
        Assert.DoesNotContain("create", scene.Calls);
        Assert.Null(host.Stop);
    }

    [Fact]
    public void AVerifiedWorldEqualToThePlayersOwnIsASilentW1AndRefused()
    {
        var host = Host(world: new HumanPuppetWorld(FakeHumanScene.PlayerWorld, F11Verified: true));
        Assert.True(host.CheckCustomizePlus().Done);
        Assert.Equal(HumanPuppetRefusal.WorldUnverified, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    // ---- CP-3 and the providers (H-11) ----

    [Fact]
    public void SpawningNeedsAPassedCustomizePlusCheck()
    {
        var host = Host();
        Assert.Equal(HumanPuppetRefusal.CustomizePlusNotChecked, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("ipc")]
    [InlineData("loaded")]
    public void AFailedCustomizePlusCheckStopsAndKeepsCustomizePlusOff(string failure)
    {
        switch (failure)
        {
            case "version":
                scene.CustomizePlusApiVersion = (6, 5);
                break;
            case "ipc":
                scene.CustomizePlusParentIpcPresent = false;
                break;
            default:
                scene.CustomizePlusLoaded = false;
                break;
        }
        var host = Host();
        var outcome = host.CheckCustomizePlus();
        Assert.False(outcome.Done);
        Assert.Equal(HumanPuppetStopRule.CustomizePlus, outcome.Stop!.Rule);
        Assert.Equal(HumanPuppetRefusal.Stopped, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    [Fact]
    public void AMissingProviderOrBackendRefusesBeforeAnySideEffect()
    {
        var host = Ready();
        scene.PenumbraAvailable = false;
        Assert.Equal(HumanPuppetRefusal.Unavailable, host.Spawn().Refusal);
        scene.PenumbraAvailable = true;
        scene.Available = false;
        Assert.Equal(HumanPuppetRefusal.Unavailable, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    // ---- Capacity ----

    [Fact]
    public void ThePuppetLimitIsWithinTheEightActorTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Host(limit: ActorCapacityPolicy.ActiveMaximumActorsPerScene + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Host(limit: 0));
        var host = Ready(limit: 2);
        Assert.True(host.Spawn().Spawned);
        Assert.True(host.Spawn().Spawned);
        Assert.Equal(HumanPuppetRefusal.AtCapacity, host.Spawn().Refusal);
    }

    [Fact]
    public void OtherRscActorsCountTowardTheEight()
    {
        var host = Ready(limit: 2, others: ActorCapacityPolicy.ActiveMaximumActorsPerScene - 1);
        Assert.True(host.Spawn().Spawned);
        Assert.Equal(HumanPuppetRefusal.AtCapacity, host.Spawn().Refusal);
    }

    // ---- M-1 ----

    [Fact]
    public void SpawnRunsM1InOneCallAtAlphaZeroWithoutDrawOrByIndexCalls()
    {
        var host = Ready();
        var result = host.Spawn();
        Assert.True(result.Spawned);
        var index = host.Snapshot()[0].GlobalIndex;
        var calls = scene.Calls;
        var order = new[]
        {
            IndexOfCall(calls, "snapshot"),
            IndexOfCall(calls, "create"),
            IndexOfCall(calls, $"alpha {index} 0"),
            IndexOfCall(calls, $"copy 0->{index}"),
            IndexOfCall(calls, $"copy {index}->{index}"),
            IndexOfCall(calls, $"penumbra set {index} -1"),
            IndexOfCall(calls, $"customize set {index} -1"),
            IndexOfCall(calls, "write identity kind=False clear=False"),
        };
        Assert.Equal(order.Order(), order);
        // No draw, no Penumbra collection call, no Glamourer call inside M-1.
        Assert.DoesNotContain(calls, call => call.StartsWith("draw on", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.StartsWith("collection", StringComparison.Ordinal) || call.Contains("assign", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.StartsWith("glamourer", StringComparison.Ordinal));
        Assert.Equal(0f, scene.At(index).Alpha);
        // The O1 scan ran once, between the detach and the identity write.
        Assert.Equal(1, scene.ScanCount);
        // Both maps were read back after the detach and again after the write.
        Assert.Equal(new[] { "M-1 detach", "M-1 re-verify" }, result.Readings.Select(reading => reading.Phase));
        Assert.All(result.Readings, reading => Assert.True(reading.Resolved));
        Assert.Equal(-1, scene.PenumbraEntry(index));
        Assert.Equal(-1, scene.CustomizePlusEntry(index));
        // F0's record: one entry per copy.
        Assert.Equal(new[] { "from the player", "self-copy" }, result.Copies.Select(copy => copy.Copy));
    }

    [Fact]
    public void TheIdentityWriteCarriesOnlyTheSyntheticNameAndTheVerifiedWorld()
    {
        var (host, handle) = Spawned();
        var puppet = scene.At(host.Snapshot()[0].GlobalIndex);
        Assert.Equal(host.NameOf(handle), puppet.Name);
        Assert.True(HumanPuppetName.IsWellFormed(puppet.Name));
        Assert.Equal(World.WorldId, puppet.World);
        Assert.Equal(2, puppet.Kind);
        // The player was only read.
        Assert.Equal(FakeHumanScene.PlayerName, scene.Player.Name);
        Assert.Equal(FakeHumanScene.PlayerWorld, scene.Player.World);
        Assert.Equal(1f, scene.Player.Alpha);
    }

    [Fact]
    public void ANameIdOrOwnerLeftByTheCopiesIsClearedByTheIdentityWrite()
    {
        scene.CopiedNameId = 77;
        scene.CopiedOwner = 0x1234;
        Spawned();
        Assert.Contains("write identity kind=False clear=True", scene.Calls);
    }

    [Fact]
    public void ThePlayerKindIsWrittenOnlyWhenRecordedForF1()
    {
        var (host, _) = Spawned(HumanPuppetKind.Player);
        Assert.Contains("write identity kind=True clear=False", scene.Calls);
        Assert.Equal(1, scene.At(host.Snapshot()[0].GlobalIndex).Kind);
        host.OnFrameworkUpdate(2);
        Assert.Null(host.Stop);
    }

    [Fact]
    public void AFailedDetachStopsBeforeTheScanAndTheIdentityWrite()
    {
        scene.FailPenumbraSet = true;
        var host = Ready();
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.S4ParentMapOrIsolation, result.Stop!.Rule);
        Assert.Equal(0, scene.ScanCount);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("write identity", StringComparison.Ordinal));
        Assert.False(scene.Exists(200));
        Assert.Equal(HumanPuppetRefusal.Stopped, host.Spawn().Refusal);
    }

    [Fact]
    public void ACustomizePlusReadBackThatNamesThePlayerStops()
    {
        scene.CustomizePlusReadOverride = _ => 0;
        var host = Ready();
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.S4ParentMapOrIsolation, result.Stop!.Rule);
        Assert.Contains(result.Readings, reading => reading.CustomizePlus == 0 && !reading.Resolved);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("write identity", StringComparison.Ordinal));
        Assert.False(scene.Exists(200));
    }

    [Theory]
    [InlineData(9000, false)]
    [InlineData((int)HumanPuppetWorld.AnyWorld, false)]
    [InlineData(9000, true)]
    public void AnO1HitStopsWithoutTheIdentityWrite(int carrierWorld, bool upperCase)
    {
        var host = Ready();
        host.NameSource = _ => FixedName;
        scene.Injected[57] = new(57, upperCase ? FixedName.ToUpperInvariant() : FixedName, (ushort)carrierWorld);
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.Collision, result.Stop!.Rule);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("write identity", StringComparison.Ordinal));
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void TheSameNameOnAnotherWorldIsNotACollision()
    {
        var host = Ready();
        host.NameSource = _ => FixedName;
        scene.Injected[57] = new(57, FixedName, 9001);
        Assert.True(host.Spawn().Spawned);
    }

    [Fact]
    public void ACopyThatChangesTheEntityIdLeavesTheObjectUntouchedAsResidue()
    {
        scene.CopyChangesEntityId = true;
        var host = Ready();
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.S6Residue, result.Stop!.Rule);
        Assert.Contains(host.Residue, item => item.What.Contains("no longer provably", StringComparison.Ordinal));
        Assert.DoesNotContain("delete 200", scene.Calls);
        Assert.True(scene.Exists(200));
    }

    [Fact]
    public void AnObjectOutsideTwoHundredToFourThirtyNineIsDeletedAndStops()
    {
        scene.NextGlobalIndex = 440;
        var host = Ready();
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.Ownership, result.Stop!.Rule);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("copy", StringComparison.Ordinal));
        // No field write outside 200–439: deleted only.
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("alpha 440", StringComparison.Ordinal) || call == "draw off 440");
        Assert.Contains("delete 440", scene.Calls);
    }

    [Fact]
    public void AnAllocationWithoutAUsableIndexIsALostReceipt()
    {
        scene.CreateReturnsNothingButAllocates = true;
        var host = Ready();
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.S6Residue, result.Stop!.Rule);
        Assert.Contains(host.Residue, item => item.Reason == "body receipt lost");
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("delete", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnershipIsProvedAgainImmediatelyBeforeTheIdentityWrite()
    {
        var host = Ready();
        scene.OnScan = () => scene.At(200).EntityId++;
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.S6Residue, result.Stop!.Rule);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("write identity", StringComparison.Ordinal));
        Assert.Contains(host.Residue, item => item.Reason.StartsWith("before the identity write", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdentityWithoutItsTerminatorFailsTheReVerification()
    {
        scene.WriteLeavesNoTerminator = true;
        var host = Ready();
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.S5Identity, result.Stop!.Rule);
        Assert.False(scene.Exists(200));
    }

    // ---- Draw and F2 ----

    [Fact]
    public void ADrawStaysHiddenAndIsObservedOnALaterUpdate()
    {
        var (host, handle) = Spawned();
        Assert.True(host.RequestDraw(handle).Done);
        Assert.Equal(0f, scene.At(200).Alpha);
        Assert.True(IndexOfCall(scene.Calls, "alpha 200 0") < scene.Calls.LastIndexOf("draw on 200"));
        host.OnFrameworkUpdate(5);
        var snapshot = Assert.Single(host.Snapshot());
        Assert.Equal(HumanPuppetPhase.DrawRequested, snapshot.Phase);
        Assert.Equal(1, snapshot.DrawRequestedUpdate);
        Assert.Equal(5, snapshot.DrawObservedUpdate);
        Assert.Equal(HumanPuppetRefusal.NotHidden, host.RequestDraw(handle).Refusal);
    }

    [Fact]
    public void EveryUpdateReadsBothMapsAndAReadingOfTheParentStops()
    {
        var (host, _) = Spawned();
        for (var tick = 2; tick < 10; tick++)
            host.OnFrameworkUpdate(tick);
        Assert.Equal(8, host.Readings.Count(reading => reading.Phase == "F2 tick"));
        scene.SetEntries(200, 0);
        host.OnFrameworkUpdate(10);
        Assert.Equal(HumanPuppetStopRule.S4ParentMapOrIsolation, host.Stop!.Rule);
        Assert.Equal(10, host.Stop.Update);
        // Hidden, re-detached, and deleted (no change was made, so D2 is not engaged).
        Assert.Contains("penumbra set 200 -1", scene.Calls.Skip(scene.Calls.IndexOf("write identity kind=False clear=False")));
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AnOverwrittenHomeWorldStopsAsAnIdentityFailure()
    {
        var (host, _) = Spawned();
        scene.At(200).World = FakeHumanScene.PlayerWorld;
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.S5Identity, host.Stop!.Rule);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void ACarrierSeenOnAnUpdateIsACollisionStop()
    {
        var host = Ready();
        host.NameSource = _ => FixedName;
        Assert.True(host.Spawn().Spawned);
        scene.Injected[57] = new(57, FixedName, World.WorldId);
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.Collision, host.Stop!.Rule);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AProviderUnloadStopsTheRun()
    {
        var (host, _) = Spawned();
        scene.CustomizePlusLoaded = false;
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.Provider, host.Stop!.Rule);
    }

    [Fact]
    public void ADeletedSlotIsReadOnTheNextUpdateAndMustBeDetachedInBothMaps()
    {
        var (host, handle) = Spawned();
        host.Remove(handle);
        host.OnFrameworkUpdate(2);
        Assert.Contains(host.Readings, reading => reading.Phase == "F2-R item 1" && reading.Penumbra == -1 && reading.CustomizePlus == -1);
        Assert.Null(host.Stop);
    }

    [Fact]
    public void ADeletedSlotLeftNamingAParentStops()
    {
        scene.DestructorKeepsMaps = true;
        var (host, handle) = Spawned();
        host.Remove(handle);
        scene.SetEntries(200, 0);
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.S4ParentMapOrIsolation, host.Stop!.Rule);
    }

    // ---- P6(a) and P16 ----

    [Fact]
    public void IsolationHoldsWhenNoIndividualEntryAndTheExpectedCollectionApply()
    {
        var (host, handle) = Drawn();
        Assert.True(host.VerifyIsolation(handle).Done);
        // The clan-and-gender and gender types were asked first; Default answered.
        var asked = scene.Calls.Where(call => call.StartsWith("collection ", StringComparison.Ordinal) && !call.StartsWith("collection for", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "collection ClanGender", "collection Gender", "collection Default" }, asked);
    }

    [Fact]
    public void IsolationIsNotReadAgainstTheExpectedCollectionOnceAssigned()
    {
        var (host, handle) = Drawn();
        Assert.True(host.AssignTemporaryCollection(handle).Done);
        Assert.Equal(HumanPuppetRefusal.AlreadyAssigned, host.VerifyIsolation(handle).Refusal);
        Assert.Null(host.Stop);
    }

    [Fact]
    public void AnExactIndividualEntryIsACollision()
    {
        var (host, handle) = Drawn();
        scene.IndividualSet = true;
        Assert.Equal(HumanPuppetStopRule.Collision, host.VerifyIsolation(handle).Stop!.Rule);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AnUnexpectedEffectiveCollectionFailsIsolation()
    {
        var (host, handle) = Drawn();
        scene.EffectiveOverride = Guid.NewGuid();
        Assert.Equal(HumanPuppetStopRule.S4ParentMapOrIsolation, host.VerifyIsolation(handle).Stop!.Rule);
    }

    [Fact]
    public void AnUnfilledCustomizeArrayDefersTheChecks()
    {
        var (host, handle) = Drawn();
        scene.At(200).Customize![0] = 0;
        Assert.Equal(HumanPuppetRefusal.CustomizeNotFilled, host.VerifyIsolation(handle).Refusal);
        Assert.Equal(HumanPuppetRefusal.CustomizeNotFilled, host.AssignTemporaryCollection(handle).Refusal);
        Assert.DoesNotContain("create collection", scene.Calls);
        Assert.Null(host.Stop);
    }

    [Theory]
    [InlineData((byte)3, HumanPuppetKind.BattleNpc, "NonPlayerElderly,Default")]
    [InlineData((byte)4, HumanPuppetKind.BattleNpc, "NonPlayerChild,Default")]
    [InlineData((byte)1, HumanPuppetKind.BattleNpc, "ClanGender:5:2:True,Gender:0:2:True,Default")]
    [InlineData((byte)1, HumanPuppetKind.Player, "ClanGender:5:2:False,Gender:0:2:False,Default")]
    public void TheExpectedCollectionOrderFollowsTheCustomizeBytes(byte bodyType, HumanPuppetKind kind, string expected)
    {
        var customize = new byte[26];
        customize[0] = 1;
        customize[1] = 1;
        customize[2] = bodyType;
        customize[4] = 5;
        var order = HumanPuppetHost.ExpectedCollectionOrder(customize, kind).Select(role => role.Kind switch
        {
            HumanCollectionRoleKind.ClanGender or HumanCollectionRoleKind.Gender => $"{role.Kind}:{role.Clan}:{role.Gender}:{role.IsNpc}",
            _ => role.Kind.ToString(),
        });
        Assert.Equal(expected, string.Join(",", order));
    }

    // ---- H-5 and H-6 ----

    [Fact]
    public void TheTemporaryAssignmentIsNonForcedAndVerified()
    {
        var (host, handle) = Drawn();
        Assert.True(host.AssignTemporaryCollection(handle).Done);
        var created = IndexOfCall(scene.Calls, "create collection");
        Assert.True(IndexOfCall(scene.Calls, "collection for 200") < created);
        Assert.True(created < IndexOfCall(scene.Calls, "assign 200"));
        Assert.True(Assert.Single(host.Snapshot()).Assigned);
        Assert.Equal(HumanPuppetRefusal.AlreadyAssigned, host.AssignTemporaryCollection(handle).Refusal);
        // The port has no forced route and no persistent assignment.
        var assign = typeof(IHumanPenumbraPort).GetMethod(nameof(IHumanPenumbraPort.AssignTemporaryCollection))!;
        Assert.DoesNotContain(assign.GetParameters(), parameter => parameter.ParameterType == typeof(bool));
        Assert.Null(typeof(IHumanPenumbraPort).GetMethod("SetCollectionForObject"));
    }

    [Fact]
    public void ALostCollectionIdIsAStopWithResidue()
    {
        scene.LoseCollectionId = true;
        var (host, handle) = Drawn();
        var outcome = host.AssignTemporaryCollection(handle);
        Assert.Equal(HumanPuppetStopRule.S6Residue, outcome.Stop!.Rule);
        Assert.Contains(host.Residue, item => item.Reason == "collection receipt lost");
        Assert.DoesNotContain("assign 200", scene.Calls);
        // The receipt carried its change time, so D2 holds the deletion (change counted as update 2).
        Assert.Equal(HumanPuppetPhase.AwaitingDeletion, Assert.Single(host.Snapshot()).Phase);
        for (var tick = 2; tick <= 10; tick++)
            host.OnFrameworkUpdate(tick);
        Assert.DoesNotContain("delete 200", scene.Calls);
        host.OnFrameworkUpdate(11);
        Assert.Contains("delete 200", scene.Calls);
    }

    [Fact]
    public void ACarrierBeforeTheAssignmentIsACollisionStop()
    {
        var host = Ready();
        host.NameSource = _ => FixedName;
        var handle = host.Spawn().Handle!.Value;
        Assert.True(host.RequestDraw(handle).Done);
        scene.Injected[57] = new(57, FixedName, HumanPuppetWorld.AnyWorld);
        Assert.Equal(HumanPuppetStopRule.Collision, host.AssignTemporaryCollection(handle).Stop!.Rule);
        Assert.DoesNotContain("assign 200", scene.Calls);
        Assert.Contains("delete collection", scene.Calls);
    }

    [Fact]
    public void ARefusedAssignmentIsACollisionAndTheCollectionIsDeleted()
    {
        scene.AssignResult = HumanAssignmentResult.Refused;
        var (host, handle) = Drawn();
        Assert.Equal(HumanPuppetStopRule.Collision, host.AssignTemporaryCollection(handle).Stop!.Rule);
        Assert.DoesNotContain("unassign 200", scene.Calls);
        Assert.Contains("delete collection", scene.Calls);
        Assert.Empty(scene.TemporaryCollections);
    }

    // ---- P13 with D1 and D2 ----

    [Fact]
    public void RemovalWithoutAnyChangeDeletesInTheSameCall()
    {
        var (host, handle) = Spawned();
        Assert.True(host.Remove(handle).Done);
        var hide = IndexOfCall(scene.Calls, "draw off 200");
        Assert.True(hide < IndexOfCall(scene.Calls, "delete 200"));
        Assert.Equal(HumanPuppetPhase.Deleted, Assert.Single(host.Snapshot()).Phase);
        Assert.Equal(0, host.OccupiedSlots);
        Assert.Null(host.Stop);
    }

    [Fact]
    public void RemovalRemovesTheAssignmentBeforeTheCollectionAndDeletesOnlyAfterTPlusEight()
    {
        var (host, handle) = Drawn();
        host.OnFrameworkUpdate(2);
        Assert.True(host.AssignTemporaryCollection(handle).Done);
        host.OnFrameworkUpdate(3);
        Assert.True(host.Remove(handle).Done);
        Assert.True(IndexOfCall(scene.Calls, "unassign 200") < IndexOfCall(scene.Calls, "delete collection"));
        var snapshot = Assert.Single(host.Snapshot());
        Assert.Equal(HumanPuppetPhase.AwaitingDeletion, snapshot.Phase);
        // The change was made after update 3's handler, so it is counted as update 4.
        Assert.Equal(4, snapshot.LatestChangeUpdate);
        Assert.Equal(1, host.OccupiedSlots);
        for (var tick = 4; tick <= 12; tick++)
        {
            host.OnFrameworkUpdate(tick);
            Assert.True(scene.Exists(200), $"deleted at update {tick}");
        }
        host.OnFrameworkUpdate(13);
        Assert.False(scene.Exists(200));
        Assert.Equal(13, Assert.Single(host.Snapshot()).DeletedUpdate);
        Assert.Single(scene.Calls, call => call == "delete 200");
    }

    [Fact]
    public void APuppetAwaitingD2StillTakesItsSlot()
    {
        var host = Ready(limit: 1);
        var handle = host.Spawn().Handle!.Value;
        Assert.True(host.RequestDraw(handle).Done);
        Assert.True(host.AssignTemporaryCollection(handle).Done);
        host.Remove(handle);
        Assert.Equal(HumanPuppetRefusal.AtCapacity, host.Spawn().Refusal);
    }

    [Fact]
    public void AnAssignmentThatCannotBeRemovedKeepsTheCollectionUnderD1()
    {
        var (host, handle) = Drawn();
        Assert.True(host.AssignTemporaryCollection(handle).Done);
        scene.RemoveAssignmentFails = true;
        host.Remove(handle);
        Assert.DoesNotContain("delete collection", scene.Calls);
        Assert.Equal(HumanPuppetStopRule.S6Residue, host.Stop!.Rule);
        Assert.Contains(host.Residue, item => item.Reason.Contains("D1", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnverifiedMapWithAnAssignmentMakesNoByIndexCall()
    {
        var (host, handle) = Drawn();
        Assert.True(host.AssignTemporaryCollection(handle).Done);
        scene.SetEntries(200, 0);
        scene.FailPenumbraSet = true;
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.S4ParentMapOrIsolation, host.Stop!.Rule);
        Assert.DoesNotContain("unassign 200", scene.Calls);
        Assert.DoesNotContain("delete collection", scene.Calls);
        Assert.Equal(HumanPuppetPhase.Residue, Assert.Single(host.Snapshot()).Phase);
        Assert.Contains(host.Residue, item => item.Reason.Contains("no by-index call", StringComparison.Ordinal));
        Assert.False(scene.At(200).DrawEnabled);
    }

    [Fact]
    public void AnUnverifiedIdentityWithAnAssignmentMakesNoByIndexCall()
    {
        var (host, handle) = Drawn();
        Assert.True(host.AssignTemporaryCollection(handle).Done);
        scene.At(200).World = FakeHumanScene.PlayerWorld;
        host.Remove(handle);
        Assert.Equal(HumanPuppetStopRule.S5Identity, host.Stop!.Rule);
        Assert.DoesNotContain("unassign 200", scene.Calls);
        Assert.DoesNotContain("delete collection", scene.Calls);
        Assert.Equal(HumanPuppetPhase.Residue, Assert.Single(host.Snapshot()).Phase);
    }

    [Fact]
    public void AReplacedObjectIsNeverTouched()
    {
        var (host, handle) = Spawned();
        scene.Replace(200);
        var before = scene.Calls.Count;
        host.Remove(handle);
        Assert.Equal(before, scene.Calls.Count);
        Assert.Equal(HumanPuppetPhase.Residue, Assert.Single(host.Snapshot()).Phase);
        Assert.Equal(HumanPuppetStopRule.S6Residue, host.Stop!.Rule);
        Assert.True(scene.Exists(200));
    }

    [Fact]
    public void AFailedDeletionIsResidue()
    {
        scene.DeleteFails = true;
        var (host, handle) = Spawned();
        host.Remove(handle);
        Assert.Equal(HumanPuppetPhase.Residue, Assert.Single(host.Snapshot()).Phase);
        Assert.Equal(HumanPuppetStopRule.S6Residue, host.Stop!.Rule);
    }

    // ---- The stop latch (H-10, nothing retried) ----

    [Fact]
    public void AfterAStopNothingIsSpawnedDrawnOrAssigned()
    {
        var host = Ready(limit: 2);
        var first = host.Spawn().Handle!.Value;
        var second = host.Spawn().Handle!.Value;
        scene.SetEntries(200, 0);
        host.OnFrameworkUpdate(2);
        Assert.NotNull(host.Stop);
        // Both puppets were cleaned up, not only the failing one.
        Assert.All(host.Snapshot(), snapshot => Assert.Equal(HumanPuppetPhase.Deleted, snapshot.Phase));
        Assert.Equal(HumanPuppetRefusal.Stopped, host.Spawn().Refusal);
        Assert.Same(host.Stop, host.RequestDraw(first).Stop);
        Assert.Same(host.Stop, host.AssignTemporaryCollection(second).Stop);
        Assert.Same(host.Stop, host.CheckCustomizePlus().Stop);
    }

    // ---- M-2 baseline ----

    [Fact]
    public void TheBaselineCoversEverySlotFromTwoHundredToFourThirtyNine()
    {
        var host = Ready();
        var before = host.CaptureBaseline();
        Assert.Equal(HumanPuppetSlotRange.Count, before.Penumbra.Length);
        Assert.Equal(240, before.CustomizePlus.Length);
        Assert.Equal<(int, int)?>((6, 4), before.CustomizePlusApiVersion);
        Assert.Equal<(bool, bool)?>((false, true), before.GlamourerCanUnlockBaseline);
        scene.SetEntries(300, 0);
        var after = host.CaptureBaseline();
        Assert.Equal(new[] { 300 }, before.ChangedSlots(after));
        Assert.Empty(before.ChangedSlots(after, new HashSet<int> { 300 }));
    }

    // ---- H-7: the unload order ----

    [Fact]
    public async Task UnloadHidesDrainsCleansUpAndReleasesDependenciesInOrder()
    {
        var (host, handle) = Spawned();
        Assert.True(host.RequestDraw(handle).Done);
        var events = new List<string>();
        var work = new TaskCompletionSource();
        Assert.True(host.TrackWork(work.Task));
        host.RegisterDependency(new Disposable(() => events.Add($"first released, work done {work.Task.IsCompleted}")));
        host.RegisterDependency(new Disposable(() => events.Add("second released")));

        var unload = host.UnloadAsync(action =>
        {
            action();
            events.Add($"game thread, deleted {!scene.Exists(200)}");
            return Task.CompletedTask;
        });
        // Hidden before the drain, deleted only after it.
        Assert.False(scene.At(200).DrawEnabled);
        Assert.False(unload.IsCompleted);
        Assert.False(host.TrackWork(Task.CompletedTask));
        Assert.Equal(HumanPuppetRefusal.Unloading, host.Spawn().Refusal);
        // Nothing runs P13 before the drain: removal is refused and updates only verify.
        Assert.Equal(HumanPuppetRefusal.Unloading, host.Remove(handle).Refusal);
        host.OnFrameworkUpdate(2);
        Assert.True(scene.Exists(200));
        work.SetResult();
        var report = await unload;

        Assert.Equal(new[] { "refuse new work", "hide and revalidate", "drain work", "clean up puppets", "release dependencies" }, report.Order);
        Assert.Equal(new[] { "game thread, deleted False", "game thread, deleted True", "second released", "first released, work done True" }, events);
        Assert.Equal(1, report.Deleted);
        Assert.Equal(1, report.DrainedWork);
        Assert.Empty(report.Residue);
        Assert.Empty(report.DependencyFailures);
    }

    [Fact]
    public async Task UnloadNeverDeletesInsideD2()
    {
        var (host, handle) = Drawn();
        Assert.True(host.AssignTemporaryCollection(handle).Done);
        var report = await host.UnloadAsync(action =>
        {
            action();
            return Task.CompletedTask;
        });
        Assert.True(scene.Exists(200));
        Assert.Equal(0f, scene.At(200).Alpha);
        Assert.False(scene.At(200).DrawEnabled);
        Assert.Equal(0, report.Deleted);
        Assert.Contains(report.Residue, item => item.Reason.Contains("D2", StringComparison.Ordinal));
        Assert.Equal(HumanPuppetStopRule.S6Residue, host.Stop!.Rule);
        Assert.DoesNotContain("delete 200", scene.Calls);
        // D1 still ran: the assignment and the collection are gone.
        Assert.Empty(scene.Assigned);
        Assert.Empty(scene.TemporaryCollections);
    }

    [Fact]
    public async Task AFailingDependencyDoesNotStopTheOthers()
    {
        var host = Ready();
        var released = false;
        host.RegisterDependency(new Disposable(() => released = true));
        host.RegisterDependency(new Disposable(() => throw new InvalidOperationException("synthetic")));
        var report = await host.UnloadAsync(action =>
        {
            action();
            return Task.CompletedTask;
        });
        Assert.True(released);
        Assert.Equal(new[] { "release: " + nameof(InvalidOperationException) }, report.DependencyFailures);
        Assert.Throws<InvalidOperationException>(() => host.RegisterDependency(new Disposable(() => { })));
    }

    // ---- H-12: the Glamourer scope ----

    [Fact]
    public void TheGlamourerPortIsReadOnly()
    {
        var members = typeof(IHumanGlamourerReadPort).GetMembers().Select(member => member.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "ApiVersion", "CanUnlockBaseline", "get_ApiVersion" }, members);
    }

    [Theory]
    [InlineData("ApplyState")]
    [InlineData("UnlockAll")]
    [InlineData("DeletePlayerState")]
    [InlineData("SetCollectionForObject")]
    [InlineData("P14")]
    [InlineData("Revert")]
    public void NoHumanHostTypeHasAStateWritingMember(string name)
    {
        // H-12, amended for HV-3 (Debug only, owner-approved): exactly these
        // three HV-3 types may name "ApplyState" and "Revert", because HV-3
        // applies and reverts one equipment-only state by object index. They
        // stay under every other row, and every other type stays under all rows.
        string[] exempt = name is "ApplyState" or "Revert" ? Hv3StateTypes : [];
        var all = typeof(HumanPuppetHost).Assembly.GetTypes()
            .Where(type => type.Namespace == "RSyncStageCraft.Core.Actors.Humans").ToArray();
        // The exemption is never vacuous: each named type exists.
        Assert.All(Hv3StateTypes, exempted => Assert.Contains(all, type => type.FullName == exempted));
        var types = all.Where(type => !exempt.Contains(type.FullName)).ToArray();
        Assert.Equal(all.Length - exempt.Length, types.Length);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Assert.All(types, type => Assert.DoesNotContain(type.GetMembers(flags), member => member.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>HV-3's port, stage and state enum: the only H-12 exemption, for the "ApplyState" and "Revert" rows only.</summary>
    private static readonly string[] Hv3StateTypes =
    [
        "RSyncStageCraft.Core.Actors.Humans.IHumanActorGlamourer",
        "RSyncStageCraft.Core.Actors.Humans.HumanActorGlamourerStage",
        "RSyncStageCraft.Core.Actors.Humans.HumanGlamourerState",
    ];

    [Fact]
    public void BaselineReadsAreTheOnlyGlamourerCalls()
    {
        var (host, handle) = Spawned();
        host.RequestDraw(handle);
        host.VerifyIsolation(handle);
        host.AssignTemporaryCollection(handle);
        host.Remove(handle);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("glamourer", StringComparison.Ordinal));
        host.CaptureBaseline();
        Assert.Equal("glamourer can unlock 0 0", Assert.Single(scene.Calls, call => call.StartsWith("glamourer", StringComparison.Ordinal)));
    }

    private sealed class Disposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
#endif
