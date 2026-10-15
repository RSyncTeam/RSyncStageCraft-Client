using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Dependencies;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Ui;

/// <summary>The main window's tabs, in the order they are drawn.</summary>
internal enum MainTab
{
    SceneHere,
    FurnitureAndLights,
    Actors,
    Appearances,
    Settings,
}

/// <summary>
/// Which tab the main window shows. It has no ImGui calls and is
/// tested directly.
/// <list type="bullet">
/// <item>Dalamud "Open" shows the last tab and "Settings" shows Settings.
/// The last tab is held for the session only and never saved.</item>
/// <item>Every tab change clears the gizmo selection, which ends any drag.
/// A Scene here row first switches to the
/// object's tab, then selects the object.</item>
/// <item>The gizmo overlay is drawn only on Furniture &amp; lights and Actors,
/// while the main window is open.</item>
/// </list>
/// </summary>
internal sealed class MainWindowNavigation
{
    private readonly Action<RscObjectId?> select;
    private MainTab? requestedTab;
    private RscObjectId? requestedObject;

    /// <param name="select">The gizmo selection (<c>FurnitureGizmoController.Select</c>).</param>
    internal MainWindowNavigation(Action<RscObjectId?> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        this.select = select;
    }

    /// <summary>The tab shown now (Scene here until another is shown).</summary>
    internal MainTab Current { get; private set; } = MainTab.SceneHere;

    /// <summary>A tab the window must switch to on its next frame, if any.</summary>
    internal MainTab? Requested => requestedTab;

    /// <summary>Dalamud "Settings": show the Settings tab.</summary>
    internal void OpenSettings() => Request(MainTab.Settings);

    /// <summary>Switches to a tab on the next frame. A pending object selection is dropped.</summary>
    internal void Request(MainTab tab)
    {
        requestedTab = tab;
        requestedObject = null;
    }

    /// <summary>A Scene here row: switch to the object's tab, then select it.</summary>
    internal void RequestSelect(MainTab tab, RscObjectId objectId)
    {
        requestedTab = tab;
        requestedObject = objectId;
    }

    /// <summary>
    /// Reports the tab the tab bar drew as active this frame. A change clears
    /// the selection; then a pending Scene here selection for that tab is
    /// applied. Returns true when the shown tab changed.
    /// </summary>
    internal bool ObserveActive(MainTab active)
    {
        var changed = active != Current;
        if (changed)
        {
            Current = active;
            select(null);
        }
        if (requestedTab == active)
        {
            requestedTab = null;
            if (requestedObject is { } chosen)
            {
                requestedObject = null;
                select(chosen);
            }
        }
        return changed;
    }

    /// <summary>Closing the main window does what closing the Furniture or Actors window did: it clears the selection.</summary>
    internal void Closed()
    {
        requestedTab = null;
        requestedObject = null;
        select(null);
    }

    /// <summary>The gizmo is drawn only on Furniture &amp; lights and Actors, while the window is open.</summary>
    internal bool GizmoVisible(bool windowOpen) =>
        windowOpen && Current is MainTab.FurnitureAndLights or MainTab.Actors;
}

/// <summary>The one action a banner offers.</summary>
internal enum BannerAction
{
    None,
    OpenSettings,
    Connect,
    Reconnect,
}

/// <summary>The single banner under the status strip, when something needs doing.</summary>
internal sealed record MainBanner(string Text, BannerAction Action)
{
    /// <summary>The unknown-location banner.</summary>
    internal const string UnknownLocation =
        "Your location is not known yet, so nothing here is placed or drawn. " +
        "Wait a moment after arriving; if it stays, change area and come back.";

    /// <summary>
    /// At most one banner, by priority: needs settings, sign-in refused, not
    /// connected (off, unreachable or stopped), unknown location. No banner
    /// is shown for a missing plugin: no plugin is needed to connect, place or
    /// draw. The texts are the connection's existing details.
    /// </summary>
    internal static MainBanner? For(ServerConnectionStatus status, bool locationKnown)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            ServerConnectionState.NotConfigured => new(status.Detail, BannerAction.OpenSettings),
            ServerConnectionState.SignInRefused => new(status.Detail, BannerAction.Reconnect),
            ServerConnectionState.Disabled => new(status.Detail, BannerAction.Connect),
            ServerConnectionState.Unreachable or ServerConnectionState.Stopped =>
                new(status.Detail, BannerAction.Reconnect),
            _ when !locationKnown => new(UnknownLocation, BannerAction.None),
            _ => null,
        };
    }

    internal static string Label(BannerAction action) => action switch
    {
        BannerAction.OpenSettings => "Settings",
        BannerAction.Connect => "Connect",
        BannerAction.Reconnect => "Reconnect",
        _ => string.Empty,
    };
}

/// <summary>The status strip above the tabs. Built from local data only.</summary>
internal static class StatusStrip
{
    /// <summary>The state name. Never the enum name of an unknown value.</summary>
    internal static string StateName(ServerConnectionState state) => state switch
    {
        ServerConnectionState.Disabled => "Off",
        ServerConnectionState.NotConfigured => "Needs settings",
        ServerConnectionState.WaitingForDependencies => "Waiting",
        ServerConnectionState.Connecting => "Connecting",
        ServerConnectionState.Syncing or ServerConnectionState.SyncReady => "Connected",
        ServerConnectionState.SignInRefused => "Sign-in refused",
        ServerConnectionState.Unreachable => "Server unreachable",
        ServerConnectionState.Stopped => "Stopped",
        _ => "Not connected",
    };

    /// <summary>"Up to date" or "Catching up" while connected; otherwise none.</summary>
    internal static string? SyncName(ServerConnectionState state) => state switch
    {
        ServerConnectionState.SyncReady => "Up to date",
        ServerConnectionState.Syncing => "Catching up",
        _ => null,
    };

    /// <summary>
    /// The server's host name only: no scheme, port, path, query or user
    /// information. None when the address is empty or not a URL (the strip
    /// then leaves it out; Settings shows the address as typed).
    /// </summary>
    internal static string? Host(string? endpoint) =>
        Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.IdnHost
            : null;

    /// <summary>
    /// The second line, only when there is something to say: the retry
    /// countdown, or the detail of a state no banner covers (connecting or
    /// waiting). A sign-in refusal is in the banner directly below.
    /// </summary>
    internal static string? SecondLine(ServerConnectionStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.RetryAt is { } retryAt && retryAt - now > TimeSpan.Zero)
            return $"Retrying in {Math.Ceiling((retryAt - now).TotalSeconds):0} s.";
        return status.State is ServerConnectionState.Connecting or ServerConnectionState.WaitingForDependencies &&
               !string.IsNullOrWhiteSpace(status.Detail)
            ? status.Detail
            : null;
    }
}

/// <summary>Place names from the existing location description (<c>ClientLocation.DescribeLines</c>).</summary>
internal static class PlaceLabels
{
    private const string WorldPrefix = "World: ";
    private const string IdsPrefix = "IDs: ";

    /// <summary>The short name for the status strip: the place without the world and the raw IDs.</summary>
    internal static string Short(IReadOnlyList<string> lines) =>
        string.Join(", ", lines.Where(line =>
            !line.StartsWith(WorldPrefix, StringComparison.Ordinal) &&
            !line.StartsWith(IdsPrefix, StringComparison.Ordinal)));

    /// <summary>The full description for the Scene here heading: everything but the raw IDs.</summary>
    internal static string Full(IReadOnlyList<string> lines) =>
        string.Join(", ", lines.Where(line => !line.StartsWith(IdsPrefix, StringComparison.Ordinal)));
}

/// <summary>
/// The Scene here ownership column. Only data the client already
/// has: actors carry an owner ID with an optional display name; furniture
/// and lights carry only a creator ID and any member may edit them, so no
/// name is ever shown for them.
/// </summary>
internal static class SceneOwnership
{
    internal static string ForActor(ActorState actor, RscUserId? self, Func<RscUserId, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (ActorOwnership.IsEditableBy(actor, self))
            return "Yours";
        var label = ActorOwnership.OwnerLabel(actor, nameOf);
        return $"{label} (only {label} can change it)";
    }

    internal static string ForShared(RscUserId creator, RscUserId? self) =>
        self is not { } me || !me.IsValid
            ? "Shared"
            : creator == me
                ? "Shared · placed by you"
                : "Shared · placed by another player";
}

/// <summary>One Scene here row.</summary>
internal sealed record SceneHereRow(RscObjectId ObjectId, string Name, string Kind, string Ownership, MainTab Tab);

/// <summary>The Scene here overview, from the shared state at this place.</summary>
internal static class SceneHere
{
    /// <summary>The corrected "Retry all scene objects" tooltip: it redraws actors too.</summary>
    internal const string RetryTooltip =
        "Releases and redraws every furniture item, light and actor at this place, not only the failed ones.\n" +
        "The whole scene may briefly disappear and redraw.";

    internal const string FurnitureKind = "Furniture";
    internal const string LightKind = "Light";
    internal const string ActorKind = "Actor";

    /// <summary>Furniture, then lights, then actors, each in object order.</summary>
    internal static IReadOnlyList<SceneHereRow> Rows(
        IEnumerable<AuthoritativeObjectState> objects,
        SceneLocation place,
        RscUserId? self,
        Func<FurnitureState, string> furnitureName,
        Func<ActorState, string> actorName,
        Func<RscUserId, string?> ownerName) =>
        objects
            .Where(state => state.Location == place && state is FurnitureState or LightState or ActorState)
            .OrderBy(state => state switch { FurnitureState => 0, LightState => 1, _ => 2 })
            .ThenBy(state => state.ObjectId.Value)
            .Select(state => state switch
            {
                FurnitureState furniture => new SceneHereRow(furniture.ObjectId, furnitureName(furniture), FurnitureKind,
                    SceneOwnership.ForShared(furniture.CreatorId, self), MainTab.FurnitureAndLights),
                LightState light => new SceneHereRow(light.ObjectId, "Point light", LightKind,
                    SceneOwnership.ForShared(light.CreatorId, self), MainTab.FurnitureAndLights),
                ActorState actor => new SceneHereRow(actor.ObjectId, actorName(actor), ActorKind,
                    SceneOwnership.ForActor(actor, self, ownerName), MainTab.Actors),
                _ => throw new InvalidOperationException(),
            })
            .ToArray();

    /// <summary>"Furniture n · Lights n · Actors n of max".</summary>
    internal static string Counts(IReadOnlyList<SceneHereRow> rows, int maximumActors) =>
        $"Furniture {rows.Count(row => row.Kind == FurnitureKind)} · " +
        $"Lights {rows.Count(row => row.Kind == LightKind)} · " +
        $"Actors {rows.Count(row => row.Kind == ActorKind)} of {maximumActors}";

    /// <summary>Retry is offered when any furniture, light or actor report has failed.</summary>
    internal static bool OffersRetry(IReadOnlyList<FurnitureUnsupportedReport> reports) =>
        reports.Any(report => report.Kind == LocalObjectReportKind.Failed);

    /// <summary>
    /// A not-drawn report's name. An actor report (identity "bnpc:&lt;row&gt;")
    /// is named by its creature, as the Actors window did; any other report
    /// by its name, as the Furniture window did.
    /// </summary>
    internal static string ReportName(FurnitureUnsupportedReport report, Func<uint, string?> creatureName)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.Identity.StartsWith("bnpc:", StringComparison.Ordinal))
            return report.Name ?? report.Identity;
        return uint.TryParse(report.Identity.AsSpan(5), out var baseId) && creatureName(baseId) is { } name
            ? name
            : report.Identity;
    }

    /// <summary>The one line a Furniture &amp; lights or Actors tab keeps; none when nothing is listed.</summary>
    internal static string? NotDrawnLink(int count) =>
        count > 0 ? $"{count} not drawn here — see Scene here" : null;
}

/// <summary>
/// Why a Place button is disabled, shown as its tooltip.
/// None when placing is possible. The first reason that applies wins.
/// </summary>
internal static class PlaceBlock
{
    internal const string ConnectFirst = "Connect first.";
    internal const string LocationUnknown = "Your location is not known here.";
    internal const string WaitForMove = "Wait until the current move is saved.";

    /// <param name="unsupported">Why the selected item cannot be shared, or null when it can.</param>
    internal static string? Reason(bool connected, bool locationKnown, string? unsupported, bool canEdit)
    {
        if (!connected)
            return ConnectFirst;
        if (!locationKnown)
            return LocationUnknown;
        if (unsupported is not null)
            return $"This item cannot be shared: {unsupported.TrimEnd('.')}.";
        return canEdit ? null : WaitForMove;
    }
}

/// <summary>
/// Actors tab notices, built from the flags.
/// <c>ActorSuppression.Describe()</c> is unchanged and goes to the log.
/// </summary>
internal static class ActorNotices
{
    internal const string HiddenText = "Actors are hidden on this client during GPose and cutscenes.";

    internal const string HiddenUncheckedText =
        "Actors are hidden on this client because RSC could not check for GPose or cutscenes; they reappear when it can.";

    internal static string? Hidden(Core.Actors.ActorSuppression suppression) => !suppression.Suppressed
        ? null
        : suppression.ReadFailure is not null
            ? HiddenUncheckedText
            : HiddenText;

    internal static string? LeftUntouched(int count) => count > 0
        ? $"{count} object(s) were left untouched for safety; they disappear when you change area."
        : null;
}

/// <summary>
/// The plugin lines, built from the health flags: what
/// is missing, what that disables, and the one action. A probe failure is
/// recognised by the existing <c>FailureReason</c> prefix; the failure text
/// itself goes to the log, never to the player.
/// </summary>
internal static class PluginLines
{
    internal const string Penumbra = "Penumbra";
    internal const string Glamourer = "Glamourer";
    internal const string ProbeFailurePrefix = "Dependency health probe failed: ";

    internal static bool Ready(DependencyHealth health) => health.Available && health.Compatible;

    /// <summary>The line for one required plugin.</summary>
    internal static string Line(DependencyHealth health)
    {
        var name = health.Name;
        if (Ready(health))
            return $"{name}: ready";
        if (health.FailureReason?.StartsWith(ProbeFailurePrefix, StringComparison.Ordinal) == true)
            return $"{name}'s status could not be read. Press Refresh plugin status.{Effects(name)}";
        if (health.Available)
            return $"{name} is a version RSC cannot use. Update {name}, then press Refresh plugin status.{Effects(name)}";
        return name switch
        {
            Penumbra => "Penumbra is not available. Capturing your current character is disabled; " +
                        "saved Glamourer designs can still be captured. Install or enable Penumbra, then press Refresh plugin status.",
            Glamourer => "Glamourer is not available. Capturing appearances and Make a copy are disabled. " +
                         "Install or enable Glamourer, then press Refresh plugin status.",
            _ => $"{name} is not available. Install or enable {name}, then press Refresh plugin status.",
        };
    }

    /// <summary>The optional-plugin line (Customize+).</summary>
    internal static string Optional(string displayName) => $"{displayName}: optional; not used by RSC.";

    /// <summary>What a missing plugin disables (no plugin is needed to connect, place or draw).</summary>
    private static string Effects(string name) => name switch
    {
        Penumbra => " Capturing your current character is disabled; saved Glamourer designs can still be captured.",
        Glamourer => " Capturing appearances and Make a copy are disabled.",
        _ => string.Empty,
    };
}

/// <summary>
/// Which Appearances actions the plugins allow.
/// Penumbra is needed only to capture the current character; Glamourer for
/// every capture (current character and saved designs) and for Make a copy.
/// A button is disabled while its plugin is unavailable; the capture reader
/// and the copier still refuse on their own if a plugin goes away between
/// the frame that showed the button and the click (the existing backstops).
/// </summary>
internal sealed record AppearanceGate(bool PenumbraReady, bool GlamourerReady, IReadOnlyList<string> MissingLines)
{
    /// <summary>"Capture my current character".</summary>
    internal bool CanCaptureCurrent => PenumbraReady && GlamourerReady;

    /// <summary>"Load saved Glamourer designs" and "Capture design (state only)".</summary>
    internal bool CanUseSavedDesigns => GlamourerReady;

    /// <summary>"Make a copy" and "Create the copy".</summary>
    internal bool CanCopy => GlamourerReady;

    /// <summary>From the last plugin status observation. A plugin with no health entry counts as unavailable.</summary>
    internal static AppearanceGate From(IReadOnlyList<DependencyHealth> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var penumbra = dependencies.FirstOrDefault(health => health.Name == PluginLines.Penumbra);
        var glamourer = dependencies.FirstOrDefault(health => health.Name == PluginLines.Glamourer);
        var missing = new List<string>(2);
        foreach (var (name, health) in new[] { (PluginLines.Penumbra, penumbra), (PluginLines.Glamourer, glamourer) })
        {
            if (health.Name is null)
                missing.Add(PluginLines.Line(new DependencyHealth(name, false, false)));
            else if (!PluginLines.Ready(health))
                missing.Add(PluginLines.Line(health));
        }
        return new(penumbra.Name is not null && PluginLines.Ready(penumbra),
            glamourer.Name is not null && PluginLines.Ready(glamourer),
            missing);
    }
}

/// <summary>
/// The catch-all text for a failed appearance operation.
/// Neither text promises that nothing changed: an upload, rename or copy may
/// have taken effect. A copy's result is in Glamourer, not in the list.
/// </summary>
internal static class AppearanceOperationText
{
    internal const string FailedText =
        "Something went wrong (details in the log). Refresh the list to see the current state, then try again.";

    internal const string CopyFailedText =
        "Something went wrong (details in the log). Check Glamourer for the copy before trying again.";

    internal static string Failed(bool copy) => copy ? CopyFailedText : FailedText;

    /// <summary>
    /// When closing the window discards an
    /// un-uploaded capture, the message that capture set (its review or
    /// refusal text) is cleared. Any other message, set after it, stays.
    /// </summary>
    internal static string? AfterCloseDiscard(string? message, string? captureMessage) =>
        captureMessage is not null && ReferenceEquals(message, captureMessage) ? null : message;
}

/// <summary>Settings → About.</summary>
internal static class AboutLines
{
    internal static IReadOnlyList<string> For(Version? pluginVersion) =>
    [
        $"Plugin version {pluginVersion?.ToString() ?? "unknown"}",
        $"Protocol version {ProtocolConstants.CurrentVersion}",
        // ProtocolConstants.MaximumGroupMembers is 2; a test keeps the two together.
        "Supports two players",
    ];
}
