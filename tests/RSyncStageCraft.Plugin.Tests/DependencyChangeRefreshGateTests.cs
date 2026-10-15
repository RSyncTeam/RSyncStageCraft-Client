using RSyncStageCraft.Core.Diagnostics;
using RSyncStageCraft.Ui;

namespace RSyncStageCraft.Tests;

public sealed class DependencyChangeRefreshGateTests
{
    [Fact]
    public void RequiredDependencyChangeRequestsOneDeferredRefresh()
    {
        var observations = new RuntimeObservationTrace();
        var gate = new DependencyChangeRefreshGate(observations);

        gate.Observe("Unloaded", ["OtherPlugin", "penumbra"]);
        gate.Observe("Loaded", ["Glamourer"]);

        Assert.True(gate.TryConsume());
        Assert.False(gate.TryConsume());
        Assert.Collection(
            observations.Snapshot(),
            current =>
            {
                Assert.Equal("dependency.change-observed", current.Phase);
                Assert.Equal("Unloaded/penumbra", current.Detail);
            },
            current =>
            {
                Assert.Equal("dependency.change-observed", current.Phase);
                Assert.Equal("Loaded/Glamourer", current.Detail);
            });
    }

    [Fact]
    public void OptionalHostChangeRequestsOneDeferredMetadataRefresh()
    {
        var observations = new RuntimeObservationTrace();
        var gate = new DependencyChangeRefreshGate(observations);

        gate.Observe("Loaded", ["CustomizePlus"]);

        Assert.True(gate.TryConsume());
        Assert.False(gate.TryConsume());
        var observation = Assert.Single(observations.Snapshot());
        Assert.Equal("dependency.change-observed", observation.Phase);
        Assert.Equal("Loaded/CustomizePlus", observation.Detail);
    }

    [Theory]
    [InlineData("Stagehand")]
    [InlineData("HDM")]
    public void ReferenceOnlyPluginChangeDoesNotRequestRefresh(
        string internalName)
    {
        var observations = new RuntimeObservationTrace();
        var gate = new DependencyChangeRefreshGate(observations);

        gate.Observe("Loaded", [internalName]);

        Assert.False(gate.TryConsume());
        Assert.Empty(observations.Snapshot());
    }

    [Fact]
    public void UnrelatedBoundedChangeDoesNotRequestRefresh()
    {
        var observations = new RuntimeObservationTrace();
        var gate = new DependencyChangeRefreshGate(observations);

        gate.Observe("Loaded", ["OtherPlugin", "AnotherPlugin"]);
        gate.Observe(
            "Update",
            Enumerable.Range(
                    0,
                    DependencyChangeRefreshGate.MaximumAffectedNames)
                .Select(index => $"Other{index}"));

        Assert.False(gate.TryConsume());
        Assert.Empty(observations.Snapshot());
    }

    [Fact]
    public void TruncatedOrUnreadableAffectedListFailsClosedToRefresh()
    {
        var observations = new RuntimeObservationTrace();
        var gate = new DependencyChangeRefreshGate(observations);
        var names = Enumerable.Range(
                0,
                DependencyChangeRefreshGate.MaximumAffectedNames + 1)
            .Select(index => $"Other{index}");

        gate.Observe("Update", names);

        Assert.True(gate.TryConsume());
        Assert.Contains(
            observations.Snapshot(),
            current => current.Detail == "Update/affected-list-truncated");

        gate.Observe("Update", new ThrowingNames());

        Assert.True(gate.TryConsume());
        Assert.Contains(
            observations.Snapshot(),
            current => current.Detail == "Update/affected-list-unreadable");
    }

    [Fact]
    public void ChangeObservedInsideExternalCallIsMarkedReentrant()
    {
        var observations = new RuntimeObservationTrace();
        var gate = new DependencyChangeRefreshGate(observations);

        using (observations.BeginExternalCall("dependency.probe", "Penumbra"))
            gate.Observe("Unloaded", ["Penumbra"]);

        var change = Assert.Single(
            observations.Snapshot(),
            current => current.Phase == "dependency.change-observed");
        Assert.Equal(1, change.ExternalCallDepth);
        Assert.True(change.Reentrant);
    }

    private sealed class ThrowingNames : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator() =>
            throw new InvalidOperationException("enumeration failed");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
