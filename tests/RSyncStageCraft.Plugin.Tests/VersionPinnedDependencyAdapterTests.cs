using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Dependencies;

namespace RSyncStageCraft.Tests;

public sealed class VersionPinnedDependencyAdapterTests
{
    [Theory]
    [InlineData(5, 15, true)]
    [InlineData(5, 16, true)]
    [InlineData(5, 14, false)]
    [InlineData(6, 15, false)]
    public void PenumbraRequiresExactBreakingAndMinimumFeature(
        int breaking,
        int feature,
        bool compatible)
    {
        using var adapter = new PenumbraDependencyAdapter(
            () => (breaking, feature));

        Assert.True(adapter.IsAvailable);
        Assert.Equal(compatible, adapter.IsCompatible);
    }

    [Theory]
    [InlineData(1, 8, true)]
    [InlineData(1, 9, true)]
    [InlineData(1, 7, false)]
    [InlineData(2, 8, false)]
    public void GlamourerRequiresExactMajorAndMinimumMinor(
        int major,
        int minor,
        bool compatible)
    {
        using var adapter = new GlamourerDependencyAdapter(
            () => (major, minor));

        Assert.True(adapter.IsAvailable);
        Assert.Equal(compatible, adapter.IsCompatible);
    }

    [Fact]
    public void UnavailableGeneratedSubscriberFailsClosed()
    {
        using var penumbra = new PenumbraDependencyAdapter(
            () => throw new InvalidOperationException("Penumbra unavailable."));
        using var glamourer = new GlamourerDependencyAdapter(
            () => throw new InvalidOperationException("Glamourer unavailable."));

        Assert.False(penumbra.IsAvailable);
        Assert.False(penumbra.IsCompatible);
        Assert.False(glamourer.IsAvailable);
        Assert.False(glamourer.IsCompatible);
    }

    [Fact]
    public void DisposedAdaptersRemainUnavailableWithoutCallingSubscriber()
    {
        var calls = 0;
        var penumbra = new PenumbraDependencyAdapter(
            () =>
            {
                calls++;
                return (5, 15);
            });
        var glamourer = new GlamourerDependencyAdapter(
            () =>
            {
                calls++;
                return (1, 8);
            });

        penumbra.Dispose();
        glamourer.Dispose();

        Assert.False(penumbra.IsAvailable);
        Assert.False(penumbra.IsCompatible);
        Assert.False(glamourer.IsAvailable);
        Assert.False(glamourer.IsCompatible);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ObservationsDistinguishInstalledProviderAndContractVersions()
    {
        using var penumbra = new PenumbraDependencyAdapter(
            () => (5, 15),
            () => "1.7.2.1",
            "5.15.1.0");
        using var glamourer = new GlamourerDependencyAdapter(
            () => (1, 8),
            () => "1.7.1.3",
            "2.8.2.0");

        var penumbraObservation =
            ((IDependencyObservationAdapter)penumbra).Observe();
        var glamourerObservation =
            ((IDependencyObservationAdapter)glamourer).Observe();

        Assert.Equal("1.7.2.1", penumbraObservation.InstalledVersion);
        Assert.Equal("5.15", penumbraObservation.ProviderApiVersion);
        Assert.Equal("5.15.1.0", penumbraObservation.ContractAssemblyVersion);
        Assert.True(penumbraObservation.Compatible);
        Assert.Equal("1.7.1.3", glamourerObservation.InstalledVersion);
        Assert.Equal("1.8", glamourerObservation.ProviderApiVersion);
        Assert.Equal("2.8.2.0", glamourerObservation.ContractAssemblyVersion);
        Assert.True(glamourerObservation.Compatible);
    }
}
