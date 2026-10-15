using RSyncStageCraft.Core.Dependencies;
using RSyncStageCraft.Core.Diagnostics;

namespace RSyncStageCraft.Ui;

internal sealed class PluginStatusModel
{
    private readonly DependencyHealthMonitor health;
    private readonly Func<IReadOnlyList<OptionalHostMetadata>> optionalHostProbe;
    private readonly Action<string>? log;
    private readonly Dictionary<string, string?> loggedFailures = new(StringComparer.Ordinal);
    private DependencyHealth[] dependencies = Array.Empty<DependencyHealth>();
    private OptionalHostMetadata[] optionalHosts = Array.Empty<OptionalHostMetadata>();

    internal PluginStatusModel(DependencyRegistry dependencies)
        : this(dependencies, observations: null)
    {
    }

    internal PluginStatusModel(
        DependencyRegistry dependencies,
        RuntimeObservationTrace? observations,
        Func<IReadOnlyList<OptionalHostMetadata>>? optionalHostProbe = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        health = new DependencyHealthMonitor(dependencies, observations);
        this.optionalHostProbe = optionalHostProbe ??
            (static () => Array.Empty<OptionalHostMetadata>());
        this.log = log;
    }

    internal bool HasObservation { get; private set; }

    internal bool IsReady =>
        HasObservation &&
        dependencies.Length != 0 &&
        dependencies.All(current => current.Available && current.Compatible);

    internal IReadOnlyList<DependencyHealth> Dependencies => dependencies;

    internal IReadOnlyList<OptionalHostMetadata> OptionalHosts => optionalHosts;

    internal void Refresh()
    {
        dependencies = health.Snapshot().ToArray();
        // The failure text goes to the log (once per change), not to the player.
        foreach (var dependency in dependencies)
        {
            if (loggedFailures.TryGetValue(dependency.Name, out var last) &&
                string.Equals(last, dependency.FailureReason, StringComparison.Ordinal))
                continue;
            loggedFailures[dependency.Name] = dependency.FailureReason;
            if (dependency.FailureReason is { } failure)
                log?.Invoke($"RSC plugin status: {dependency.Name}: {failure}");
        }
        try
        {
            optionalHosts = optionalHostProbe().ToArray();
        }
        catch
        {
            optionalHosts = Array.Empty<OptionalHostMetadata>();
        }
        HasObservation = true;
    }
}
