namespace RSyncStageCraft.Adapters;

/// <summary>
/// Client-owned filesystem roots derived from Dalamud's API 15 plugin-specific
/// configuration directory. Path derivation is pure: startup remains
/// responsible for creating directories and restoring persisted state.
/// </summary>
public sealed class DalamudPluginDataPaths
{
    public DalamudPluginDataPaths(DirectoryInfo configDirectory)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);

        ConfigDirectory = Path.GetFullPath(configDirectory.FullName);
        AppearanceCacheDirectory = Path.Combine(ConfigDirectory, "appearance-cache");
    }

    public string ConfigDirectory { get; }

    /// <summary>
    /// The disposable appearance blob cache (versioned inside, see
    /// AppearanceBlobCache); separate from settings and the access key.
    /// </summary>
    public string AppearanceCacheDirectory { get; }
}
