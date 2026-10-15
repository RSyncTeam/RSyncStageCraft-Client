using RSyncStageCraft.Adapters;

namespace RSyncStageCraft.Tests;

public sealed class DalamudGameThreadDispatcherTests
{
    [Fact]
    public async Task OperationRunsOnlyInsideScheduledFrameworkCallback()
    {
        var scheduler = new Scheduler();
        var dispatcher = new FrameworkGameThreadDispatcherCore(scheduler);
        var invoked = false;

        var pending = dispatcher.InvokeAsync(
            () =>
            {
                Assert.True(scheduler.InCallback);
                invoked = true;
                return 42;
            },
            CancellationToken.None);

        Assert.False(invoked);
        Assert.False(pending.IsCompleted);
        scheduler.Execute();
        Assert.Equal(42, await pending);
        Assert.True(invoked);
        Assert.Equal(1, scheduler.ScheduleCalls);
    }

    [Fact]
    public async Task CancellationBeforeSchedulingNeverQueuesOperation()
    {
        var scheduler = new Scheduler();
        var dispatcher = new FrameworkGameThreadDispatcherCore(scheduler);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var pending = dispatcher.InvokeAsync(() => 1, cancelled.Token);

        await Assert.ThrowsAsync<TaskCanceledException>(() => pending);
        Assert.Equal(0, scheduler.ScheduleCalls);
    }

    [Fact]
    public async Task CancellationAfterSchedulingIsObservedAtCallbackBoundary()
    {
        var scheduler = new Scheduler();
        var dispatcher = new FrameworkGameThreadDispatcherCore(scheduler);
        using var cancelled = new CancellationTokenSource();
        var invoked = false;
        var pending = dispatcher.InvokeAsync(
            () =>
            {
                invoked = true;
                return 1;
            },
            cancelled.Token);

        cancelled.Cancel();
        scheduler.Execute();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(invoked);
    }

    [Fact]
    public async Task CallbackAndSchedulerExceptionsRemainObservable()
    {
        var callbackScheduler = new Scheduler();
        var callbackDispatcher = new FrameworkGameThreadDispatcherCore(callbackScheduler);
        var callback = callbackDispatcher.InvokeAsync<int>(
            () => throw new InvalidOperationException("callback failed"),
            CancellationToken.None);
        callbackScheduler.Execute();
        var callbackError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => callback);
        Assert.Equal("callback failed", callbackError.Message);

        var scheduler = new Scheduler { ScheduleError = new("schedule failed") };
        var dispatcher = new FrameworkGameThreadDispatcherCore(scheduler);
        var scheduled = dispatcher.InvokeAsync(() => 1, CancellationToken.None);
        var scheduleError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduled);
        Assert.Equal("schedule failed", scheduleError.Message);
    }

    private sealed class Scheduler : IFrameworkThreadScheduler
    {
        private Action pending;

        public bool InCallback { get; private set; }
        public int ScheduleCalls { get; private set; }
        public InvalidOperationException ScheduleError { get; set; }

        public Task<T> RunOnFrameworkThread<T>(Func<T> operation)
        {
            ScheduleCalls++;
            if (ScheduleError is not null)
                throw ScheduleError;
            Assert.Null(pending);

            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending = () =>
            {
                InCallback = true;
                try
                {
                    completion.TrySetResult(operation());
                }
                catch (OperationCanceledException cancelled)
                {
                    completion.TrySetCanceled(cancelled.CancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    InCallback = false;
                    pending = null;
                }
            };
            return completion.Task;
        }

        public void Execute()
        {
            Assert.NotNull(pending);
            pending();
        }
    }
}
