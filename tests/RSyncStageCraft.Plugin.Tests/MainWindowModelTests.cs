using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Location;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Ui;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The main window's navigation, banner, status strip and Scene here models,
/// without ImGui.
/// </summary>
public sealed class MainWindowModelTests
{
    private static readonly RscUserId Me = new("alice");
    private static readonly RscUserId Other = new("bob");

    private sealed class Selection
    {
        internal List<RscObjectId?> Calls { get; } = [];

        internal void Select(RscObjectId? objectId) => Calls.Add(objectId);
    }

    private static FurnitureState Furniture(RscUserId creator, SceneLocation location) => new(
        new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), creator, TestLocations.SharedScene,
        "furniture:indoor:6000", new Vector3State(0, 0, 0), new RotationState(0, 0, 0, 1)) { Location = location };

    private static LightState Light(RscUserId creator, SceneLocation location) => new(
        new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), creator, TestLocations.SharedScene,
        LightIdentity.Point, new Vector3State(0, 0, 0), LightLimits.UnitScale) { Location = location };

    private static ActorState Actor(RscUserId owner, SceneLocation location) => new(
        new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), owner, TestLocations.SharedScene, 4242,
        new Vector3State(0, 0, 0), 0, 1, ActorControlState.Default) { Location = location };

    private static FurnitureUnsupportedReport Report(LocalObjectReportKind kind) =>
        new(new RscObjectId(Guid.NewGuid()), "x", null, "reason", 1, kind);

    // ---- Navigation --------------------------------------------------------

    [Fact]
    public void TheWindowStartsOnSceneHereAndOpenShowsTheLastTab()
    {
        var selection = new Selection();
        var navigation = new MainWindowNavigation(selection.Select);

        Assert.Equal(MainTab.SceneHere, navigation.Current);
        Assert.Null(navigation.Requested);

        // The user picks Appearances; closing and opening again ("Open") keeps it.
        Assert.True(navigation.ObserveActive(MainTab.Appearances));
        navigation.Closed();
        Assert.False(navigation.ObserveActive(MainTab.Appearances));
        Assert.Equal(MainTab.Appearances, navigation.Current);
        Assert.Null(navigation.Requested);
    }

    [Fact]
    public void SettingsOpensTheSettingsTab()
    {
        var navigation = new MainWindowNavigation(new Selection().Select);

        navigation.OpenSettings();

        Assert.Equal(MainTab.Settings, navigation.Requested);
        // The tab bar still shows the old tab on the frame the switch is asked for.
        Assert.False(navigation.ObserveActive(MainTab.SceneHere));
        Assert.Equal(MainTab.Settings, navigation.Requested);
        Assert.True(navigation.ObserveActive(MainTab.Settings));
        Assert.Equal(MainTab.Settings, navigation.Current);
        Assert.Null(navigation.Requested);
    }

    [Fact]
    public void TheLastTabIsHeldForTheSessionOnlyAndNeverSaved()
    {
        Assert.DoesNotContain(typeof(Configuration).GetProperties(),
            property => property.PropertyType == typeof(MainTab) ||
                        property.Name.Contains("Tab", StringComparison.OrdinalIgnoreCase));
        // A new session (a new plugin instance) starts on Scene here.
        Assert.Equal(MainTab.SceneHere, new MainWindowNavigation(new Selection().Select).Current);
    }

    // The UI enums are internal, so theories take their names (a public test
    // method cannot have an internal parameter type).
    [Theory]
    [InlineData("SceneHere", "FurnitureAndLights")]
    [InlineData("FurnitureAndLights", "Actors")]
    [InlineData("Actors", "FurnitureAndLights")]
    [InlineData("Actors", "Appearances")]
    [InlineData("FurnitureAndLights", "Settings")]
    [InlineData("Settings", "SceneHere")]
    public void EveryTabChangeClearsTheSelection(string fromName, string toName)
    {
        var from = Enum.Parse<MainTab>(fromName);
        var to = Enum.Parse<MainTab>(toName);
        var selection = new Selection();
        var navigation = new MainWindowNavigation(selection.Select);
        navigation.ObserveActive(from);
        selection.Calls.Clear();

        Assert.False(navigation.ObserveActive(from));
        Assert.Empty(selection.Calls);

        Assert.True(navigation.ObserveActive(to));
        Assert.Equal<RscObjectId?>([null], selection.Calls);
    }

    [Fact]
    public void ClosingTheWindowClearsTheSelection()
    {
        var selection = new Selection();
        var navigation = new MainWindowNavigation(selection.Select);

        navigation.Closed();

        Assert.Equal<RscObjectId?>([null], selection.Calls);
    }

    [Theory]
    [InlineData("FurnitureAndLights")]
    [InlineData("Actors")]
    public void ASceneHereRowSwitchesTabFirstThenSelectsTheObject(string tabName)
    {
        var tab = Enum.Parse<MainTab>(tabName);
        var selection = new Selection();
        var navigation = new MainWindowNavigation(selection.Select);
        var chosen = new RscObjectId(Guid.NewGuid());

        navigation.RequestSelect(tab, chosen);
        Assert.Equal(tab, navigation.Requested);
        // Nothing is selected while Scene here is still shown.
        Assert.False(navigation.ObserveActive(MainTab.SceneHere));
        Assert.Empty(selection.Calls);

        Assert.True(navigation.ObserveActive(tab));
        Assert.Equal<RscObjectId?>([null, chosen], selection.Calls);
        Assert.Null(navigation.Requested);

        // Applied once only.
        Assert.False(navigation.ObserveActive(tab));
        Assert.Equal(2, selection.Calls.Count);
    }

    [Fact]
    public void ASceneHereSelectionIsDroppedWhenTheWindowClosesOrAnotherTabIsAsked()
    {
        var selection = new Selection();
        var navigation = new MainWindowNavigation(selection.Select);
        var chosen = new RscObjectId(Guid.NewGuid());

        navigation.RequestSelect(MainTab.Actors, chosen);
        navigation.Closed();
        Assert.Null(navigation.Requested);
        navigation.ObserveActive(MainTab.Actors);
        Assert.DoesNotContain((RscObjectId?)chosen, selection.Calls);

        navigation.RequestSelect(MainTab.FurnitureAndLights, chosen);
        navigation.Request(MainTab.Settings);
        navigation.ObserveActive(MainTab.Settings);
        navigation.ObserveActive(MainTab.FurnitureAndLights);
        Assert.DoesNotContain((RscObjectId?)chosen, selection.Calls);
    }

    [Theory]
    [InlineData("SceneHere", false)]
    [InlineData("FurnitureAndLights", true)]
    [InlineData("Actors", true)]
    [InlineData("Appearances", false)]
    [InlineData("Settings", false)]
    public void TheGizmoIsDrawnOnlyOnFurnitureAndActorsWhileTheWindowIsOpen(string tabName, bool shown)
    {
        var navigation = new MainWindowNavigation(new Selection().Select);
        navigation.ObserveActive(Enum.Parse<MainTab>(tabName));

        Assert.Equal(shown, navigation.GizmoVisible(windowOpen: true));
        Assert.False(navigation.GizmoVisible(windowOpen: false));
    }

    // ---- Banner ------------------------------------------------------------

    [Theory]
    [InlineData(ServerConnectionState.NotConfigured, "OpenSettings")]
    [InlineData(ServerConnectionState.SignInRefused, "Reconnect")]
    [InlineData(ServerConnectionState.Disabled, "Connect")]
    [InlineData(ServerConnectionState.Unreachable, "Reconnect")]
    [InlineData(ServerConnectionState.Stopped, "Reconnect")]
    public void AConnectionBannerShowsTheDetailWithOneActionAndOutranksTheLocation(
        ServerConnectionState state, string actionName)
    {
        var action = Enum.Parse<BannerAction>(actionName);
        foreach (var locationKnown in new[] { true, false })
        {
            var banner = MainBanner.For(new ServerConnectionStatus(state, "the detail"), locationKnown);

            Assert.NotNull(banner);
            Assert.Equal("the detail", banner.Text);
            Assert.Equal(action, banner.Action);
            Assert.NotEmpty(MainBanner.Label(action));
        }
    }

    [Theory]
    [InlineData(ServerConnectionState.WaitingForDependencies)]
    [InlineData(ServerConnectionState.Connecting)]
    [InlineData(ServerConnectionState.Syncing)]
    [InlineData(ServerConnectionState.SyncReady)]
    public void NoConnectionBannerWhileConnectingWaitingOrConnected(ServerConnectionState state)
    {
        Assert.Null(MainBanner.For(new ServerConnectionStatus(state, "the detail"), locationKnown: true));

        var location = MainBanner.For(new ServerConnectionStatus(state, "the detail"), locationKnown: false);
        Assert.NotNull(location);
        Assert.Equal(MainBanner.UnknownLocation, location.Text);
        Assert.Equal(
            "Your location is not known yet, so nothing here is placed or drawn. " +
            "Wait a moment after arriving; if it stays, change area and come back.",
            location.Text);
        Assert.Equal(BannerAction.None, location.Action);
        Assert.Empty(MainBanner.Label(BannerAction.None));
    }

    [Fact]
    public void ABannerAppearsExactlyWhenTheConnectionOrTheLocationNeedsAnAction()
    {
        var connectionBanners = new[]
        {
            ServerConnectionState.NotConfigured, ServerConnectionState.SignInRefused, ServerConnectionState.Disabled,
            ServerConnectionState.Unreachable, ServerConnectionState.Stopped,
        };
        foreach (var state in Enum.GetValues<ServerConnectionState>())
        foreach (var locationKnown in new[] { true, false })
        {
            var banner = MainBanner.For(new ServerConnectionStatus(state, "detail"), locationKnown);
            Assert.Equal(connectionBanners.Contains(state) || !locationKnown, banner is not null);
            if (banner is not null)
                Assert.False(string.IsNullOrWhiteSpace(banner.Text));
        }
    }

    // ---- Status strip ------------------------------------------------------

    [Theory]
    [InlineData(ServerConnectionState.Disabled, "Off")]
    [InlineData(ServerConnectionState.NotConfigured, "Needs settings")]
    [InlineData(ServerConnectionState.WaitingForDependencies, "Waiting")]
    [InlineData(ServerConnectionState.Connecting, "Connecting")]
    [InlineData(ServerConnectionState.Syncing, "Connected")]
    [InlineData(ServerConnectionState.SyncReady, "Connected")]
    [InlineData(ServerConnectionState.SignInRefused, "Sign-in refused")]
    [InlineData(ServerConnectionState.Unreachable, "Server unreachable")]
    [InlineData(ServerConnectionState.Stopped, "Stopped")]
    public void EveryStateHasItsStripName(ServerConnectionState state, string name) =>
        Assert.Equal(name, StatusStrip.StateName(state));

    [Fact]
    public void EveryStateIsNamedAndAnUnknownValueNeverShowsAnEnumName()
    {
        Assert.Equal(9, Enum.GetValues<ServerConnectionState>().Length);
        Assert.Equal("Not connected", StatusStrip.StateName((ServerConnectionState)99));
        Assert.Null(StatusStrip.SyncName((ServerConnectionState)99));
    }

    [Theory]
    [InlineData(ServerConnectionState.SyncReady, "Up to date")]
    [InlineData(ServerConnectionState.Syncing, "Catching up")]
    [InlineData(ServerConnectionState.Connecting, null)]
    [InlineData(ServerConnectionState.Disabled, null)]
    public void TheSyncStateShowsOnlyWhileConnected(ServerConnectionState state, string? name) =>
        Assert.Equal(name, StatusStrip.SyncName(state));

    [Theory]
    [InlineData("wss://rsc.example.test/rsc", "rsc.example.test")]
    [InlineData("wss://user:secret@rsc.example.test:8443/path?key=abc#frag", "rsc.example.test")]
    [InlineData("  ws://127.0.0.1:5000  ", "127.0.0.1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("not a url", null)]
    public void TheStripShowsTheServerHostOnly(string? endpoint, string? host)
    {
        var shown = StatusStrip.Host(endpoint);

        Assert.Equal(host, shown);
        foreach (var hidden in new[] { "user", "secret", "8443", "path", "key", "abc", "frag", "wss" })
            Assert.DoesNotContain(hidden, shown ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecondLineShowsTheRetryCountdownOrAConnectingDetailOnly()
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("Retrying in 12 s.", StatusStrip.SecondLine(
            new ServerConnectionStatus(ServerConnectionState.Unreachable, "x", RetryAt: now.AddSeconds(11.2)), now));
        Assert.Null(StatusStrip.SecondLine(
            new ServerConnectionStatus(ServerConnectionState.Unreachable, "x", RetryAt: now.AddSeconds(-1)), now));
        Assert.Equal("Trying once more.", StatusStrip.SecondLine(
            new ServerConnectionStatus(ServerConnectionState.Connecting, "Trying once more."), now));
        Assert.Equal("Waiting for required plugins.", StatusStrip.SecondLine(
            new ServerConnectionStatus(ServerConnectionState.WaitingForDependencies, "Waiting for required plugins."), now));
        // The banner carries these; the strip does not repeat them.
        Assert.Null(StatusStrip.SecondLine(new ServerConnectionStatus(ServerConnectionState.SignInRefused, "refused"), now));
        Assert.Null(StatusStrip.SecondLine(new ServerConnectionStatus(ServerConnectionState.Disabled, "off"), now));
        Assert.Null(StatusStrip.SecondLine(new ServerConnectionStatus(ServerConnectionState.SyncReady, "ready"), now));
    }

    [Fact]
    public void PlaceNamesComeFromTheExistingDescription()
    {
        // Neutral sample place: a synthetic world name and address.
        var plot = new ClientLocation(339, 40, 5, 7, null, 1, Indoors: true) { DistrictTerritoryId = 339 };
        var lines = plot.DescribeLines(new LocationNames("Mist", null, "Example World"));

        Assert.Equal("Mist, Ward 5, main division, plot 7, indoors", PlaceLabels.Short(lines));
        Assert.Equal("Mist, World: Example World, Ward 5, main division, plot 7, indoors", PlaceLabels.Full(lines));
        Assert.DoesNotContain("IDs", PlaceLabels.Full(lines), StringComparison.Ordinal);

        var field = new ClientLocation(128, null, null, null, null, null, Indoors: false)
            .DescribeLines(LocationNames.None);
        Assert.Equal("Zone 128", PlaceLabels.Short(field));
        Assert.Equal("Zone 128", PlaceLabels.Full(field));
    }

    // ---- Scene here --------------------------------------------------------

    [Fact]
    public void ActorOwnershipShowsYoursTheOwnerNameOrItsStableId()
    {
        var here = TestLocations.Default;
        var mine = Actor(Me, here);
        var named = Actor(Other, here);
        var unnamed = Actor(new RscUserId("carol"), here);
        string? NameOf(RscUserId user) => user == Other ? "Bob" : null;

        Assert.Equal("Yours", SceneOwnership.ForActor(mine, Me, NameOf));
        Assert.Equal("Bob (only Bob can change it)", SceneOwnership.ForActor(named, Me, NameOf));
        Assert.Equal("carol (only carol can change it)", SceneOwnership.ForActor(unnamed, Me, NameOf));
        // Not signed in: nothing is "yours".
        Assert.Equal("alice (only alice can change it)", SceneOwnership.ForActor(mine, null, NameOf));
    }

    [Fact]
    public void FurnitureAndLightsAreSharedAndNeverShowACreatorName()
    {
        Assert.Equal("Shared · placed by you", SceneOwnership.ForShared(Me, Me));
        Assert.Equal("Shared · placed by another player", SceneOwnership.ForShared(Other, Me));
        Assert.Equal("Shared", SceneOwnership.ForShared(Other, null));
        Assert.DoesNotContain("bob", SceneOwnership.ForShared(Other, Me), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SceneHereListsOnlyThisPlaceByKindWithTabsAndCounts()
    {
        var here = TestLocations.Default;
        var elsewhere = new SceneLocation(here.TerritoryId + 1, null);
        var chair = Furniture(Me, here);
        var lamp = Light(Other, here);
        var mine = Actor(Me, here);
        var theirs = Actor(Other, here);
        var away = Furniture(Me, elsewhere);
        var unlocated = Furniture(Me, here) with { Location = null };

        var rows = SceneHere.Rows(
            [theirs, away, lamp, mine, chair, unlocated], here, Me,
            _ => "Chair", _ => "Goobbue", user => user == Other ? "Bob" : null);

        Assert.Equal(4, rows.Count);
        Assert.Equal(new SceneHereRow(chair.ObjectId, "Chair", "Furniture", "Shared · placed by you", MainTab.FurnitureAndLights), rows[0]);
        Assert.Equal(new SceneHereRow(lamp.ObjectId, "Point light", "Light", "Shared · placed by another player", MainTab.FurnitureAndLights), rows[1]);
        Assert.Equal(
            new[] { mine.ObjectId, theirs.ObjectId }.OrderBy(id => id.Value),
            rows.Skip(2).Select(row => row.ObjectId));
        Assert.All(rows.Skip(2), row => Assert.Equal(MainTab.Actors, row.Tab));
        Assert.Contains(rows, row => row.ObjectId == mine.ObjectId && row.Ownership == "Yours");
        Assert.Contains(rows, row => row.ObjectId == theirs.ObjectId && row.Ownership == "Bob (only Bob can change it)");

        Assert.Equal("Furniture 1 · Lights 1 · Actors 2 of 8", SceneHere.Counts(rows, 8));
        Assert.Equal("Furniture 0 · Lights 0 · Actors 0 of 8", SceneHere.Counts([], 8));
    }

    [Fact]
    public void RetryIsOfferedWhenAnyReportHasFailed()
    {
        Assert.False(SceneHere.OffersRetry([]));
        Assert.False(SceneHere.OffersRetry([Report(LocalObjectReportKind.NotSupported), Report(LocalObjectReportKind.Retrying)]));
        Assert.True(SceneHere.OffersRetry([Report(LocalObjectReportKind.Retrying), Report(LocalObjectReportKind.Failed)]));
    }

    [Fact]
    public void NotDrawnActorsAreNamedByCreatureAndOtherReportsByTheirName()
    {
        string? Creature(uint baseId) => baseId == 4242 ? "Goobbue" : null;
        FurnitureUnsupportedReport Named(string identity, string? name) =>
            new(new RscObjectId(Guid.NewGuid()), identity, name, "reason", 1);

        // Actor reports carry the name "Actor"; the creature name is shown instead.
        Assert.Equal("Goobbue", SceneHere.ReportName(Named("bnpc:4242", "Actor"), Creature));
        Assert.Equal("bnpc:7", SceneHere.ReportName(Named("bnpc:7", "Actor"), Creature));
        Assert.Equal("bnpc:x", SceneHere.ReportName(Named("bnpc:x", "Actor"), Creature));
        Assert.Equal("Oak chair", SceneHere.ReportName(Named("furniture:indoor:6000", "Oak chair"), Creature));
        Assert.Equal("furniture:indoor:6000", SceneHere.ReportName(Named("furniture:indoor:6000", null), Creature));
    }

    [Fact]
    public void TheRetryTooltipNamesActorsAndTheTabsLinkToSceneHere()
    {
        Assert.Contains("every furniture item, light and actor at this place", SceneHere.RetryTooltip, StringComparison.Ordinal);
        Assert.Null(SceneHere.NotDrawnLink(0));
        Assert.Equal("2 not drawn here — see Scene here", SceneHere.NotDrawnLink(2));
    }

    [Fact]
    public void AboutGivesThePluginAndProtocolVersionsAndTheTwoPlayerLimit()
    {
        Assert.Equal(2, ProtocolConstants.MaximumGroupMembers);
        Assert.Equal<string>(
            ["Plugin version 1.2.3.4", $"Protocol version {ProtocolConstants.CurrentVersion}", "Supports two players"],
            AboutLines.For(new Version(1, 2, 3, 4)));
        Assert.Equal("Plugin version unknown", AboutLines.For(null)[0]);
    }
}
