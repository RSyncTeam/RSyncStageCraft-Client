using RSyncStageCraft.Core.Dependencies;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Read-only readiness probe over the version-pinned generated Penumbra API.
/// It owns no collection, assignment, mod, redraw, or cleanup behavior.
/// </summary>
public sealed class PenumbraDependencyAdapter :
    IDependencyAdapter,
    IDependencyObservationAdapter
{
    public const int RequiredBreakingVersion = 5;
    public const int MinimumFeatureVersion = 15;

    private readonly Func<(int Breaking, int Feature)> getApiVersion;
    private readonly Func<string?> getInstalledVersion;
    private readonly string? contractAssemblyVersion;
    private int disposed;

    internal PenumbraDependencyAdapter(
        Func<(int Breaking, int Feature)> getApiVersion,
        Func<string?>? getInstalledVersion = null,
        string? contractAssemblyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(getApiVersion);
        this.getApiVersion = getApiVersion;
        this.getInstalledVersion = getInstalledVersion ?? (() => null);
        this.contractAssemblyVersion = contractAssemblyVersion;
    }

    public string Name => "Penumbra";
    public bool IsAvailable => TryGetVersion(out _);

    public bool IsCompatible =>
        TryGetVersion(out var version) &&
        version.Breaking == RequiredBreakingVersion &&
        version.Feature >= MinimumFeatureVersion;

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    DependencyAdapterObservation IDependencyObservationAdapter.Observe()
    {
        var installedVersion = TryGetInstalledVersion();
        var available = TryGetVersion(out var version);
        return new(
            available,
            available &&
                version.Breaking == RequiredBreakingVersion &&
                version.Feature >= MinimumFeatureVersion,
            installedVersion,
            available ? $"{version.Breaking}.{version.Feature}" : null,
            contractAssemblyVersion);
    }

    private bool TryGetVersion(out (int Breaking, int Feature) version)
    {
        version = default;
        if (Volatile.Read(ref disposed) != 0)
            return false;

        try
        {
            version = getApiVersion();
            return Volatile.Read(ref disposed) == 0;
        }
        catch
        {
            return false;
        }
    }

    private string? TryGetInstalledVersion()
    {
        if (Volatile.Read(ref disposed) != 0)
            return null;

        try
        {
            return getInstalledVersion();
        }
        catch
        {
            return null;
        }
    }
}
