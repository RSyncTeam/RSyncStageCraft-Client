using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Diagnostics;

namespace RSyncStageCraft.Core.Dependencies;

public readonly record struct DependencyHealth(
    string Name,
    bool Available,
    bool Compatible,
    string? FailureReason = null,
    string? InstalledVersion = null,
    string? ProviderApiVersion = null,
    string? ContractAssemblyVersion = null);

public sealed class DependencyHealthMonitor
{
    private readonly DependencyRegistry registry;
    private readonly RuntimeObservationTrace? observations;

    public DependencyHealthMonitor(DependencyRegistry registry)
        : this(registry, observations: null)
    {
    }

    internal DependencyHealthMonitor(
        DependencyRegistry registry,
        RuntimeObservationTrace? observations)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
        this.observations = observations;
    }

    public IReadOnlyCollection<DependencyHealth> Snapshot() =>
        registry.SnapshotDependencies().Select(Probe).ToArray();

    private DependencyHealth Probe(RegisteredDependency dependency)
    {
        using var call = observations?.BeginExternalCall(
            "dependency.probe",
            dependency.Name);
        try
        {
            if (dependency.Adapter is IDependencyObservationAdapter observable)
            {
                var observation = observable.Observe();
                return new(
                    dependency.Name,
                    observation.Available,
                    observation.Compatible,
                    observation.Available && observation.Compatible
                        ? null
                        : "Dependency unavailable or incompatible.",
                    BoundedValue(observation.InstalledVersion),
                    BoundedValue(observation.ProviderApiVersion),
                    BoundedValue(observation.ContractAssemblyVersion));
            }

            var available = dependency.Adapter.IsAvailable;
            var compatible = dependency.Adapter.IsCompatible;
            return new(
                dependency.Name,
                available,
                compatible,
                available && compatible
                    ? null
                    : "Dependency unavailable or incompatible.");
        }
        catch (Exception ex)
        {
            return new(
                dependency.Name,
                Available: false,
                Compatible: false,
                BoundedFailure("Dependency health probe failed: ", SafeMessage(ex)));
        }
    }

    private static string? SafeMessage(Exception exception)
    {
        try
        {
            return exception.Message;
        }
        catch
        {
            // Diagnostics are not authority. A hostile or unusual exception
            // must not turn an already failed health probe into an escaping
            // readiness-monitor failure.
            return null;
        }
    }

    private static string BoundedFailure(string prefix, string? detail)
    {
        var maximum = ClientLimits.MaximumDependencyFailureReasonLength;
        if (prefix.Length >= maximum)
            return prefix[..maximum];
        if (string.IsNullOrEmpty(detail))
            return prefix;

        var detailLength = Math.Min(detail.Length, maximum - prefix.Length);
        return string.Concat(prefix, detail.AsSpan(0, detailLength));
    }

    private static string? BoundedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Length <= ProtocolLimits.MaximumIdentifierLength
            ? value
            : value[..ProtocolLimits.MaximumIdentifierLength];
    }
}
