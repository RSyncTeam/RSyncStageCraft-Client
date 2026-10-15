using RSyncStageCraft.Core.Dependencies;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Diagnostics;
using RSyncStageCraft.Ui;

namespace RSyncStageCraft.Tests;

public sealed class PluginStatusModelTests
{
    [Fact]
    public void ConstructionDoesNotProbeDependencies()
    {
        using var registry = new DependencyRegistry();
        var dependency = new RecordingDependency("Penumbra", true, true);
        registry.Register(dependency);

        var optionalProbeCount = 0;
        var status = new PluginStatusModel(
            registry,
            observations: null,
            () =>
            {
                optionalProbeCount++;
                return Array.Empty<OptionalHostMetadata>();
            });

        Assert.False(status.HasObservation);
        Assert.False(status.IsReady);
        Assert.Empty(status.Dependencies);
        Assert.Empty(status.OptionalHosts);
        Assert.Equal(0, dependency.ProbeCount);
        Assert.Equal(0, optionalProbeCount);
    }

    [Fact]
    public void ExplicitRefreshPublishesOptionalMetadataWithoutAffectingReadiness()
    {
        using var registry = new DependencyRegistry();
        registry.Register(new RecordingDependency("Penumbra", true, true));
        var optionalProbeCount = 0;
        var status = new PluginStatusModel(
            registry,
            observations: null,
            () =>
            {
                optionalProbeCount++;
                return
                [
                    new OptionalHostMetadata(
                        "Customize+",
                        "CustomizePlus",
                        InventoryObserved: true,
                        Installed: true,
                        InstalledVersion: "2.2.1.3"),
                ];
            });

        status.Refresh();

        Assert.True(status.IsReady);
        Assert.Equal(1, optionalProbeCount);
        var customizePlus = Assert.Single(status.OptionalHosts);
        Assert.Equal("Customize+", customizePlus.DisplayName);
        Assert.True(customizePlus.InventoryObserved);
        Assert.True(customizePlus.Installed);
        Assert.Equal("2.2.1.3", customizePlus.InstalledVersion);
    }

    [Fact]
    public void AFailureReasonGoesToTheLogOncePerChange()
    {
        // The plugin line does not show FailureReason; it is logged.
        using var registry = new DependencyRegistry();
        registry.Register(new RecordingDependency("Penumbra", true, true));
        registry.Register(new RecordingDependency("Glamourer", true, false));
        var logged = new List<string>();
        var status = new PluginStatusModel(registry, observations: null, optionalHostProbe: null, log: logged.Add);

        status.Refresh();
        status.Refresh();

        Assert.Equal<string>(["RSC plugin status: Glamourer: Dependency unavailable or incompatible."], logged);
    }

    [Fact]
    public void ExplicitRefreshPublishesFailClosedDependencySnapshot()
    {
        using var registry = new DependencyRegistry();
        var penumbra = new RecordingDependency("Penumbra", true, true);
        var glamourer = new RecordingDependency("Glamourer", true, false);
        registry.Register(penumbra);
        registry.Register(glamourer);
        var status = new PluginStatusModel(registry);

        status.Refresh();

        Assert.True(status.HasObservation);
        Assert.False(status.IsReady);
        Assert.Collection(
            status.Dependencies,
            current =>
            {
                Assert.Equal("Penumbra", current.Name);
                Assert.True(current.Available);
                Assert.True(current.Compatible);
            },
            current =>
            {
                Assert.Equal("Glamourer", current.Name);
                Assert.True(current.Available);
                Assert.False(current.Compatible);
                Assert.NotNull(current.FailureReason);
            });
        Assert.Equal(2, penumbra.ProbeCount);
        Assert.Equal(2, glamourer.ProbeCount);
    }

    [Fact]
    public void VersionedRefreshUsesOneProviderCallAndPublishesExactLabels()
    {
        using var registry = new DependencyRegistry();
        var calls = 0;
        registry.Register(new PenumbraDependencyAdapter(
            () =>
            {
                calls++;
                return (5, 15);
            },
            () => "1.7.2.1",
            "5.15.1.0"));
        var observations = new RuntimeObservationTrace();
        var status = new PluginStatusModel(registry, observations);

        status.Refresh();

        var dependency = Assert.Single(status.Dependencies);
        Assert.True(status.IsReady);
        Assert.Equal("1.7.2.1", dependency.InstalledVersion);
        Assert.Equal("5.15", dependency.ProviderApiVersion);
        Assert.Equal("5.15.1.0", dependency.ContractAssemblyVersion);
        Assert.Equal(1, calls);
        Assert.Collection(
            observations.Snapshot(),
            current =>
            {
                Assert.Equal("dependency.probe.begin", current.Phase);
                Assert.Equal(1, current.ExternalCallDepth);
            },
            current =>
            {
                Assert.Equal("dependency.probe.end", current.Phase);
                Assert.Equal(1, current.ExternalCallDepth);
            });
    }

    private sealed class RecordingDependency(
        string name,
        bool available,
        bool compatible) : IDependencyAdapter
    {
        public string Name { get; } = name;
        public int ProbeCount { get; private set; }

        public bool IsAvailable
        {
            get
            {
                ProbeCount++;
                return available;
            }
        }

        public bool IsCompatible
        {
            get
            {
                ProbeCount++;
                return compatible;
            }
        }

        public void Dispose()
        {
        }
    }
}
