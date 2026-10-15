using RSyncStageCraft.Core;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Dependencies;

namespace RSyncStageCraft.Tests;

public sealed class DependencyRegistryTests
{
    [Fact]
    public void RegistryIsNotReadyUntilRegisteredDependenciesAreHealthy()
    {
        using var registry = new DependencyRegistry();
        Assert.False(registry.IsReady);

        var penumbra = new FakeDependency("Penumbra", true, true);
        var glamourer = new FakeDependency("Glamourer", true, true);
        registry.Register(penumbra);
        registry.Register(glamourer);

        Assert.True(registry.IsReady);
    }

    [Fact]
    public void UnavailableDependencyMakesRegistryNotReady()
    {
        using var registry = new DependencyRegistry();
        registry.Register(new FakeDependency("Penumbra", true, true));
        registry.Register(new FakeDependency("Glamourer", false, true));

        Assert.False(registry.IsReady);
    }

    [Fact]
    public void DuplicateDependencyNamesAreRejected()
    {
        using var registry = new DependencyRegistry();
        registry.Register(new FakeDependency("Penumbra", true, true));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new FakeDependency("penumbra", true, true)));
    }

    [Fact]
    public void HealthSnapshotReportsFailures()
    {
        using var registry = new DependencyRegistry();
        registry.Register(new FakeDependency("Glamourer", true, false));
        var monitor = new DependencyHealthMonitor(registry);

        var health = Assert.Single(monitor.Snapshot());
        Assert.False(health.Available && health.Compatible);
        Assert.NotNull(health.FailureReason);
    }

    [Fact]
    public void CapacityAndNamesAreBoundedWithoutDisposingRejectedAdapter()
    {
        using var registry = new DependencyRegistry(maximumDependencies: 1);
        var first = new FakeDependency("Penumbra", true, true);
        var excess = new FakeDependency("Glamourer", true, true);
        using var names = new DependencyRegistry();

        Assert.True(registry.TryRegister(first).Registered);
        Assert.Equal(
            DependencyRegistrationStatus.CapacityExceeded,
            registry.TryRegister(excess).Status);
        Assert.Equal(
            DependencyRegistrationStatus.InvalidName,
            names.TryRegister(
                new FakeDependency(
                    new string('x', ProtocolLimits.MaximumIdentifierLength + 1),
                    true,
                    true)).Status);
        Assert.Equal(0, excess.DisposeCount);
        Assert.Single(registry.Adapters);
    }

    [Fact]
    public void ConstructorRejectsDependencyLimitAboveHardMaximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DependencyRegistry(
                ClientLimits.MaximumDependencyAdapters + 1));
    }

    [Fact]
    public void ConcurrentDuplicateRegistrationAcceptsExactlyOne()
    {
        using var registry = new DependencyRegistry();
        var results = new DependencyRegistrationResult[32];

        Parallel.For(
            0,
            results.Length,
            index => results[index] = registry.TryRegister(
                new FakeDependency("Penumbra", true, true)));

        Assert.Single(results, result => result.Registered);
        Assert.All(
            results.Where(result => !result.Registered),
            result => Assert.Equal(
                DependencyRegistrationStatus.DuplicateName,
                result.Status));
        Assert.Single(registry.Adapters);
    }

    [Fact]
    public void AdapterCollectionIsAnImmutablePointInTimeSnapshot()
    {
        using var registry = new DependencyRegistry();
        registry.Register(new FakeDependency("Penumbra", true, true));
        var before = registry.Adapters;

        registry.Register(new FakeDependency("Glamourer", true, true));

        Assert.Single(before);
        Assert.Equal(2, registry.Adapters.Count);
    }

    [Fact]
    public void RegisteredNameCannotBeChangedByExternalAdapterMutation()
    {
        using var registry = new DependencyRegistry();
        var dependency = new MutableNameDependency("Penumbra");
        registry.Register(dependency);
        dependency.Name = "Renamed";

        var duplicate = registry.TryRegister(
            new FakeDependency("penumbra", true, true));
        var health = Assert.Single(new DependencyHealthMonitor(registry).Snapshot());

        Assert.Equal(DependencyRegistrationStatus.DuplicateName, duplicate.Status);
        Assert.Equal("Penumbra", health.Name);
    }

    [Fact]
    public void DisposalAttemptsEveryAdapterBeforeSurfacingFailures()
    {
        var registry = new DependencyRegistry();
        var first = new FakeDependency("Penumbra", true, true, throwOnDispose: true);
        var second = new FakeDependency("Glamourer", true, true);
        var third = new FakeDependency("Transport", true, true, throwOnDispose: true);
        registry.Register(first);
        registry.Register(second);
        registry.Register(third);

        var failure = Assert.Throws<AggregateException>(registry.Dispose);

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, third.DisposeCount);
        Assert.Empty(registry.Adapters);
        registry.Dispose();
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public void RegistrationAfterDisposeFailsWithoutTakingOwnership()
    {
        var registry = new DependencyRegistry();
        registry.Dispose();
        var dependency = new FakeDependency("Penumbra", true, true);

        var result = registry.TryRegister(dependency);

        Assert.Equal(DependencyRegistrationStatus.Disposed, result.Status);
        Assert.Equal(0, dependency.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => registry.Register(dependency));
    }

    [Fact]
    public void ThrowingHealthProbeFailsClosedAndBoundsDiagnostic()
    {
        using var registry = new DependencyRegistry();
        registry.Register(new ThrowingHealthDependency());
        var monitor = new DependencyHealthMonitor(registry);

        var health = Assert.Single(monitor.Snapshot());

        Assert.False(registry.IsReady);
        Assert.False(health.Available);
        Assert.False(health.Compatible);
        Assert.NotNull(health.FailureReason);
        Assert.True(
            health.FailureReason.Length <=
            ClientLimits.MaximumDependencyFailureReasonLength);
    }

    [Fact]
    public void ThrowingExceptionMessageCannotEscapeHealthSnapshot()
    {
        using var registry = new DependencyRegistry();
        registry.Register(new ThrowingMessageHealthDependency());
        var monitor = new DependencyHealthMonitor(registry);

        var health = Assert.Single(monitor.Snapshot());

        Assert.False(health.Available);
        Assert.False(health.Compatible);
        Assert.Equal("Dependency health probe failed: ", health.FailureReason);
    }

    private sealed class FakeDependency(
        string name,
        bool available,
        bool compatible,
        bool throwOnDispose = false) : IDependencyAdapter
    {
        public string Name { get; } = name;
        public bool IsAvailable { get; } = available;
        public bool IsCompatible { get; } = compatible;
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose)
                throw new InvalidOperationException($"Failed to dispose {Name}.");
        }
    }

    private sealed class ThrowingHealthDependency : IDependencyAdapter
    {
        public string Name => "Penumbra";
        public bool IsAvailable =>
            throw new InvalidOperationException(new string('x', 2048));
        public bool IsCompatible => true;
        public void Dispose()
        {
        }
    }

    private sealed class ThrowingMessageHealthDependency : IDependencyAdapter
    {
        public string Name => "Penumbra";
        public bool IsAvailable => throw new ThrowingMessageException();
        public bool IsCompatible => true;
        public void Dispose()
        {
        }
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message =>
            throw new InvalidOperationException("Diagnostic accessor failed.");
    }

    private sealed class MutableNameDependency(string name) : IDependencyAdapter
    {
        public string Name { get; set; } = name;
        public bool IsAvailable => true;
        public bool IsCompatible => true;
        public void Dispose()
        {
        }
    }
}
