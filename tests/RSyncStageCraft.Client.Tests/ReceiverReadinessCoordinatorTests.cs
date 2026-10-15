using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverReadinessCoordinatorTests
{
    [Fact]
    public void FourFlagSnapshotFailsClosedWithoutReceiptSafeHosts()
    {
        var legacyShape = new ReceiverDependencySnapshot(
            PenumbraReady: true,
            GlamourerReady: true,
            IpcReady: true,
            ProtocolCompatible: true);

        Assert.False(legacyShape.LocalObjectHostReady);
        Assert.False(legacyShape.IsReady);
    }

    [Fact]
    public void ShutdownRequestPermanentlyClosesAtomicAdmission()
    {
        var readiness = new ReceiverReadinessCoordinator();
        var ready = new ReceiverDependencySnapshot(true, true, true, true, true);
        readiness.Update(ready);
        var fence = (IReceiverReadinessAdmissionFence)readiness;

        Assert.True(fence.TryExecuteIfReady(() => 1, out var before));
        Assert.Equal(1, before);

        readiness.RequestShutdown();

        Assert.False(readiness.IsReady);
        Assert.False(fence.TryExecuteIfReady(() => 2, out _));
        readiness.Update(new(true, true, true, false, true));
        readiness.Update(ready);
        Assert.False(readiness.IsReady);
        Assert.False(fence.TryExecuteIfReady(() => 3, out _));
    }

    [Fact]
    public async Task ShutdownRequestCannotSplitAtomicAdmissionPublication()
    {
        var readiness = new ReceiverReadinessCoordinator();
        readiness.Update(new(true, true, true, true, true));
        var fence = (IReceiverReadinessAdmissionFence)readiness;
        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var admission = Task.Run(() => fence.TryExecuteIfReady(
            () =>
            {
                publicationEntered.TrySetResult();
                releasePublication.Task.GetAwaiter().GetResult();
                return true;
            },
            out var published) && published);
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdownStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = Task.Run(() =>
        {
            shutdownStarted.TrySetResult();
            readiness.RequestShutdown();
        });
        await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(shutdown.IsCompleted);

        releasePublication.TrySetResult();

        Assert.True(await admission.WaitAsync(TimeSpan.FromSeconds(2)));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(readiness.IsReady);
        Assert.False(fence.TryExecuteIfReady(() => true, out _));
    }

    [Fact]
    public void ConnectionAndApplyReadinessAreSeparateDecisions()
    {
        // Split readiness: the shared authority is
        // connection-level; the local-object view additionally needs its host.
        var readiness = new ReceiverReadinessCoordinator();
        Assert.False(readiness.IsReady);
        Assert.False(readiness.LocalObjects.IsReady);

        Assert.True(readiness.Update(new(
            PenumbraReady: true,
            GlamourerReady: true,
            IpcReady: true,
            ProtocolCompatible: false,
            LocalObjectHostReady: true)));
        Assert.False(readiness.IsReady);
        Assert.False(readiness.LocalObjects.IsReady);

        Assert.True(readiness.Update(new(
            PenumbraReady: true,
            GlamourerReady: true,
            IpcReady: true,
            ProtocolCompatible: true,
            LocalObjectHostReady: false)));
        Assert.True(readiness.IsReady);
        Assert.False(readiness.LocalObjects.IsReady);

        var ready = new ReceiverDependencySnapshot(true, true, true, true, true);
        Assert.True(readiness.Update(ready));
        Assert.True(readiness.IsReady);
        Assert.True(readiness.LocalObjects.IsReady);
        Assert.Equal(ready, readiness.Current);
        Assert.False(readiness.Update(ready));
    }

    [Fact]
    public async Task ReadinessUpdateCannotSplitFinalAdmissionPublication()
    {
        var readiness = new ReceiverReadinessCoordinator();
        readiness.Update(new(true, true, true, true, true));
        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var admission = Task.Run(() =>
        {
            var admitted = ((IReceiverReadinessAdmissionFence)readiness)
                .TryExecuteIfReady(
                    () =>
                    {
                        publicationEntered.TrySetResult();
                        releasePublication.Task.GetAwaiter().GetResult();
                        return true;
                    },
                    out var published);
            return (admitted, published);
        });
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dependencyLossStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencyLoss = Task.Run(() =>
        {
            dependencyLossStarted.TrySetResult();
            return readiness.Update(new(true, true, true, false, true));
        });
        await dependencyLossStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(dependencyLoss.IsCompleted);

        releasePublication.TrySetResult();

        Assert.Equal(
            (admitted: true, published: true),
            await admission.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await dependencyLoss.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(readiness.IsReady);
    }

    [Fact]
    public void LifecycleLossLatchSurvivesRecoveryUntilExactCompletion()
    {
        var readiness = new ReceiverReadinessCoordinator();
        var ready = new ReceiverDependencySnapshot(true, true, true, true, true);
        Assert.Null(readiness.UpdateForLifecycle(ready).PendingLoss);
        Assert.True(readiness.IsReady);

        var loss = Assert.IsType<ReceiverReadinessLossObservation>(
            readiness.UpdateForLifecycle(
                new(true, true, true, false, true)).PendingLoss);
        var recovery = readiness.UpdateForLifecycle(ready);

        Assert.Same(loss, recovery.PendingLoss);
        Assert.Equal(ready, readiness.Current);
        Assert.False(readiness.IsReady);
        Assert.True(readiness.CompleteLifecycleLoss(loss));
        Assert.True(readiness.IsReady);
        Assert.False(readiness.CompleteLifecycleLoss(loss));
    }

    [Fact]
    public void OlderLossCompletionCannotClearANewerObservation()
    {
        var readiness = new ReceiverReadinessCoordinator();
        readiness.UpdateForLifecycle(new(true, true, true, true, true));
        var first = Assert.IsType<ReceiverReadinessLossObservation>(
            readiness.UpdateForLifecycle(
                new(true, true, true, false, true)).PendingLoss);
        var second = Assert.IsType<ReceiverReadinessLossObservation>(
            readiness.UpdateForLifecycle(
                new(false, true, true, false, true)).PendingLoss);
        readiness.UpdateForLifecycle(new(true, true, true, true, true));

        Assert.NotSame(first, second);
        Assert.False(readiness.CompleteLifecycleLoss(first));
        Assert.False(readiness.IsReady);
        Assert.True(readiness.CompleteLifecycleLoss(second));
        Assert.True(readiness.IsReady);
    }
}
