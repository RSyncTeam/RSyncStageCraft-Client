using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class LocalObjectReconcilerTests
{
    [Fact]
    public async Task CompatibleStateUpdatesStableOwnedReceipt()
    {
        var fixture = new Fixture();
        var first = fixture.Token(1);
        fixture.Fence.Advance(first);

        var spawned = await fixture.Reconciler.ReconcileAsync(
            first,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        var second = fixture.Token(2);
        fixture.Fence.Advance(second);
        var updated = await fixture.Reconciler.ReconcileAsync(
            second,
            fixture.Furniture(2, "furniture:indoor:6000", 9));

        Assert.True(spawned.Succeeded);
        Assert.True(updated.Succeeded);
        Assert.Equal(spawned.Handle, updated.Handle);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(1, fixture.Runtime.ApplyCalls);
        Assert.Equal(0, fixture.Runtime.ReleaseCalls);
        Assert.Equal(2, fixture.Dispatcher.Calls);
    }

    [Fact]
    public async Task ExactAppliedGenerationIsAnExternalNoOp()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        var state = fixture.Furniture(1, "furniture:indoor:6000", 1);
        fixture.Fence.Advance(token);
        Assert.True((await fixture.Reconciler.ReconcileAsync(token, state)).Succeeded);

        var repeated = await fixture.Reconciler.ReconcileAsync(
            token,
            state with { Revision = new ObjectRevision(2) });

        Assert.True(repeated.Succeeded);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(0, fixture.Runtime.ApplyCalls);
        Assert.Equal(2, fixture.Dispatcher.Calls);
    }

    [Fact]
    public async Task IdentityChangeOrdersReleaseCheckpointBeforeNextSpawn()
    {
        var fixture = new Fixture();
        var first = fixture.Token(1);
        fixture.Fence.Advance(first);
        Assert.True((await fixture.Reconciler.ReconcileAsync(
            first,
            fixture.Furniture(1, "furniture:indoor:6000", 1))).Succeeded);
        fixture.Runtime.Events.Clear();

        var second = fixture.Token(2);
        fixture.Fence.Advance(second);
        var replacement = await fixture.Reconciler.ReconcileAsync(
            second,
            fixture.Furniture(2, "furniture:indoor:6002", 2));

        Assert.True(replacement.Succeeded);
        Assert.Equal(2, replacement.Handle!.Value.SpawnGeneration);
        Assert.Equal(
            new[] { "release:1", "spawn:furniture:indoor:6002:2" },
            fixture.Runtime.Events);
        Assert.True(fixture.Ledger.TryGet(second, out var retained));
        Assert.Equal(LocalObjectReconcileAction.Update, retained.Action);
        Assert.Equal(replacement.Handle, retained.Handle);
    }

    [Fact]
    public async Task ApplyCallbackThatBecomesStaleCannotCallRuntime()
    {
        var fixture = new Fixture();
        var first = fixture.Token(1);
        var second = fixture.Token(2);
        fixture.Fence.Advance(first);
        fixture.Dispatcher.BeforeInvoke = () => fixture.Fence.Advance(second);

        var result = await fixture.Reconciler.ReconcileAsync(
            first,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.Stale, result.Status);
        Assert.Empty(fixture.Runtime.Events);
        Assert.True(fixture.Ledger.TryGet(first, out var empty));
        Assert.Null(empty.Handle);
    }

    [Fact]
    public async Task FailedReplacementReleaseRetainsOldExactHandleForRetry()
    {
        var fixture = new Fixture();
        var first = fixture.Token(1);
        fixture.Fence.Advance(first);
        var original = await fixture.Reconciler.ReconcileAsync(
            first,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        var second = fixture.Token(2);
        fixture.Fence.Advance(second);
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.TransientFailure;
        var failed = await fixture.Reconciler.ReconcileAsync(
            second,
            fixture.Furniture(2, "furniture:indoor:6002", 2));

        Assert.Equal(LocalObjectReconcileStatus.TransientFailure, failed.Status);
        Assert.True(fixture.Ledger.TryGet(second, out var retained));
        Assert.Equal(original.Handle, retained.Handle);
        Assert.Equal(LocalObjectReconcileAction.Replace, retained.Action);

        fixture.Runtime.ReleaseStatus = ApplyPortStatus.Succeeded;
        var retried = await fixture.Reconciler.ReconcileAsync(
            second,
            fixture.Furniture(2, "furniture:indoor:6002", 2));

        Assert.True(retried.Succeeded);
        Assert.Equal(2, retried.Handle!.Value.SpawnGeneration);
    }

    [Fact]
    public async Task ReplacementReleaseExceptionRetainsOldExactHandleForRetry()
    {
        var fixture = new Fixture();
        var first = fixture.Token(1);
        fixture.Fence.Advance(first);
        var original = await fixture.Reconciler.ReconcileAsync(
            first,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        var second = fixture.Token(2);
        fixture.Fence.Advance(second);
        fixture.Runtime.ReleaseException = new InvalidOperationException("release fault");
        var failed = await fixture.Reconciler.ReconcileAsync(
            second,
            fixture.Furniture(2, "furniture:indoor:6002", 2));

        Assert.Equal(LocalObjectReconcileStatus.TransientFailure, failed.Status);
        Assert.True(fixture.Ledger.TryGet(second, out var retained));
        Assert.Equal(original.Handle, retained.Handle);
        Assert.Equal(LocalObjectReconcileAction.Replace, retained.Action);

        fixture.Runtime.ReleaseException = null;
        var retried = await fixture.Reconciler.ReconcileAsync(
            second,
            fixture.Furniture(2, "furniture:indoor:6002", 2));

        Assert.True(retried.Succeeded);
        Assert.Equal(2, retried.Handle!.Value.SpawnGeneration);
    }

    [Fact]
    public async Task MismatchedSpawnReceiptIsCompensatedAndNeverRecorded()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        fixture.Fence.Advance(token);
        fixture.Runtime.SpawnFactory = request => new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            request.Token.ObjectId,
            RscObjectKind.Light,
            request.SpawnGeneration);

        var result = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.PermanentFailure, result.Status);
        Assert.Equal(1, fixture.Runtime.ReleaseCalls);
        Assert.True(fixture.Ledger.TryGet(token, out var empty));
        Assert.Null(empty.Handle);
    }

    [Fact]
    public async Task FailedMismatchedSpawnCompensationRemainsTrackedUntilRetry()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        fixture.Fence.Advance(token);
        var mismatched = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            fixture.ObjectId,
            RscObjectKind.Light,
            1);
        fixture.Runtime.SpawnFactory = _ => mismatched;
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.TransientFailure;

        var failed = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.TransientFailure, failed.Status);
        Assert.True(fixture.Ledger.TryGet(token, out var retained));
        Assert.Null(retained.Handle);
        Assert.Equal(mismatched, retained.Compensation);
        Assert.Equal(1, fixture.Ledger.CompensationCount);

        fixture.Runtime.ReleaseStatus = ApplyPortStatus.Succeeded;
        fixture.Runtime.SpawnFactory = null;
        var retried = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.True(retried.Succeeded);
        Assert.Equal(2, fixture.Runtime.ReleaseCalls);
        Assert.Equal(0, fixture.Ledger.CompensationCount);
        Assert.Equal(1, fixture.Ledger.OwnedCount);
    }

    [Fact]
    public async Task CleanupCannotRemoveLedgerDuringSpawn()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        fixture.Fence.Advance(token);
        LocalObjectOwnershipCleanupResult cleanupResult = default;
        fixture.Runtime.DuringSpawn = _ =>
        {
            var cleanup = new LocalObjectOwnershipCleanup(
                fixture.Ledger,
                fixture.Dispatcher,
                fixture.Runtime);
            cleanupResult = cleanup.CleanupPassAsync(token.Session, 1)
                .GetAwaiter()
                .GetResult();
        };

        var result = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.True(result.Succeeded);
        Assert.Equal(0, cleanupResult.Released);
        Assert.Equal(1, cleanupResult.Failed);
        Assert.True(cleanupResult.HasRemaining);
        Assert.True(fixture.Ledger.TryGet(token, out var retained));
        Assert.Equal(result.Handle, retained.Handle);
        Assert.Equal(1, fixture.Ledger.OwnedCount);
    }

    [Fact]
    public async Task NewGenerationCannotDisplaceLedgerDuringSpawnPublication()
    {
        var fixture = new Fixture();
        var applying = fixture.Token(1);
        var replacement = fixture.Token(2);
        var replacementState = fixture.Furniture(2, "furniture:indoor:6000", 2);
        var replacementIdentity = LocalObjectSpawnIdentity.FromState(replacementState);
        LocalObjectReservationResult attemptedReservation = default;
        fixture.Fence.Advance(applying);
        fixture.Runtime.DuringSpawn = _ =>
        {
            fixture.Fence.Advance(replacement);
            attemptedReservation = fixture.Ledger.TryReserve(
                replacement,
                replacementIdentity);
        };

        var result = await fixture.Reconciler.ReconcileAsync(
            applying,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.Stale, result.Status);
        Assert.Equal(
            LocalObjectReservationStatus.MutationInProgress,
            attemptedReservation.Status);
        Assert.True(fixture.Ledger.TryGet(applying, out var retained));
        Assert.Equal(result.Handle, retained.Handle);
        Assert.False(fixture.Ledger.IsApplied(applying));
        Assert.Equal(0, fixture.Runtime.ReleaseCalls);

        Assert.True(fixture.Ledger.TryReserve(
            replacement,
            replacementIdentity).Accepted);
        Assert.False(fixture.Ledger.TryGet(applying, out _));
        Assert.True(fixture.Ledger.TryGet(replacement, out var transferred));
        Assert.Equal(retained.Handle, transferred.Handle);
    }

    [Fact]
    public async Task NewGenerationDuringReplacementReleaseStopsStaleRespawn()
    {
        var fixture = new Fixture();
        var original = fixture.Token(1);
        fixture.Fence.Advance(original);
        Assert.True((await fixture.Reconciler.ReconcileAsync(
            original,
            fixture.Furniture(1, "furniture:indoor:6000", 1))).Succeeded);

        var replacing = fixture.Token(2);
        var replacement = fixture.Token(3);
        var replacementState = fixture.Furniture(3, "furniture:indoor:6001", 3);
        var replacementIdentity = LocalObjectSpawnIdentity.FromState(replacementState);
        LocalObjectReservationResult attemptedReservation = default;
        fixture.Fence.Advance(replacing);
        fixture.Runtime.DuringRelease = _ =>
        {
            fixture.Fence.Advance(replacement);
            attemptedReservation = fixture.Ledger.TryReserve(
                replacement,
                replacementIdentity);
        };

        var result = await fixture.Reconciler.ReconcileAsync(
            replacing,
            fixture.Furniture(2, "furniture:indoor:6002", 2));

        Assert.Equal(LocalObjectReconcileStatus.Stale, result.Status);
        Assert.Equal(
            LocalObjectReservationStatus.ReleaseInProgress,
            attemptedReservation.Status);
        Assert.Equal(1, fixture.Runtime.ReleaseCalls);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.True(fixture.Ledger.TryGet(replacing, out var released));
        Assert.Null(released.Handle);

        Assert.True(fixture.Ledger.TryReserve(
            replacement,
            replacementIdentity).Accepted);
        Assert.True(fixture.Ledger.TryGet(replacement, out var transferred));
        Assert.Null(transferred.Handle);
    }

    [Fact]
    public async Task NewGenerationDuringCompensationReleaseStopsStaleRespawn()
    {
        var fixture = new Fixture();
        var applying = fixture.Token(1);
        var failedReceipt = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            fixture.ObjectId,
            RscObjectKind.Furniture,
            1);
        fixture.Fence.Advance(applying);
        fixture.Runtime.SpawnStatus = ApplyPortStatus.TransientFailure;
        fixture.Runtime.FailedSpawnHandle = failedReceipt;
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.TransientFailure;
        Assert.Equal(
            LocalObjectReconcileStatus.TransientFailure,
            (await fixture.Reconciler.ReconcileAsync(
                applying,
                fixture.Furniture(1, "furniture:indoor:6000", 1))).Status);

        var replacement = fixture.Token(2);
        var replacementState = fixture.Furniture(2, "furniture:indoor:6000", 2);
        var replacementIdentity = LocalObjectSpawnIdentity.FromState(replacementState);
        LocalObjectReservationResult attemptedReservation = default;
        fixture.Runtime.SpawnStatus = ApplyPortStatus.Succeeded;
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.Succeeded;
        fixture.Runtime.DuringRelease = _ =>
        {
            fixture.Fence.Advance(replacement);
            attemptedReservation = fixture.Ledger.TryReserve(
                replacement,
                replacementIdentity);
        };

        var result = await fixture.Reconciler.ReconcileAsync(
            applying,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.Stale, result.Status);
        Assert.Equal(
            LocalObjectReservationStatus.ReleaseInProgress,
            attemptedReservation.Status);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(2, fixture.Runtime.ReleaseCalls);
        Assert.True(fixture.Ledger.TryGet(applying, out var released));
        Assert.Null(released.Handle);
        Assert.Null(released.Compensation);

        Assert.True(fixture.Ledger.TryReserve(
            replacement,
            replacementIdentity).Accepted);
        Assert.True(fixture.Ledger.TryGet(replacement, out var transferred));
        Assert.Null(transferred.Handle);
        Assert.Null(transferred.Compensation);
    }

    [Fact]
    public async Task NewGenerationDuringUpdateCannotPublishStaleAppliedMarker()
    {
        var fixture = new Fixture();
        var original = fixture.Token(1);
        fixture.Fence.Advance(original);
        var spawned = await fixture.Reconciler.ReconcileAsync(
            original,
            fixture.Furniture(1, "furniture:indoor:6000", 1));
        Assert.True(spawned.Succeeded);

        var updating = fixture.Token(2);
        var replacement = fixture.Token(3);
        var identity = LocalObjectSpawnIdentity.FromState(
            fixture.Furniture(3, "furniture:indoor:6000", 3));
        fixture.Fence.Advance(updating);
        fixture.Runtime.DuringApply = _ => fixture.Fence.Advance(replacement);

        var result = await fixture.Reconciler.ReconcileAsync(
            updating,
            fixture.Furniture(2, "furniture:indoor:6000", 2));

        Assert.Equal(LocalObjectReconcileStatus.Stale, result.Status);
        Assert.Equal(1, fixture.Runtime.ApplyCalls);
        Assert.True(fixture.Ledger.TryGet(updating, out var retained));
        Assert.Equal(spawned.Handle, retained.Handle);
        Assert.False(fixture.Ledger.IsApplied(updating));

        Assert.True(fixture.Ledger.TryReserve(replacement, identity).Accepted);
        Assert.True(fixture.Ledger.TryGet(replacement, out var transferred));
        Assert.Equal(retained.Handle, transferred.Handle);
    }

    [Fact]
    public async Task NewGenerationOverridesObsoleteReplacementReleaseFailure()
    {
        var fixture = new Fixture();
        var original = fixture.Token(1);
        fixture.Fence.Advance(original);
        var spawned = await fixture.Reconciler.ReconcileAsync(
            original,
            fixture.Furniture(1, "furniture:indoor:6000", 1));
        Assert.True(spawned.Succeeded);

        var replacing = fixture.Token(2);
        var replacement = fixture.Token(3);
        var replacementState = fixture.Furniture(3, "furniture:indoor:6001", 3);
        var replacementIdentity = LocalObjectSpawnIdentity.FromState(replacementState);
        fixture.Fence.Advance(replacing);
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.TransientFailure;
        fixture.Runtime.DuringRelease = _ => fixture.Fence.Advance(replacement);

        var result = await fixture.Reconciler.ReconcileAsync(
            replacing,
            fixture.Furniture(2, "furniture:indoor:6002", 2));

        Assert.Equal(LocalObjectReconcileStatus.Stale, result.Status);
        Assert.Equal(1, fixture.Runtime.ReleaseCalls);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.True(fixture.Ledger.TryGet(replacing, out var retained));
        Assert.Equal(spawned.Handle, retained.Handle);
        Assert.False(fixture.Ledger.IsApplied(replacing));

        Assert.True(fixture.Ledger.TryReserve(
            replacement,
            replacementIdentity).Accepted);
        Assert.True(fixture.Ledger.TryGet(replacement, out var transferred));
        Assert.Equal(retained.Handle, transferred.Handle);
    }

    [Fact]
    public async Task NewGenerationOverridesObsoleteUpdateException()
    {
        var fixture = new Fixture();
        var original = fixture.Token(1);
        fixture.Fence.Advance(original);
        var spawned = await fixture.Reconciler.ReconcileAsync(
            original,
            fixture.Furniture(1, "furniture:indoor:6000", 1));
        Assert.True(spawned.Succeeded);

        var updating = fixture.Token(2);
        var replacement = fixture.Token(3);
        var replacementState = fixture.Furniture(3, "furniture:indoor:6000", 3);
        var replacementIdentity = LocalObjectSpawnIdentity.FromState(replacementState);
        fixture.Fence.Advance(updating);
        fixture.Runtime.DuringApply = _ =>
        {
            fixture.Fence.Advance(replacement);
            throw new InvalidOperationException("obsolete update failed");
        };

        var result = await fixture.Reconciler.ReconcileAsync(
            updating,
            fixture.Furniture(2, "furniture:indoor:6000", 2));

        Assert.Equal(LocalObjectReconcileStatus.Stale, result.Status);
        Assert.Equal(1, fixture.Runtime.ApplyCalls);
        Assert.True(fixture.Ledger.TryGet(updating, out var retained));
        Assert.Equal(spawned.Handle, retained.Handle);
        Assert.False(fixture.Ledger.IsApplied(updating));

        Assert.True(fixture.Ledger.TryReserve(
            replacement,
            replacementIdentity).Accepted);
        Assert.True(fixture.Ledger.TryGet(replacement, out var transferred));
        Assert.Equal(retained.Handle, transferred.Handle);
    }

    [Fact]
    public async Task FailedSpawnReceiptRemainsTrackedWhenCompensationFails()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        var failedReceipt = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            fixture.ObjectId,
            RscObjectKind.Furniture,
            1);
        fixture.Fence.Advance(token);
        fixture.Runtime.SpawnStatus = ApplyPortStatus.TransientFailure;
        fixture.Runtime.FailedSpawnHandle = failedReceipt;
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.TransientFailure;

        var result = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.TransientFailure, result.Status);
        Assert.Equal(1, fixture.Runtime.ReleaseCalls);
        Assert.True(fixture.Ledger.TryGet(token, out var retained));
        Assert.Null(retained.Handle);
        Assert.Equal(failedReceipt, retained.Compensation);
        Assert.Equal(1, fixture.Ledger.CompensationCount);
    }

    [Fact]
    public async Task SpawnExceptionReleasesMutationClaimForCleanup()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        fixture.Fence.Advance(token);
        fixture.Runtime.SpawnFactory = _ => throw new InvalidOperationException("spawn fault");

        var failed = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));
        var cleanup = new LocalObjectOwnershipCleanup(
            fixture.Ledger,
            fixture.Dispatcher,
            fixture.Runtime);
        var cleaned = await cleanup.CleanupPassAsync(token.Session, 1);

        Assert.Equal(LocalObjectReconcileStatus.TransientFailure, failed.Status);
        Assert.Equal(1, cleaned.Released);
        Assert.False(cleaned.HasRemaining);
        Assert.Equal(0, fixture.Ledger.Count);
    }

    [Fact]
    public async Task ThrowingExceptionMessageCannotStrandLocalOwnershipClaim()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        fixture.Fence.Advance(token);
        fixture.Runtime.SpawnFactory = _ => throw new ThrowingMessageException();

        var failed = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));
        var cleanup = new LocalObjectOwnershipCleanup(
            fixture.Ledger,
            fixture.Dispatcher,
            fixture.Runtime);
        var cleaned = await cleanup.CleanupPassAsync(token.Session, 1);

        Assert.Equal(LocalObjectReconcileStatus.TransientFailure, failed.Status);
        Assert.Equal(ReceiverFailureDiagnostic.Fallback, failed.Error);
        Assert.Equal(1, cleaned.Released);
        Assert.False(cleaned.HasRemaining);
        Assert.Equal(0, fixture.Ledger.Count);
    }

    [Fact]
    public async Task UnavailableBeforeDispatchDoesNotReserveOrMutate()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        fixture.Fence.Advance(token);
        fixture.Readiness.IsReady = false;

        var result = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.Unavailable, result.Status);
        Assert.Equal(0, fixture.Dispatcher.Calls);
        Assert.Equal(0, fixture.Ledger.Count);
        Assert.Empty(fixture.Runtime.Events);
    }

    [Fact]
    public async Task ReadinessLossBeforeDispatchedOperationDoesNotMutate()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        fixture.Fence.Advance(token);
        fixture.Dispatcher.BeforeInvoke = () => fixture.Readiness.IsReady = false;

        var result = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.Unavailable, result.Status);
        Assert.Equal(1, fixture.Dispatcher.Calls);
        Assert.Empty(fixture.Runtime.Events);
        Assert.True(fixture.Ledger.TryGet(token, out var retained));
        Assert.Null(retained.Handle);
        Assert.Null(retained.Compensation);
    }

    [Fact]
    public async Task ReadinessLossDuringReplacementReleaseStopsBeforeSpawn()
    {
        var fixture = new Fixture();
        var first = fixture.Token(1);
        fixture.Fence.Advance(first);
        Assert.True((await fixture.Reconciler.ReconcileAsync(
            first,
            fixture.Furniture(1, "furniture:indoor:6000", 1))).Succeeded);

        var replacement = fixture.Token(2);
        fixture.Fence.Advance(replacement);
        fixture.Runtime.DuringRelease = _ => fixture.Readiness.IsReady = false;

        var unavailable = await fixture.Reconciler.ReconcileAsync(
            replacement,
            fixture.Furniture(2, "furniture:indoor:6002", 2));

        Assert.Equal(LocalObjectReconcileStatus.Unavailable, unavailable.Status);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(1, fixture.Runtime.ReleaseCalls);
        Assert.True(fixture.Ledger.TryGet(replacement, out var retained));
        Assert.Null(retained.Handle);
        Assert.Equal(2, retained.NextSpawnGeneration);

        fixture.Runtime.DuringRelease = null;
        fixture.Readiness.IsReady = true;
        var retried = await fixture.Reconciler.ReconcileAsync(
            replacement,
            fixture.Furniture(2, "furniture:indoor:6002", 2));

        Assert.True(retried.Succeeded);
        Assert.Equal(2, fixture.Runtime.SpawnCalls);
        Assert.Equal(1, fixture.Runtime.ReleaseCalls);
        Assert.Equal(2, retried.Handle!.Value.SpawnGeneration);
    }

    [Fact]
    public async Task ReadinessLossDuringCompensationReleaseStopsBeforeSpawn()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        fixture.Fence.Advance(token);
        fixture.Runtime.SpawnStatus = ApplyPortStatus.TransientFailure;
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.TransientFailure;
        fixture.Runtime.FailedSpawnHandle = new(
            Guid.NewGuid(),
            fixture.ObjectId,
            RscObjectKind.Furniture,
            1);
        Assert.Equal(
            LocalObjectReconcileStatus.TransientFailure,
            (await fixture.Reconciler.ReconcileAsync(
                token,
                fixture.Furniture(1, "furniture:indoor:6000", 1))).Status);

        fixture.Runtime.SpawnStatus = ApplyPortStatus.Succeeded;
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.Succeeded;
        fixture.Runtime.DuringRelease = _ => fixture.Readiness.IsReady = false;
        var unavailable = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.Unavailable, unavailable.Status);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(2, fixture.Runtime.ReleaseCalls);
        Assert.True(fixture.Ledger.TryGet(token, out var retained));
        Assert.Null(retained.Compensation);
        Assert.Null(retained.Handle);

        fixture.Runtime.DuringRelease = null;
        fixture.Readiness.IsReady = true;
        var retried = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.True(retried.Succeeded);
        Assert.Equal(2, fixture.Runtime.SpawnCalls);
        Assert.Equal(2, fixture.Runtime.ReleaseCalls);
    }

    [Fact]
    public async Task ReadinessLossDuringSpawnRetainsExactReceiptForRetry()
    {
        var fixture = new Fixture();
        var token = fixture.Token(1);
        fixture.Fence.Advance(token);
        fixture.Runtime.DuringSpawn = _ => fixture.Readiness.IsReady = false;

        var unavailable = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.Equal(LocalObjectReconcileStatus.Unavailable, unavailable.Status);
        Assert.True(unavailable.Handle!.Value.IsValid);
        Assert.True(fixture.Ledger.TryGet(token, out var retained));
        Assert.Equal(unavailable.Handle, retained.Handle);
        Assert.False(fixture.Ledger.IsApplied(token));
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(0, fixture.Runtime.ApplyCalls);

        fixture.Runtime.DuringSpawn = null;
        fixture.Readiness.IsReady = true;
        var retried = await fixture.Reconciler.ReconcileAsync(
            token,
            fixture.Furniture(1, "furniture:indoor:6000", 1));

        Assert.True(retried.Succeeded);
        Assert.Equal(unavailable.Handle, retried.Handle);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(1, fixture.Runtime.ApplyCalls);
        Assert.True(fixture.Ledger.IsApplied(token));
    }

    [Fact]
    public async Task ReadinessLossDuringUpdateRetainsReceiptWithoutAppliedCheckpoint()
    {
        var fixture = new Fixture();
        var first = fixture.Token(1);
        fixture.Fence.Advance(first);
        var applied = await fixture.Reconciler.ReconcileAsync(
            first,
            fixture.Furniture(1, "furniture:indoor:6000", 1));
        Assert.True(applied.Succeeded);

        var update = fixture.Token(2);
        fixture.Fence.Advance(update);
        fixture.Runtime.DuringApply = _ => fixture.Readiness.IsReady = false;

        var unavailable = await fixture.Reconciler.ReconcileAsync(
            update,
            fixture.Furniture(2, "furniture:indoor:6000", 2));

        Assert.Equal(LocalObjectReconcileStatus.Unavailable, unavailable.Status);
        Assert.True(fixture.Ledger.TryGet(update, out var retained));
        Assert.Equal(applied.Handle, retained.Handle);
        Assert.False(fixture.Ledger.IsApplied(update));
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(1, fixture.Runtime.ApplyCalls);

        fixture.Runtime.DuringApply = null;
        fixture.Readiness.IsReady = true;
        var retried = await fixture.Reconciler.ReconcileAsync(
            update,
            fixture.Furniture(2, "furniture:indoor:6000", 2));

        Assert.True(retried.Succeeded);
        Assert.Equal(applied.Handle, retried.Handle);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(2, fixture.Runtime.ApplyCalls);
        Assert.True(fixture.Ledger.IsApplied(update));
    }

    [Fact]
    public async Task CapacityFailureDoesNotDispatchOrAllocate()
    {
        var session = new ReceiverSessionKey(1, 1, 1);
        var occupiedId = new RscObjectId(Guid.NewGuid());
        var targetId = new RscObjectId(Guid.NewGuid());
        var ledger = new LocalObjectOwnershipLedger(1);
        Assert.True(ledger.TryReserve(
            new LocalObjectApplyToken(session, occupiedId, new ApplyGeneration(1)),
            new LocalObjectSpawnIdentity(RscObjectKind.Furniture, "furniture:indoor:6000")).Accepted);
        var token = new LocalObjectApplyToken(session, targetId, new ApplyGeneration(1));
        var fence = new TestFence();
        fence.Advance(token);
        var dispatcher = new TestDispatcher();
        var runtime = new TestRuntime();
        var reconciler = new LocalObjectReconciler(
            fence,
            ledger,
            dispatcher,
            runtime,
            new TestReadiness());

        var result = await reconciler.ReconcileAsync(
            token,
            Furniture(targetId, 1, "furniture:indoor:6002", 1));

        Assert.Equal(LocalObjectReconcileStatus.CapacityExceeded, result.Status);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Empty(runtime.Events);
    }

    private sealed class Fixture
    {
        private readonly ReceiverSessionKey session = new(1, 1, 1);

        public Fixture()
        {
            ObjectId = new RscObjectId(Guid.NewGuid());
            Ledger = new LocalObjectOwnershipLedger(4);
            Fence = new TestFence();
            Dispatcher = new TestDispatcher();
            Runtime = new TestRuntime();
            Readiness = new TestReadiness();
            Reconciler = new LocalObjectReconciler(
                Fence,
                Ledger,
                Dispatcher,
                Runtime,
                Readiness);
        }

        public RscObjectId ObjectId { get; }
        public LocalObjectOwnershipLedger Ledger { get; }
        public TestFence Fence { get; }
        public TestDispatcher Dispatcher { get; }
        public TestRuntime Runtime { get; }
        public TestReadiness Readiness { get; }
        public LocalObjectReconciler Reconciler { get; }

        public LocalObjectApplyToken Token(long generation) =>
            new(session, ObjectId, new ApplyGeneration(generation));

        public FurnitureState Furniture(
            long revision,
            string identity,
            float x) =>
            LocalObjectReconcilerTests.Furniture(ObjectId, revision, identity, x);
    }

    private sealed class TestReadiness : IReceiverReadinessPort
    {
        public bool IsReady { get; set; } = true;
    }

    private sealed class TestFence : ILocalObjectDesiredFence
    {
        private readonly object gate = new();
        private LocalObjectApplyToken current;

        public void Advance(LocalObjectApplyToken token)
        {
            lock (gate)
                current = token;
        }

        public bool IsCurrent(LocalObjectApplyToken token)
        {
            lock (gate)
                return current == token;
        }

        public bool TryExecuteCurrent<T>(
            LocalObjectApplyToken token,
            Func<T> operation,
            out T result)
        {
            lock (gate)
            {
                if (current != token)
                {
                    result = default!;
                    return false;
                }

                result = operation();
                return true;
            }
        }
    }

    private sealed class TestDispatcher : IGameThreadDispatcher
    {
        public int Calls { get; private set; }
        public Action BeforeInvoke { get; set; }

        public Task<T> InvokeAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var before = BeforeInvoke;
            BeforeInvoke = null;
            before?.Invoke();
            return Task.FromResult(operation());
        }
    }

    private sealed class TestRuntime : ILocalObjectRuntimePort
    {
        public List<string> Events { get; } = new();
        public int SpawnCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public ApplyPortStatus SpawnStatus { get; set; } = ApplyPortStatus.Succeeded;
        public ApplyPortStatus ApplyStatus { get; set; } = ApplyPortStatus.Succeeded;
        public ApplyPortStatus ReleaseStatus { get; set; } = ApplyPortStatus.Succeeded;
        public Exception ReleaseException { get; set; }
        public LocalObjectInstanceHandle FailedSpawnHandle { get; set; }
        public Func<LocalObjectSpawnRequest, LocalObjectInstanceHandle> SpawnFactory { get; set; }
        public Action<LocalObjectSpawnRequest> DuringSpawn { get; set; }
        public Action<LocalObjectInstanceHandle> DuringApply { get; set; }
        public Action<LocalObjectInstanceHandle> DuringRelease { get; set; }

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request)
        {
            SpawnCalls++;
            Events.Add($"spawn:{request.Identity.Value}:{request.SpawnGeneration}");
            DuringSpawn?.Invoke(request);
            if (SpawnStatus != ApplyPortStatus.Succeeded)
                return new(SpawnStatus, FailedSpawnHandle, "spawn failed");

            var handle = SpawnFactory?.Invoke(request) ?? new LocalObjectInstanceHandle(
                Guid.NewGuid(),
                request.Token.ObjectId,
                request.Identity.Kind,
                request.SpawnGeneration);
            return new(ApplyPortStatus.Succeeded, handle);
        }

        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState)
        {
            ApplyCalls++;
            Events.Add($"apply:{handle.SpawnGeneration}");
            DuringApply?.Invoke(handle);
            return new(ApplyStatus, ApplyStatus == ApplyPortStatus.Succeeded ? null : "apply failed");
        }

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
        {
            ReleaseCalls++;
            Events.Add($"release:{handle.SpawnGeneration}");
            DuringRelease?.Invoke(handle);
            if (ReleaseException is not null)
                throw ReleaseException;

            return new(
                ReleaseStatus,
                ReleaseStatus == ApplyPortStatus.Succeeded ? null : "release failed");
        }
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message =>
            throw new InvalidOperationException("Diagnostic accessor failed.");
    }

    private static FurnitureState Furniture(
        RscObjectId objectId,
        long revision,
        string identity,
        float x) => new(
            objectId,
            new ObjectRevision(revision),
            new RscUserId("creator"),
            null,
            identity,
            new Vector3State(x, 2, 3),
            new RotationState(0, 0, 0, 1));
}
