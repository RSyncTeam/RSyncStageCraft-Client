using Dalamud.Plugin;
using RSyncStageCraft.Core.Dependencies;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Reads only Dalamud's installed-plugin manifest metadata for explicitly
/// audited optional hosts. It does not create IPC subscribers or call a
/// provider.
/// </summary>
internal static class DalamudOptionalHostMetadataProbe
{
    internal const int MaximumInstalledPlugins = 256;
    private const int MaximumVersionLength = 96;

    private static readonly HostDefinition[] KnownHosts =
    [
        new("Customize+", "CustomizePlus"),
    ];

    internal static IReadOnlyList<OptionalHostMetadata> Observe(
        IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);

        try
        {
            return ObserveCandidates(
                pluginInterface.InstalledPlugins.Select(plugin =>
                    new InstalledPluginCandidate(
                        plugin.InternalName,
                        plugin.Version.ToString())));
        }
        catch
        {
            return Unobserved();
        }
    }

    internal static IReadOnlyList<OptionalHostMetadata> ObserveCandidates(
        IEnumerable<InstalledPluginCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        try
        {
            var matches = new Dictionary<string, string?>(
                StringComparer.OrdinalIgnoreCase);
            using var enumerator = candidates.GetEnumerator();
            for (var inspected = 0; inspected < MaximumInstalledPlugins; inspected++)
            {
                if (!enumerator.MoveNext())
                    return Observed(matches);

                var candidate = enumerator.Current;
                if (candidate.InternalName is null ||
                    matches.ContainsKey(candidate.InternalName) ||
                    !KnownHosts.Any(host => string.Equals(
                        host.InternalName,
                        candidate.InternalName,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                matches.Add(
                    candidate.InternalName,
                    BoundVersion(candidate.Version));
            }

            // An unexpectedly large or non-terminating inventory is not a
            // trustworthy snapshot. Fail closed instead of publishing a
            // partial observation.
            return enumerator.MoveNext()
                ? Unobserved()
                : Observed(matches);
        }
        catch
        {
            return Unobserved();
        }
    }

    internal static IReadOnlyList<OptionalHostMetadata> Unobserved() =>
        KnownHosts
            .Select(host => new OptionalHostMetadata(
                host.DisplayName,
                host.InternalName,
                InventoryObserved: false,
                Installed: false,
                InstalledVersion: null))
            .ToArray();

    private static IReadOnlyList<OptionalHostMetadata> Observed(
        IReadOnlyDictionary<string, string?> matches) =>
        KnownHosts
            .Select(host => new OptionalHostMetadata(
                host.DisplayName,
                host.InternalName,
                InventoryObserved: true,
                Installed: matches.TryGetValue(
                    host.InternalName,
                    out var version),
                InstalledVersion: version))
            .ToArray();

    private static string? BoundVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        return version.Length <= MaximumVersionLength
            ? version
            : version[..MaximumVersionLength];
    }

    private readonly record struct HostDefinition(
        string DisplayName,
        string InternalName);
}

internal readonly record struct InstalledPluginCandidate(
    string? InternalName,
    string? Version);
