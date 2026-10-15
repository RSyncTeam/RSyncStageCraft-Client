using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class UnloadAwareDispatcherTests
{
    [Fact]
    public async Task UsesTheGameThreadUntilUnloadThenRunsInline()
    {
        var inner = new QueueingDispatcher();
        var dispatcher = new UnloadAwareDispatcher(inner);

        var queued = dispatcher.InvokeAsync(() => 1, CancellationToken.None);
        Assert.False(queued.IsCompleted);
        Assert.Equal(1, inner.Queued);
        Assert.False(dispatcher.IsUnloading);

        dispatcher.RunInline();
        Assert.True(dispatcher.IsUnloading);
        Assert.Equal(2, await dispatcher.InvokeAsync(() => 2, CancellationToken.None));
        Assert.Equal(1, inner.Queued);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.InvokeAsync<int>(() => throw new InvalidOperationException(), CancellationToken.None));
        Assert.True(dispatcher.InvokeAsync(() => 3, new CancellationToken(canceled: true)).IsCanceled);
    }

    private sealed class QueueingDispatcher : IGameThreadDispatcher
    {
        internal int Queued { get; private set; }

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            Queued++;
            return new TaskCompletionSource<T>().Task;
        }
    }
}
