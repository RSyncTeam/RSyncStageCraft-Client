using RSyncStageCraft.Adapters;

namespace RSyncStageCraft.Tests;

public sealed class DalamudPluginDataPathsTests
{
    [Fact]
    public void AppearanceCacheIsDerivedBelowExactPluginConfigDirectory()
    {
        var config = Path.Combine(
            Path.GetTempPath(),
            "rsync-stagecraft-config",
            Guid.NewGuid().ToString("N"));

        var paths = new DalamudPluginDataPaths(new DirectoryInfo(config));

        Assert.Equal(Path.GetFullPath(config), paths.ConfigDirectory);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(config), "appearance-cache"),
            paths.AppearanceCacheDirectory);
        Assert.False(Directory.Exists(config));
    }

    [Fact]
    public void NullConfigDirectoryIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DalamudPluginDataPaths(null!));
    }
}
