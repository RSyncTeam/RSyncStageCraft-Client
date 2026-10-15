using FFXIVClientStructs.FFXIV.Client.Game;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Dependencies;
using RSyncStageCraft.Core.Location;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class PluginConnectionPartsTests
{
    [Fact]
    public void OpenWorldLocationHasNoHousingPart()
    {
        var location = ClientLocation.FromOutdoor(132, 40, rawWard: -1, rawPlot: -1, rawDivision: 0)!;

        Assert.False(location.IsHousing);
        Assert.Equal((ushort)40, location.WorldId);
        Assert.Null(location.Ward);
        Assert.Null(location.Plot);
        Assert.Equal(new SceneLocation(132, null), location.ToSceneLocation());
        Assert.Equal("Zone 132, world 40", location.Describe());
    }

    [Fact]
    public void HousingDistrictOutdoorsUsesTheOutdoorGetters()
    {
        var onPlot = ClientLocation.FromOutdoor(340, 40, rawWard: 5, rawPlot: 33, rawDivision: 2)!;
        Assert.Equal((ushort)6, onPlot.Ward);
        Assert.Equal((ushort)34, onPlot.Plot);
        Assert.Equal((byte)2, onPlot.Division);
        Assert.False(onPlot.Indoors);
        // Outdoors the scene location is the ward's division: standing on a
        // plot or on the street of the same division is the same place.
        Assert.Equal(new SceneLocation(340, new HousingLocation(40, 6, 0, 0, Division: 2)), onPlot.ToSceneLocation());
        var subdivisionStreet = ClientLocation.FromOutdoor(340, 40, rawWard: 5, rawPlot: -1, rawDivision: 2)!;
        Assert.Equal(onPlot.ToSceneLocation(), subdivisionStreet.ToSceneLocation());

        // The main division is another place, and an unknown division fails closed.
        var street = ClientLocation.FromOutdoor(340, 40, rawWard: 5, rawPlot: -1, rawDivision: 1)!;
        Assert.Null(street.Plot);
        Assert.Equal(new SceneLocation(340, new HousingLocation(40, 6, 0, 0, Division: 1)), street.ToSceneLocation());
        Assert.Null(ClientLocation.FromOutdoor(340, 40, rawWard: 5, rawPlot: 3, rawDivision: 0)!.ToSceneLocation());
        Assert.Equal("Zone 340, world 40, ward 6, main division, outdoors", street.Describe());
    }

    [Fact]
    public void WindowShowsNamesButKeepsTheIds()
    {
        // Neutral sample places: synthetic world names and addresses.
        var outside = ClientLocation.FromOutdoor(340, 40, rawWard: 3, rawPlot: -1, rawDivision: 1)!;
        Assert.Equal(
            ["The Lavender Beds", "World: Example World", "Ward 4, main division, outdoors", "IDs: zone 340, world 40"],
            outside.DescribeLines(new LocationNames("The Lavender Beds", null, "Example World")));

        // Indoors the district is shown next to the interior's own name.
        var identity = HouseIdReader.Read(House(unit: 4, wardIndex: 6, room: 0, district: 339, world: 40))!.Value;
        var inside = ClientLocation.FromIndoor(282, 282, identity)!;
        Assert.Equal(
            ["Private Cottage - Mist (Mist)", "World: Other Example World", "Ward 7, main division, plot 5, indoors",
                "IDs: zone 282, district 339, world 40"],
            inside.DescribeLines(new LocationNames("Private Cottage - Mist", "Mist", "Other Example World")));
    }

    [Fact]
    public void MissingNamesFallBackToIds()
    {
        var open = ClientLocation.FromOutdoor(132, 40, -1, -1, 0)!;
        Assert.Equal(["Zone 132", "World: 40", "IDs: zone 132, world 40"], open.DescribeLines(LocationNames.None));
        Assert.Equal(["Zone 132", "World: 40", "IDs: zone 132, world 40"],
            open.DescribeLines(new LocationNames("  ", null, "")));
    }

    [Fact]
    public void NoTerritoryMeansNoLocation()
    {
        Assert.Null(ClientLocation.FromOutdoor(0, 40, -1, -1, 0));
    }

    // Real ClientStructs HouseId values: byte 0 is the unit (bit 7 =
    // apartment, low bits = plot or apartment division), bytes 2-3 hold the
    // ward (bits 0-5) and room (bits 6-15), then the district and world.
    private static HouseId House(byte unit, int wardIndex, int room, ushort district, ushort world) =>
        (ulong)unit |
        ((ulong)(ushort)((wardIndex & 0x3F) | (room << 6)) << 16) |
        ((ulong)district << 32) |
        ((ulong)world << 48);

    [Fact]
    public void HouseInteriorTakesPlotWardAndDivisionFromTheHouseId()
    {
        // Indoors the game reports division 0; the plot index decides it.
        var identity = HouseIdReader.Read(House(unit: 4, wardIndex: 6, room: 0, district: 339, world: 40))!.Value;
        Assert.Equal(339, identity.DistrictTerritoryId);
        Assert.Equal(4, identity.PlotIndex);
        Assert.False(identity.IsApartment);

        var location = ClientLocation.FromIndoor(originalTerritoryId: 282, currentTerritoryId: 1251, identity)!;
        Assert.Equal(282u, location.TerritoryId);
        Assert.Equal((ushort)339, location.DistrictTerritoryId);
        Assert.Equal((ushort)7, location.Ward);
        Assert.Equal((ushort)5, location.Plot);
        Assert.Equal((byte)1, location.Division);
        Assert.Null(location.Room);
        Assert.True(location.Indoors);
        Assert.Equal(new SceneLocation(282, new HousingLocation(40, 7, 5, 0, Division: 1)), location.ToSceneLocation());
    }

    [Fact]
    public void SubdivisionHouseAndPrivateChamberAreExact()
    {
        var identity = HouseIdReader.Read(House(unit: 40, wardIndex: 0, room: 3, district: 341, world: 40))!.Value;
        var location = ClientLocation.FromIndoor(384, 384, identity)!;

        Assert.Equal((ushort)41, location.Plot);
        Assert.Equal((byte)2, location.Division);
        Assert.Equal((ushort)3, location.Room);
        Assert.False(location.Workshop);
        Assert.Equal(new SceneLocation(384, new HousingLocation(40, 1, 41, 3, Division: 2)), location.ToSceneLocation());
    }

    [Fact]
    public void ApartmentIsAnExactProtocolLocationIncludingItsDivision()
    {
        var identity = HouseIdReader.Read(House(unit: 0x81, wardIndex: 4, room: 42, district: 340, world: 40))!.Value;
        Assert.True(identity.IsApartment);
        Assert.Equal(1, identity.ApartmentDivision);

        var location = ClientLocation.FromIndoor(608, 608, identity)!;
        Assert.True(location.Apartment);
        Assert.False(location.IsApartmentLobby);
        Assert.Null(location.Plot);
        Assert.Equal((ushort)42, location.Room);
        Assert.Equal((byte)2, location.Division);
        // Room numbers repeat across divisions; protocol 3 carries the division.
        Assert.Equal(new SceneLocation(608, new HousingLocation(40, 5, 0, 42, Division: 2)), location.ToSceneLocation());
        var mainDivision = HouseIdReader.Read(House(unit: 0x80, wardIndex: 4, room: 42, district: 340, world: 40))!.Value;
        Assert.NotEqual(location.ToSceneLocation(), ClientLocation.FromIndoor(608, 608, mainDivision)!.ToSceneLocation());
    }

    [Fact]
    public void ApartmentLobbyIsAKnownLocation()
    {
        // Room 0 inside an apartment building is its lobby, not an unknown house.
        var identity = HouseIdReader.Read(House(unit: 0x80, wardIndex: 7, room: 0, district: 339, world: 40))!.Value;
        Assert.True(identity.IsApartment);
        Assert.Equal(0, identity.ApartmentDivision);
        Assert.Equal(0, identity.RoomNumber);

        var lobby = ClientLocation.FromIndoor(573, 573, identity)!;
        Assert.True(lobby.Apartment);
        Assert.True(lobby.IsApartmentLobby);
        Assert.Null(lobby.Room);
        Assert.Null(lobby.Plot);
        Assert.Equal((ushort)8, lobby.Ward);
        Assert.Equal((byte)1, lobby.Division);
        Assert.Equal(
            "Zone 573, district 339, world 40, ward 8, main division, apartment lobby, indoors",
            lobby.Describe());
        // The lobby is its own place: room 0 of that building.
        Assert.Equal(new SceneLocation(573, new HousingLocation(40, 8, 0, 0, Division: 1)), lobby.ToSceneLocation());
    }

    [Fact]
    public void WorkshopIsItsOwnProtocolLocation()
    {
        var identity = HouseIdReader.Read(House(unit: 5, wardIndex: 1, room: 0x3FF, district: 339, world: 40))!.Value;
        Assert.True(identity.IsWorkshop);

        var location = ClientLocation.FromIndoor(423, 423, identity)!;
        Assert.True(location.Workshop);
        Assert.Equal((ushort)6, location.Plot);
        Assert.Null(location.Room);
        Assert.Equal(
            new SceneLocation(423, new HousingLocation(40, 2, 6, 0, Division: 1, Workshop: true)),
            location.ToSceneLocation());
        var house = HouseIdReader.Read(House(unit: 5, wardIndex: 1, room: 0, district: 339, world: 40))!.Value;
        Assert.NotEqual(location.ToSceneLocation(), ClientLocation.FromIndoor(423, 423, house)!.ToSceneLocation());
    }

    [Fact]
    public void UnknownHouseIsNeverGuessed()
    {
        Assert.Null(HouseIdReader.Read(0UL));
        Assert.Null(HouseIdReader.Read(ulong.MaxValue));
        var noWorld = HouseIdReader.Read(House(unit: 3, wardIndex: 0, room: 0, district: 339, world: 0))!.Value;
        Assert.Null(ClientLocation.FromIndoor(282, 282, noWorld));
    }

    [Fact]
    public void ReadinessNeedsBothPluginsAndNeverReportsHosts()
    {
        var both = ConnectionDependencies.FromHealth(
        [
            new DependencyHealth("Penumbra", true, true),
            new DependencyHealth("Glamourer", true, true),
        ]);
        Assert.True(both.CanConnect);
        Assert.False(both.LocalObjectHostReady);
        Assert.False(both.IsReady);

        var incompatible = ConnectionDependencies.FromHealth(
        [
            new DependencyHealth("Penumbra", true, false),
            new DependencyHealth("Glamourer", true, true),
        ]);
        Assert.False(incompatible.PenumbraReady);
        // Penumbra and Glamourer do not gate connecting.
        Assert.True(incompatible.CanConnect);

        var missing = ConnectionDependencies.FromHealth([new DependencyHealth("Penumbra", true, true)]);
        Assert.False(missing.GlamourerReady);
        Assert.True(missing.CanConnect);
        var baseGameOnly = ConnectionDependencies.FromHealth([]);
        Assert.False(baseGameOnly.PenumbraReady);
        Assert.True(baseGameOnly.CanConnect);
    }

    [Fact]
    public void ProtectedKeyRoundTripsAndIsNotStoredInPlainText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rsc-key-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProtectedAccessKeyStore(directory);
            var key = "rsc1." + new string('a', 32) + "." + new string('b', 64);
            Assert.Null(store.Load());

            Assert.True(store.Save(key));
            Assert.True(store.Exists);
            var raw = File.ReadAllBytes(Path.Combine(directory, ProtectedAccessKeyStore.FileName));
            Assert.DoesNotContain("rsc1.", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
            Assert.Equal(key, new ProtectedAccessKeyStore(directory).Load());

            Assert.True(store.Save(null));
            Assert.False(store.Exists);
            Assert.Null(store.Load());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptKeyFileLoadsAsNoKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rsc-key-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, ProtectedAccessKeyStore.FileName), [1, 2, 3, 4]);
            Assert.Null(new ProtectedAccessKeyStore(directory).Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
