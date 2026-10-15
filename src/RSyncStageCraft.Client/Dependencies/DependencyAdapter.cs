using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Dependencies;

public interface IDependencyAdapter : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    bool IsCompatible { get; }
}

internal interface IDependencyObservationAdapter
{
    DependencyAdapterObservation Observe();
}

internal readonly record struct DependencyAdapterObservation(
    bool Available,
    bool Compatible,
    string? InstalledVersion,
    string? ProviderApiVersion,
    string? ContractAssemblyVersion);

public enum DependencyRegistrationStatus
{
    Registered,
    InvalidName,
    DuplicateName,
    CapacityExceeded,
    Disposed,
}

public readonly record struct DependencyRegistrationResult(
    DependencyRegistrationStatus Status)
{
    public bool Registered => Status == DependencyRegistrationStatus.Registered;
}

public sealed class DependencyRegistry : IDisposable
{
    private readonly object gate = new();
    private readonly int maximumDependencies;
    private readonly List<RegisteredDependency> adapters = new();
    private bool disposed;

    public DependencyRegistry(
        int maximumDependencies = ClientLimits.DefaultDependencyAdapters)
    {
        if (maximumDependencies <= 0 ||
            maximumDependencies > ClientLimits.MaximumDependencyAdapters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDependencies));
        }

        this.maximumDependencies = maximumDependencies;
    }

    public void Register(IDependencyAdapter adapter)
    {
        var result = TryRegister(adapter);
        switch (result.Status)
        {
            case DependencyRegistrationStatus.Registered:
                return;
            case DependencyRegistrationStatus.InvalidName:
                throw new ArgumentException(
                    "Dependency name is invalid.",
                    nameof(adapter));
            case DependencyRegistrationStatus.DuplicateName:
                throw new InvalidOperationException(
                    "Dependency name is already registered.");
            case DependencyRegistrationStatus.CapacityExceeded:
                throw new InvalidOperationException(
                    "Dependency registry capacity is exhausted.");
            case DependencyRegistrationStatus.Disposed:
                throw new ObjectDisposedException(nameof(DependencyRegistry));
            default:
                throw new InvalidOperationException("Unknown dependency registration result.");
        }
    }

    public DependencyRegistrationResult TryRegister(IDependencyAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        lock (gate)
        {
            if (disposed)
                return new(DependencyRegistrationStatus.Disposed);
        }

        string? name;
        try
        {
            name = adapter.Name;
        }
        catch
        {
            return new(DependencyRegistrationStatus.InvalidName);
        }
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > ProtocolLimits.MaximumIdentifierLength)
        {
            return new(DependencyRegistrationStatus.InvalidName);
        }

        lock (gate)
        {
            if (disposed)
                return new(DependencyRegistrationStatus.Disposed);
            if (adapters.Any(existing => string.Equals(
                    existing.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return new(DependencyRegistrationStatus.DuplicateName);
            }
            if (adapters.Count >= maximumDependencies)
                return new(DependencyRegistrationStatus.CapacityExceeded);

            adapters.Add(new(name, adapter));
            return new(DependencyRegistrationStatus.Registered);
        }
    }

    public bool IsReady
    {
        get
        {
            var snapshot = SnapshotDependencies();
            if (snapshot.Length == 0)
                return false;

            foreach (var dependency in snapshot)
            {
                try
                {
                    if (!dependency.Adapter.IsAvailable ||
                        !dependency.Adapter.IsCompatible)
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }
    }

    public IReadOnlyCollection<IDependencyAdapter> Adapters =>
        SnapshotDependencies().Select(entry => entry.Adapter).ToArray();

    public void Dispose()
    {
        RegisteredDependency[] snapshot;
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            snapshot = adapters.ToArray();
            adapters.Clear();
        }

        List<Exception>? failures = null;
        foreach (var dependency in snapshot)
        {
            try
            {
                dependency.Adapter.Dispose();
            }
            catch (Exception ex)
            {
                failures ??= new List<Exception>();
                failures.Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more dependencies failed to dispose.", failures);
    }

    internal RegisteredDependency[] SnapshotDependencies()
    {
        lock (gate)
            return disposed ? Array.Empty<RegisteredDependency>() : adapters.ToArray();
    }
}

internal readonly record struct RegisteredDependency(
    string Name,
    IDependencyAdapter Adapter);
