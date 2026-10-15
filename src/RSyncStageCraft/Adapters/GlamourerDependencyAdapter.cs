using RSyncStageCraft.Core.Dependencies;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Read-only readiness probe over the version-pinned generated Glamourer API.
/// It owns no state application, lock, revert, unlock, or cleanup behavior.
/// </summary>
public sealed class GlamourerDependencyAdapter :
    IDependencyAdapter,
    IDependencyObservationAdapter
{
    public const int RequiredMajorVersion = 1;
    public const int MinimumMinorVersion = 8;

    private readonly Func<(int Major, int Minor)> getApiVersion;
    private readonly Func<string?> getInstalledVersion;
    private readonly string? contractAssemblyVersion;
    private int disposed;

    internal GlamourerDependencyAdapter(
        Func<(int Major, int Minor)> getApiVersion,
        Func<string?>? getInstalledVersion = null,
        string? contractAssemblyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(getApiVersion);
        this.getApiVersion = getApiVersion;
        this.getInstalledVersion = getInstalledVersion ?? (() => null);
        this.contractAssemblyVersion = contractAssemblyVersion;
    }

    public string Name => "Glamourer";
    public bool IsAvailable => TryGetVersion(out _);

    public bool IsCompatible =>
        TryGetVersion(out var version) &&
        version.Major == RequiredMajorVersion &&
        version.Minor >= MinimumMinorVersion;

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    DependencyAdapterObservation IDependencyObservationAdapter.Observe()
    {
        var installedVersion = TryGetInstalledVersion();
        var available = TryGetVersion(out var version);
        return new(
            available,
            available &&
                version.Major == RequiredMajorVersion &&
                version.Minor >= MinimumMinorVersion,
            installedVersion,
            available ? $"{version.Major}.{version.Minor}" : null,
            contractAssemblyVersion);
    }

    private bool TryGetVersion(out (int Major, int Minor) version)
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
