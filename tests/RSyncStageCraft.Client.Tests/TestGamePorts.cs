using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

/// <summary>
/// An in-memory game for receiver tests: an inline game-thread dispatcher and
/// a local-object runtime that records the actors, furniture and lights it holds.
/// </summary>
internal sealed class TestGamePorts : IGameThreadDispatcher, ILocalObjectRuntimePort,
    ILocalObjectRuntimeOwnershipDiagnostics
{
    private static ApplyPortResult Success => new(ApplyPortStatus.Succeeded);

    public Dictionary<RscObjectId, AuthoritativeObjectState> LocalObjects { get; } = new();
    int ILocalObjectRuntimeOwnershipDiagnostics.PendingOwnershipCount => LocalObjects.Count;

    public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(operation());
    }

    public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(LocalObjectSpawnRequest request)
    {
        LocalObjects.Add(request.Token.ObjectId, request.DesiredState);
        return new(ApplyPortStatus.Succeeded, new(Guid.NewGuid(), request.Token.ObjectId, request.Identity.Kind, request.SpawnGeneration));
    }

    public ApplyPortResult ApplyOwned(LocalObjectInstanceHandle handle, AuthoritativeObjectState desiredState)
    {
        LocalObjects[handle.ObjectId] = desiredState;
        return Success;
    }

    public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
    {
        Assert.True(LocalObjects.Remove(handle.ObjectId));
        return Success;
    }
}
