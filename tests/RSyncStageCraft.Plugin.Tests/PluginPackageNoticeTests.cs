#nullable enable
using System.Diagnostics;
using System.IO.Compression;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The plugin travels with its licences and notices: the AGPL text, the GPL
/// text of the embedded BNpcLink data, the third-party notices and SOURCE.txt,
/// which names this exact source commit. A Release build checks the packaged
/// latest.zip; a Debug build checks the plugin's output folder.
/// </summary>
public sealed class PluginPackageNoticeTests
{
    private const string PluginFolder = "src/RSyncStageCraft";

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    // The packaged name and the repository file it must equal byte for byte.
    private static readonly (string Packaged, string Source)[] Notices =
    [
        ("LICENSE", "LICENSE"),
        ("BNpcLink.LICENSE", PluginFolder + "/Data/BNpcLink.LICENSE"),
        ("THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md"),
    ];

    [Fact]
    public void ThePluginCarriesItsLicencesAndNoticesUnchanged()
    {
        var package = ReadPackage();
        foreach (var (packaged, source) in Notices)
        {
            Assert.True(package.TryGetValue(packaged, out var content), $"{packaged} is missing from the plugin package");
            Assert.Equal(File.ReadAllBytes(Path.Combine(RepositoryRoot(), source)), content);
        }
    }

    [Fact]
    public void SourceTxtNamesThePublicRepositoryAndThisCommit()
    {
        var package = ReadPackage();
        Assert.True(package.TryGetValue("SOURCE.txt", out var content), "SOURCE.txt is missing from the plugin package");
        var lines = System.Text.Encoding.UTF8.GetString(content!).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal("RSyncStageCraft source code: https://github.com/RSyncTeam/RSyncStageCraft-Client", lines[0]);
        Assert.Equal("Commit: " + HeadCommit(), lines[1]);
        Assert.StartsWith("Licence: AGPL-3.0-only", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackageHoldsThePluginAndNoTestOutput()
    {
        var names = ReadPackage().Keys;
        Assert.Contains("RSyncStageCraft.dll", names);
        Assert.DoesNotContain(names, name => name.Contains("Tests", StringComparison.OrdinalIgnoreCase) || name.Contains("xunit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThePackageBundlesNoNewtonsoftJson()
    {
        // HV-3's Debug-only Newtonsoft.Json reference resolves to Dalamud's own
        // copy, which Dalamud loads for every plugin: no build ships its own.
        var names = ReadPackage().Keys;
        Assert.Contains("RSyncStageCraft.dll", names);
        Assert.DoesNotContain(names, name => Path.GetFileName(name).StartsWith("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>File name to content, from latest.zip (Release) or the output folder (Debug).</summary>
    private static Dictionary<string, byte[]> ReadPackage()
    {
        var output = Path.Combine(RepositoryRoot(), PluginFolder, "bin", Configuration);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
#if DEBUG
        var plugin = Directory.EnumerateFiles(output, "RSyncStageCraft.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Distinct()
            .ToArray();
        var folder = Assert.Single(plugin)!;
        foreach (var path in Directory.EnumerateFiles(folder))
            files[Path.GetFileName(path)] = File.ReadAllBytes(path);
#else
        var zip = Assert.Single(Directory.EnumerateFiles(output, "latest.zip", SearchOption.AllDirectories));
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries.Where(entry => entry.Name.Length > 0))
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            files[entry.FullName.Replace('\\', '/')] = copy.ToArray();
        }
#endif
        return files;
    }

    private static string HeadCommit()
    {
        var start = new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var git = Process.Start(start)!;
        var commit = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        Assert.Matches("^[0-9a-f]{40}$", commit);
        return commit;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RSyncStageCraft-Client.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
