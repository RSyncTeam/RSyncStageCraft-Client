#nullable enable
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Newtonsoft.Json;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The two-player limit as the plugin sees it: no ten-player constant or
/// setting is left, and settings files saved by older versions still load.
/// </summary>
public sealed class TwoPlayerLimitTests
{
    [Fact]
    public void ThePluginAssemblyHasNoTenPlayerConstantOrSettingLeft()
    {
        foreach (var assembly in new[] { typeof(ProtocolConstants).Assembly, typeof(Configuration).Assembly })
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var names = metadata.FieldDefinitions.Select(handle => metadata.GetString(metadata.GetFieldDefinition(handle).Name))
                .Concat(metadata.PropertyDefinitions.Select(handle =>
                    metadata.GetString(metadata.GetPropertyDefinition(handle).Name)))
                .ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain("MaximumPlayers", names);
            Assert.DoesNotContain("TypicalPlayers", names);
        }
        Assert.Equal(2, ProtocolConstants.MaximumGroupMembers);
    }

    [Fact]
    public void AnOldSettingsFileWithTheRemovedPlayerCountsStillLoads()
    {
        // Dalamud saves plugin settings with Newtonsoft.Json and its type name
        // (TypeNameHandling.Objects); unknown properties are skipped by default
        // (MissingMemberHandling.Ignore). MaximumPlayers and TypicalPlayers
        // were removed from Configuration.
        const string old = """
            {
              "$type": "RSyncStageCraft.Configuration, RSyncStageCraft",
              "Version": 1,
              "ProtocolVersion": 6,
              "MaximumPlayers": 10,
              "TypicalPlayers": 2,
              "ServerEndpoint": "wss://rsc.example.test/rsc/v1/control",
              "ConnectEnabled": true
            }
            """;
        foreach (var settings in new[]
                 {
                     new JsonSerializerSettings(),
                     new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects },
                 })
        {
            var loaded = JsonConvert.DeserializeObject<Configuration>(old, settings);
            Assert.NotNull(loaded);
            Assert.Equal("wss://rsc.example.test/rsc/v1/control", loaded!.ServerEndpoint);
            Assert.True(loaded.ConnectEnabled);
            Assert.Equal(1, loaded.Version);
        }
    }

    [Fact]
    public void ANewInstallHasNoServerAddressAndASavedOneIsKept()
    {
        Assert.Equal(string.Empty, new Configuration().ServerEndpoint);
        const string saved = """
            {
              "$type": "RSyncStageCraft.Configuration, RSyncStageCraft",
              "Version": 1,
              "ServerEndpoint": "wss://rsc.example.test/rsc/v1/control"
            }
            """;
        var loaded = JsonConvert.DeserializeObject<Configuration>(
            saved, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects });
        Assert.Equal("wss://rsc.example.test/rsc/v1/control", loaded!.ServerEndpoint);
    }
}
