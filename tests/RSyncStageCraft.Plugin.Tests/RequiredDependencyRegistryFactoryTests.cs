using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Dependencies;

namespace RSyncStageCraft.Tests;

public sealed class RequiredDependencyRegistryFactoryTests
{
    [Fact]
    public void ExactPairIsOwnedWithoutProbingDuringBootstrap()
    {
        var penumbra = new FakeDependency("Penumbra");
        var glamourer = new FakeDependency("Glamourer");
        var registry = RequiredDependencyRegistryFactory.Create(
            () => penumbra,
            () => glamourer);

        Assert.Equal(2, registry.Adapters.Count);
        Assert.Equal(0, penumbra.ProbeCount);
        Assert.Equal(0, glamourer.ProbeCount);

        registry.Dispose();

        Assert.Equal(1, penumbra.DisposeCount);
        Assert.Equal(1, glamourer.DisposeCount);
    }

    [Fact]
    public void SecondFactoryFailureRollsBackFirstAdapter()
    {
        var penumbra = new FakeDependency("Penumbra");

        Assert.Throws<InvalidOperationException>(() =>
            RequiredDependencyRegistryFactory.Create(
                () => penumbra,
                () => throw new InvalidOperationException("Glamourer construction failed.")));

        Assert.Equal(1, penumbra.DisposeCount);
    }

    [Fact]
    public void RejectedSecondAdapterAndRegisteredFirstAdapterAreBothDisposed()
    {
        var penumbra = new FakeDependency("Penumbra");
        var wrongGlamourer = new FakeDependency("Transport");

        Assert.Throws<InvalidOperationException>(() =>
            RequiredDependencyRegistryFactory.Create(
                () => penumbra,
                () => wrongGlamourer));

        Assert.Equal(1, penumbra.DisposeCount);
        Assert.Equal(1, wrongGlamourer.DisposeCount);
    }

    private sealed class FakeDependency(string name) : IDependencyAdapter
    {
        public string Name { get; } = name;
        public int ProbeCount { get; private set; }
        public int DisposeCount { get; private set; }

        public bool IsAvailable
        {
            get
            {
                ProbeCount++;
                return true;
            }
        }

        public bool IsCompatible
        {
            get
            {
                ProbeCount++;
                return true;
            }
        }

        public void Dispose() => DisposeCount++;
    }
}
