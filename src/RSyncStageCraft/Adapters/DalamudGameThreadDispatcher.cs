using Dalamud.Plugin.Services;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Adapters;

internal interface IFrameworkThreadScheduler
{
    Task<T> RunOnFrameworkThread<T>(Func<T> operation);
}

internal sealed class DalamudFrameworkThreadScheduler(IFramework framework) :
    IFrameworkThreadScheduler
{
    private readonly IFramework framework = framework ??
        throw new ArgumentNullException(nameof(framework));

    public Task<T> RunOnFrameworkThread<T>(Func<T> operation) =>
        framework.RunOnFrameworkThread(operation);
}

/// <summary>
/// Current Dalamud API 15 framework-thread adapter. The operation itself is
/// synchronous and contains no await, matching IFramework's documented
/// deadlock constraint for callbacks scheduled through RunOnFrameworkThread.
/// </summary>
public sealed class DalamudGameThreadDispatcher : IGameThreadDispatcher
{
    private readonly FrameworkGameThreadDispatcherCore core;

    public DalamudGameThreadDispatcher(IFramework framework)
    {
        core = new(new DalamudFrameworkThreadScheduler(framework));
    }

    public Task<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken) =>
        core.InvokeAsync(operation, cancellationToken);
}

internal sealed class FrameworkGameThreadDispatcherCore : IGameThreadDispatcher
{
    private readonly IFrameworkThreadScheduler scheduler;

    internal FrameworkGameThreadDispatcherCore(IFrameworkThreadScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        this.scheduler = scheduler;
    }

    public Task<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        try
        {
            return scheduler.RunOnFrameworkThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            });
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }
}
