#nullable enable

using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class ClientControlRequestRegistryTests
{
    [Fact]
    public void PendingCapacityAndLifetimeBudgetAreIndependent()
    {
        var binding = Binding();
        var registry = new ClientControlRequestRegistry(binding, new(1, 2));
        var first = Registered(registry);
        var secondId = Guid.NewGuid();
        Assert.Equal(ClientControlRequestRegistrationStatus.PendingCapacityExceeded,
            registry.Register(secondId, ClientControlRequestKind.Snapshot, null).Status);
        Assert.Equal(new ClientControlRequestSnapshot(1, 1, false), registry.Snapshot);
        Assert.True(registry.Complete(binding, first));
        var second = registry.Register(secondId, ClientControlRequestKind.Snapshot, null).Receipt!;
        Assert.True(registry.Abandon(second));
        Assert.Equal(ClientControlRequestRegistrationStatus.RequestBudgetExhausted,
            registry.Register(Guid.NewGuid(), ClientControlRequestKind.Snapshot, null).Status);
        Assert.Equal(new ClientControlRequestSnapshot(0, 2, false), registry.Snapshot);
    }

    [Fact]
    public void ExactCompletionAndAbandonmentNeverPermitIdReuse()
    {
        var binding = Binding();
        var registry = new ClientControlRequestRegistry(binding, new(2, 4));
        var first = Registered(registry);
        Assert.True(registry.Complete(binding, first));
        Assert.False(registry.Complete(binding, first));
        Assert.Equal(ClientControlRequestRegistrationStatus.DuplicateRequest,
            registry.Register(first.RequestId, ClientControlRequestKind.ReadScene,
                new RscSceneId(Guid.NewGuid())).Status);
        var second = Registered(registry);
        Assert.True(registry.Abandon(second));
        Assert.False(registry.Abandon(second));
        Assert.Equal(ClientControlRequestRegistrationStatus.DuplicateRequest,
            registry.Register(second.RequestId, second.Kind, null).Status);
    }

    [Fact]
    public void EquivalentForeignBindingAndForgedReceiptCannotCompleteOwnership()
    {
        var binding = Binding();
        var registry = new ClientControlRequestRegistry(binding, new(2, 4));
        var receipt = Registered(registry);
        var foreign = Binding();
        Assert.Equal(binding.LocalSession, foreign.LocalSession);
        Assert.Equal(binding.Remote, foreign.Remote);
        Assert.False(registry.TryFind(foreign, receipt.RequestId, out _));
        Assert.False(registry.Complete(foreign, receipt));
        var forged = new ClientControlRequestReceipt(receipt.RequestId, receipt.Kind,
            receipt.SceneId, binding, receipt.Generation);
        Assert.False(registry.Complete(binding, forged));
        Assert.False(registry.Abandon(forged));
        Assert.True(registry.TryFind(binding, receipt.RequestId, out var retained));
        Assert.Same(receipt, retained);
        Assert.Equal(1, registry.Snapshot.PendingRequests);
    }

    [Fact]
    public void RetiredRegistryDrainsAndCannotMutateReplacementWithSameWireCoordinates()
    {
        var oldBinding = Binding();
        var old = new ClientControlRequestRegistry(oldBinding, new(2, 4));
        var oldReceipt = Registered(old);
        old.Retire();
        old.Retire();
        Assert.Equal(new ClientControlRequestSnapshot(0, 0, true), old.Snapshot);
        Assert.False(old.TryFind(oldBinding, oldReceipt.RequestId, out _));
        Assert.False(old.Complete(oldBinding, oldReceipt));
        Assert.Equal(ClientControlRequestRegistrationStatus.InactiveConnection,
            old.Register(Guid.NewGuid(), ClientControlRequestKind.Snapshot, null).Status);
        var freshBinding = Binding();
        var fresh = new ClientControlRequestRegistry(freshBinding, new(2, 4));
        var replacement = fresh.Register(oldReceipt.RequestId,
            ClientControlRequestKind.Snapshot, null).Receipt!;
        Assert.Equal(oldReceipt.Generation, replacement.Generation);
        Assert.False(fresh.Abandon(oldReceipt));
        Assert.False(fresh.Complete(oldBinding, replacement));
        Assert.True(fresh.Complete(freshBinding, replacement));
    }

    [Fact]
    public async Task ConcurrentDuplicateRegistrationOwnsExactlyOneReceipt()
    {
        var registry = new ClientControlRequestRegistry(Binding(), new(4, 8));
        var id = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            registry.Register(id, ClientControlRequestKind.Snapshot, null))));
        Assert.Single(results, result => result.Status ==
            ClientControlRequestRegistrationStatus.Registered);
        Assert.Equal(15, results.Count(result => result.Status ==
            ClientControlRequestRegistrationStatus.DuplicateRequest));
        Assert.Equal(new ClientControlRequestSnapshot(1, 1, false), registry.Snapshot);
    }

    [Theory]
    [InlineData(ClientControlRequestKind.SaveScene)]
    [InlineData(ClientControlRequestKind.ReadScene)]
    public void SceneRequestsRequireAndRetainExactSceneIdentity(ClientControlRequestKind kind)
    {
        var registry = new ClientControlRequestRegistry(Binding(), new(2, 4));
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest,
            registry.Register(Guid.NewGuid(), kind, null).Status);
        var scene = new RscSceneId(Guid.NewGuid());
        var receipt = registry.Register(Guid.NewGuid(), kind, scene).Receipt!;
        Assert.Equal(scene, receipt.SceneId);
        Assert.Equal(kind, receipt.Kind);
    }

    [Fact]
    public void InvalidRequestsConsumeNeitherCapacityNorIdBudget()
    {
        var registry = new ClientControlRequestRegistry(Binding(), new(1, 1));
        var id = Guid.NewGuid();
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest,
            registry.Register(Guid.Empty, ClientControlRequestKind.Snapshot, null).Status);
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest,
            registry.Register(id, (ClientControlRequestKind)999, null).Status);
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest,
            registry.Register(id, ClientControlRequestKind.Snapshot,
                new RscSceneId(Guid.NewGuid())).Status);
        Assert.Equal(new ClientControlRequestSnapshot(0, 0, false), registry.Snapshot);
        Assert.Equal(ClientControlRequestRegistrationStatus.Registered,
            registry.Register(id, ClientControlRequestKind.Snapshot, null).Status);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(257, 1000)]
    [InlineData(2, 1)]
    [InlineData(1, 16385)]
    public void InvalidLimitsAreRefused(int pending, int retained)
    {
        var limits = new ClientControlRequestLimits(pending, retained);
        Assert.False(limits.IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClientControlRequestRegistry(Binding(), limits));
    }

    private static ClientControlRequestReceipt Registered(ClientControlRequestRegistry registry)
    {
        var result = registry.Register(Guid.NewGuid(), ClientControlRequestKind.Snapshot, null);
        Assert.Equal(ClientControlRequestRegistrationStatus.Registered, result.Status);
        return Assert.IsType<ClientControlRequestReceipt>(result.Receipt);
    }

    private static ReceiverControlBinding Binding() =>
        new(new(1, 2, 3), new(1, 2, 7), new SyncConnectionLease(3));
}
