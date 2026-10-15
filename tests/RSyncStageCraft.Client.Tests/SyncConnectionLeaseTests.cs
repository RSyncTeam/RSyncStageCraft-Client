using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class SyncConnectionLeaseTests
{
    [Fact]
    public void GuardedMutationCallbackIsAssemblyOwnedInsteadOfPublicExtensionPoint()
    {
        var methods = typeof(SyncConnectionLock).GetMethods(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public);

        Assert.DoesNotContain(methods, method => method.Name == "TryExecute");
        var assemblyMethod = typeof(SyncConnectionLock)
            .GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "TryExecute");
        Assert.True(assemblyMethod.IsAssembly);
    }

    [Fact]
    public void NewConnectionGetsValidLease()
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();

        Assert.True(lease.IsValid);
        Assert.True(connection.IsCurrent(lease));
    }

    [Fact]
    public void RepeatedAcquireWhileConnectedReusesCurrentLease()
    {
        var connection = new SyncConnectionLock();
        var first = connection.Acquire();
        var second = connection.Acquire();

        Assert.Equal(first, second);
        Assert.True(connection.IsCurrent(first));
    }

    [Fact]
    public void CreateOnlyAcquisitionCannotBorrowExistingLease()
    {
        var connection = new SyncConnectionLock();
        var existing = connection.Acquire();

        Assert.False(connection.TryAcquireNew(
            out var rejected,
            out var cancellationPending,
            out var connectionInUse));

        Assert.False(rejected.IsValid);
        Assert.False(cancellationPending);
        Assert.True(connectionInUse);
        Assert.True(connection.IsCurrent(existing));
    }

    [Fact]
    public void CreateOnlyAcquisitionDistinguishesEpochExhaustionFromLiveConflict()
    {
        var connection = new SyncConnectionLock(long.MaxValue);

        Assert.False(connection.TryAcquireNew(
            out var rejected,
            out var cancellationPending,
            out var connectionInUse));

        Assert.False(rejected.IsValid);
        Assert.False(cancellationPending);
        Assert.False(connectionInUse);
    }

    [Fact]
    public void SameEpochFromAnotherLockCannotAuthorizeConnectionOperations()
    {
        var connection = new SyncConnectionLock();
        var foreignConnection = new SyncConnectionLock();
        var lease = connection.Acquire();
        var foreignLease = foreignConnection.Acquire();

        Assert.Equal(lease.Epoch, foreignLease.Epoch);
        Assert.NotEqual(lease, foreignLease);
        Assert.False(connection.IsCurrent(foreignLease));
        Assert.False(connection.TryGetCancellationToken(foreignLease, out _));
        Assert.False(connection.TryExecute(foreignLease, () => true, out _));
        Assert.False(connection.Invalidate(foreignLease));
        Assert.True(connection.IsCurrent(lease));
    }

    [Fact]
    public void NumericEpochCannotForgeCurrentConnectionAuthority()
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();
        var forged = new SyncConnectionLease(lease.Epoch);

        Assert.True(forged.IsValid);
        Assert.NotEqual(lease, forged);
        Assert.False(connection.IsCurrent(forged));
        Assert.False(connection.TryExecute(forged, () => true, out _));
        Assert.False(connection.Invalidate(forged));
        Assert.True(connection.IsCurrent(lease));
    }

    [Fact]
    public void SameEpochCannotForgeInvalidatedCleanupAuthority()
    {
        var connection = new SyncConnectionLock();
        var foreignConnection = new SyncConnectionLock();
        var lease = connection.Acquire();
        var foreignLease = foreignConnection.Acquire();
        Assert.True(connection.Invalidate(lease));

        var invoked = false;
        Assert.False(connection.TryExecuteInvalidated(
            foreignLease,
            () => invoked = true));
        Assert.False(connection.TryExecuteInvalidated(
            new SyncConnectionLease(lease.Epoch),
            () => invoked = true));
        Assert.False(invoked);
        Assert.True(connection.TryExecuteInvalidated(lease, () => invoked = true));
        Assert.True(invoked);
    }

    [Fact]
    public void InvalidatingConnectionRejectsOldLease()
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();
        connection.Invalidate();

        Assert.False(connection.IsCurrent(lease));
    }

    [Fact]
    public void InvalidationCancelsOnlyTheMatchingConnectionEpochToken()
    {
        var connection = new SyncConnectionLock();
        var oldLease = connection.Acquire();
        Assert.True(connection.TryGetCancellationToken(oldLease, out var oldToken));
        Assert.False(oldToken.IsCancellationRequested);

        Assert.True(connection.Invalidate(oldLease));
        Assert.True(oldToken.IsCancellationRequested);
        var currentLease = connection.Acquire();
        Assert.True(connection.TryGetCancellationToken(currentLease, out var currentToken));

        Assert.False(connection.Invalidate(oldLease));
        Assert.False(currentToken.IsCancellationRequested);
        Assert.True(connection.IsCurrent(currentLease));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockingCancellationRevokesAuthorityWithoutHoldingInvalidation(
        bool exactLease)
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();
        Assert.True(connection.TryGetCancellationToken(lease, out var token));
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<(bool Current, bool Acquired, bool Pending)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() =>
        {
            var acquired = connection.TryAcquire(out _, out var pending);
            entered.TrySetResult((connection.IsCurrent(lease), acquired, pending));
            release.Wait();
        });

        var invalidation = Task.Run(() =>
        {
            if (exactLease)
                return connection.Invalidate(lease);

            connection.Invalidate();
            return true;
        });
        try
        {
            var observed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(observed.Current);
            Assert.False(observed.Acquired);
            Assert.True(observed.Pending);
            Assert.True(await invalidation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(token.IsCancellationRequested);
            Assert.NotNull(token.WaitHandle);
            Assert.False(connection.CurrentLease.IsValid);
            Assert.False(connection.TryGetCancellationToken(lease, out _));
            Assert.False(connection.TryExecute(lease, () => true, out _));
            Assert.True(connection.HasPendingCancellation(lease));
            Assert.False(connection.HasPendingCancellation(new(lease.Epoch)));
            Assert.False(connection.HasPendingCancellation(new(lease.Epoch + 1)));
            Assert.Equal(1, connection.PendingCancellationCallbacks);
            Assert.False(connection.Invalidate(lease));
            connection.Invalidate();
            Assert.False(connection.TryAcquire(out _, out var pending));
            Assert.True(pending);
            Assert.False(connection.TryAcquireNew(
                out _,
                out var createPending,
                out var connectionInUse));
            Assert.True(createPending);
            Assert.False(connectionInUse);
            Assert.Throws<InvalidOperationException>(() => connection.Acquire());
        }
        finally
        {
            release.Set();
            await invalidation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(SpinWait.SpinUntil(
            () => connection.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
        Assert.False(connection.HasPendingCancellation(lease));
        Assert.Equal(0, connection.CancellationCallbackFailures);
        var current = connection.Acquire();
        Assert.Equal(lease.Epoch + 1, current.Epoch);
        Assert.False(connection.Invalidate(lease));
        Assert.True(connection.IsCurrent(current));
    }

    [Fact]
    public void ThrowingCancellationIsObservedAndDisposesOnlyTheRetiredSource()
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();
        Assert.True(connection.TryGetCancellationToken(lease, out var token));
        using var registration = token.Register(() =>
            throw new InvalidOperationException("Injected cancellation callback failure."));

        Assert.True(connection.Invalidate(lease));
        Assert.True(SpinWait.SpinUntil(
            () => connection.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(1, connection.CancellationCallbackFailures);
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);

        var current = connection.Acquire();
        Assert.True(connection.TryGetCancellationToken(current, out var currentToken));
        Assert.False(connection.Invalidate(lease));
        Assert.False(currentToken.IsCancellationRequested);
        Assert.NotNull(currentToken.WaitHandle);
        Assert.Equal(1, connection.CancellationCallbackFailures);
    }

    [Fact]
    public void ReconnectAdvancesEpochExactlyOnce()
    {
        var connection = new SyncConnectionLock();
        var oldLease = connection.Acquire();
        connection.Invalidate();
        var newLease = connection.Acquire();

        Assert.Equal(oldLease.Epoch + 1, newLease.Epoch);
        Assert.False(connection.IsCurrent(oldLease));
        Assert.True(connection.IsCurrent(newLease));
    }

    [Fact]
    public void LeaseScopedInvalidationCannotDisconnectNewerEpoch()
    {
        var connection = new SyncConnectionLock();
        var oldLease = connection.Acquire();
        Assert.True(connection.Invalidate(oldLease));
        var newLease = connection.Acquire();

        Assert.False(connection.Invalidate(oldLease));

        Assert.True(connection.IsCurrent(newLease));
    }

    [Fact]
    public void RepeatedInvalidationDoesNotAdvanceFutureEpoch()
    {
        var connection = new SyncConnectionLock();
        var oldLease = connection.Acquire();
        connection.Invalidate();
        connection.Invalidate();
        var newLease = connection.Acquire();

        Assert.Equal(oldLease.Epoch + 1, newLease.Epoch);
    }

    [Fact]
    public void ExhaustedEpochSpaceCannotWrapOrCreateAnotherLease()
    {
        var connection = new SyncConnectionLock(long.MaxValue - 1);
        Assert.True(connection.TryAcquire(out var finalLease));
        Assert.Equal(long.MaxValue, finalLease.Epoch);
        Assert.True(connection.Invalidate(finalLease));

        Assert.False(connection.TryAcquire(out var exhausted));
        Assert.False(exhausted.IsValid);
        Assert.False(connection.CurrentLease.IsValid);
        Assert.Throws<InvalidOperationException>(() => connection.Acquire());
    }

    [Fact]
    public void StaleOperationCannotExecute()
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();
        connection.Invalidate();
        var invoked = false;

        var executed = connection.TryExecute(lease, () =>
        {
            invoked = true;
            return 42;
        }, out var result);

        Assert.False(executed);
        Assert.False(invoked);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task InvalidatedCleanupBoundarySerializesReacquisition()
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();
        Assert.True(connection.Invalidate(lease));
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquireStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(() => connection.TryExecuteInvalidated(lease, () =>
        {
            entered.TrySetResult();
            release.Wait();
        }));
        Task<SyncConnectionLease> acquire = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            acquire = Task.Run(() =>
            {
                acquireStarted.TrySetResult();
                return connection.Acquire();
            });
            await acquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
            Assert.False(acquire.IsCompleted);
        }
        finally
        {
            release.Set();
            Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(5)));
            if (acquire is not null)
                await acquire.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(lease.Epoch + 1, (await acquire).Epoch);
        Assert.False(connection.TryExecuteInvalidated(lease, () => Assert.Fail("Stale cleanup ran.")));
    }

    [Fact]
    public void InvalidatedCleanupFailureReleasesBoundaryForExactRetry()
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();
        Assert.True(connection.Invalidate(lease));

        Assert.Throws<InvalidOperationException>(() => connection.TryExecuteInvalidated(
            lease, () => throw new InvalidOperationException("Injected cleanup failure.")));

        var invoked = false;
        Assert.True(connection.TryExecuteInvalidated(lease, () => invoked = true));
        Assert.True(invoked);
        Assert.Equal(lease.Epoch + 1, connection.Acquire().Epoch);
    }

    [Fact]
    public void CurrentOperationExecutes()
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();

        Assert.True(connection.TryExecute(lease, () => 42, out var result));
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task InvalidationWaitsForCurrentGuardedOperation()
    {
        var connection = new SyncConnectionLock();
        var lease = connection.Acquire();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var invalidationStarted = new ManualResetEventSlim(false);

        var operation = Task.Run(() => connection.TryExecute(
            lease,
            () =>
            {
                entered.Set();
                release.Wait();
                return 42;
            },
            out _));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var invalidation = Task.Run(() =>
        {
            invalidationStarted.Set();
            connection.Invalidate();
        });

        Assert.True(invalidationStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(invalidation.IsCompleted);

        release.Set();
        Assert.True(await operation);
        await invalidation;
        Assert.False(connection.IsCurrent(lease));
    }
}
