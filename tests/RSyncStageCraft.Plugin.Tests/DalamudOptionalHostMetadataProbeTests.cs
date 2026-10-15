using RSyncStageCraft.Adapters;

namespace RSyncStageCraft.Tests;

public sealed class DalamudOptionalHostMetadataProbeTests
{
    [Fact]
    public void BoundedInventoryReportsOnlyKnownHostsWithoutCompatibilityClaims()
    {
        var snapshot = DalamudOptionalHostMetadataProbe.ObserveCandidates(
        [
            new("Stagehand", "0.5.1"),
            new("HDM", "1.0.3.0"),
            new("CustomizePlus", "2.2.1.3"),
        ]);

        var customizePlus = Assert.Single(snapshot);
        Assert.Equal("Customize+", customizePlus.DisplayName);
        Assert.Equal("CustomizePlus", customizePlus.InternalName);
        Assert.True(customizePlus.InventoryObserved);
        Assert.True(customizePlus.Installed);
        Assert.Equal("2.2.1.3", customizePlus.InstalledVersion);
    }

    [Fact]
    public void OversizedInventoryFailsClosedInsteadOfPublishingPartialState()
    {
        var candidates = Enumerable.Range(
                0,
                DalamudOptionalHostMetadataProbe.MaximumInstalledPlugins + 1)
            .Select(index => new InstalledPluginCandidate(
                index == 0 ? "CustomizePlus" : $"Other{index}",
                "2.2.1.3"));

        var snapshot = DalamudOptionalHostMetadataProbe.ObserveCandidates(
            candidates);

        Assert.All(
            snapshot,
            host =>
            {
                Assert.False(host.InventoryObserved);
                Assert.False(host.Installed);
                Assert.Null(host.InstalledVersion);
            });
    }

    [Fact]
    public void UnreadableInventoryFailsClosed()
    {
        var snapshot = DalamudOptionalHostMetadataProbe.ObserveCandidates(
            new ThrowingCandidates());

        Assert.All(snapshot, host => Assert.False(host.InventoryObserved));
    }

    private sealed class ThrowingCandidates :
        IEnumerable<InstalledPluginCandidate>
    {
        public IEnumerator<InstalledPluginCandidate> GetEnumerator() =>
            throw new InvalidOperationException("inventory unavailable");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
