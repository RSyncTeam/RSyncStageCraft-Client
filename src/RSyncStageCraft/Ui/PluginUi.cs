using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Dependencies;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Diagnostics;
using RSyncStageCraft.Core.Location;

namespace RSyncStageCraft.Ui;

internal sealed class PluginUi : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IUiBuilder uiBuilder;
    private readonly WindowSystem windows = new("RSyncStageCraft");
    private readonly PluginMainWindow mainWindow;
    private readonly MainWindowNavigation navigation;
    private readonly FurnitureGizmoOverlay gizmoOverlay;
    private readonly FurnitureLoadingIndicator loadingIndicator;
    private readonly DependencyChangeRefreshGate dependencyChanges;
    private readonly PluginConnection connection;
    private int disposed;

    internal PluginUi(
        IDalamudPluginInterface pluginInterface,
        DependencyRegistry dependencies,
        RuntimeObservationTrace observations,
        PluginConnection connection,
        ITextureProvider textures,
        Func<ClientPlace?> location,
        Func<PlayerPose?> pose,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(pose);

        this.pluginInterface = pluginInterface;
        this.connection = connection;
        uiBuilder = pluginInterface.UiBuilder;
        var status = new PluginStatusModel(
            dependencies,
            observations,
            () => DalamudOptionalHostMetadataProbe.Observe(pluginInterface),
            log);
        navigation = new MainWindowNavigation(connection.Gizmo.Select);
        mainWindow = new PluginMainWindow(
            status,
            connection,
            location,
            navigation,
            new SceneHereTab(connection, location, navigation),
            new FurnitureWindow(connection, new FurnitureIcons(textures), location, pose),
            new ActorWindow(connection, location, pose),
            new AppearanceWindow(connection, () =>
            {
                // The buttons follow the last plugin status observation
                // (refreshed on open, on plugin changes and on request).
                if (!status.HasObservation)
                    status.Refresh();
                return AppearanceGate.From(status.Dependencies);
            }, log));
        gizmoOverlay = new FurnitureGizmoOverlay(connection, () => navigation.GizmoVisible(mainWindow.IsOpen));
        loadingIndicator = new FurnitureLoadingIndicator(connection, location);
        dependencyChanges = new DependencyChangeRefreshGate(observations);
        windows.AddWindow(mainWindow);

        try
        {
            uiBuilder.Draw += Draw;
            uiBuilder.OpenMainUi += OpenMain;
            uiBuilder.OpenConfigUi += OpenConfig;
            pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        }
        catch
        {
            pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
            uiBuilder.OpenConfigUi -= OpenConfig;
            uiBuilder.OpenMainUi -= OpenMain;
            uiBuilder.Draw -= Draw;
            windows.RemoveAllWindows();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        // Close UI admission before the observational dependency adapters are
        // disposed. No later draw can probe a registry whose lifetime ended.
        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        uiBuilder.OpenConfigUi -= OpenConfig;
        uiBuilder.OpenMainUi -= OpenMain;
        uiBuilder.Draw -= Draw;
        windows.RemoveAllWindows();
    }

    private void Draw()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        if (dependencyChanges.TryConsume())
            mainWindow.Refresh();
        try
        {
            gizmoOverlay.Draw();
        }
        catch (Exception)
        {
            // The overlay must never take the plugin's windows down with it.
            connection.Gizmo.Cancel("The gizmo hit an error; the move was discarded.");
        }
        try
        {
            loadingIndicator.Draw();
        }
        catch (Exception)
        {
            // Display only; never take the plugin's windows down with it.
        }
        windows.Draw();
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        try
        {
            dependencyChanges.Observe(
                args.Kind.ToString(),
                args.AffectedInternalNames);
        }
        catch
        {
            dependencyChanges.ObserveUnreadable();
        }
        connection.DependenciesChanged();
    }

    /// <summary>Dalamud "Open": the main window on its last tab.</summary>
    private void OpenMain()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        // Loading the plugin remains probe-free. Opening the main window or
        // explicitly refreshing the plugin status is the user-owned
        // observation point.
        mainWindow.Refresh();
        mainWindow.IsOpen = true;
        mainWindow.BringToFront();
    }

    /// <summary>Dalamud "Settings": the main window on the Settings tab.</summary>
    private void OpenConfig()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        navigation.OpenSettings();
        OpenMain();
    }

    /// <summary>
    /// The one main window: the status strip, at most one banner, and
    /// the five tabs (<see cref="PluginNavigation"/>). It keeps the old status
    /// window's ImGui ID, so its saved position and size still apply.
    /// </summary>
    private sealed class PluginMainWindow : Window
    {
        private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
        private static readonly Vector4 Waiting = new(0.95f, 0.8f, 0.35f, 1f);
        private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.4f, 1f);
        private readonly PluginStatusModel status;
        private readonly PluginConnection connection;
        private readonly Func<ClientPlace?> location;
        private readonly MainWindowNavigation navigation;
        private readonly SceneHereTab sceneHere;
        private readonly FurnitureWindow furniture;
        private readonly ActorWindow actors;
        private readonly AppearanceWindow appearances;
        private string endpointDraft;
        private string keyDraft = string.Empty;
        private string? saveMessage;
        private bool scrubHovered;

        internal PluginMainWindow(
            PluginStatusModel status,
            PluginConnection connection,
            Func<ClientPlace?> location,
            MainWindowNavigation navigation,
            SceneHereTab sceneHere,
            FurnitureWindow furniture,
            ActorWindow actors,
            AppearanceWindow appearances)
            : base("RSyncStageCraft###RSyncStageCraftStatus")
        {
            this.status = status;
            this.connection = connection;
            this.location = location;
            this.navigation = navigation;
            this.sceneHere = sceneHere;
            this.furniture = furniture;
            this.actors = actors;
            this.appearances = appearances;
            endpointDraft = connection.Configuration.ServerEndpoint;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(560, 600),
                MaximumSize = new Vector2(1400, 1800),
            };
        }

        internal void Refresh() => status.Refresh();

        public override void OnOpen()
        {
            base.OnOpen();
            // Not when "Settings" is about to show another tab.
            if (navigation.Current == MainTab.Appearances && navigation.Requested is null or MainTab.Appearances)
                appearances.Entered();
        }

        /// <summary>
        /// Closing the window clears the gizmo selection (ending any drag) and
        /// does what closing the Appearances window did.
        /// </summary>
        public override void OnClose()
        {
            navigation.Closed();
            appearances.MainWindowClosed();
            base.OnClose();
        }

        public override void Draw()
        {
            appearances.ProcessCompletions();
            var current = connection.Service.Status;
            var here = location();
            DrawStatusStrip(current, here);
            DrawBanner(current, here?.Scene is not null);
            ImGui.Spacing();

            // The mouse wheel steps a hovered scrub field; the tab content must
            // not scroll with it at the same time (applies from the next frame).
            var contentFlags = scrubHovered ? ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None;
            ScrubControls.BeginFrame();
            try
            {
                using var bar = ImRaii.TabBar("##rsc-main-tabs");
                if (!bar.Success)
                    return;
                foreach (var (label, tab) in PluginNavigation.Tabs)
                {
                    var flags = navigation.Requested == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                    using var item = ImRaii.TabItem(label, flags);
                    if (!item.Success)
                        continue;
                    if (navigation.ObserveActive(tab) && tab == MainTab.Appearances)
                        appearances.Entered();
                    using var content = ImRaii.Child("##rsc-tab-content", new Vector2(-1, -1), false, contentFlags);
                    if (content.Success)
                        DrawTab(tab);
                }
            }
            finally
            {
                scrubHovered = ScrubControls.AnyHovered;
            }
        }

        private void DrawTab(MainTab tab)
        {
            switch (tab)
            {
                case MainTab.SceneHere:
                    sceneHere.Draw();
                    break;
                case MainTab.FurnitureAndLights:
                    furniture.Draw();
                    break;
                case MainTab.Actors:
                    actors.Draw();
                    break;
                case MainTab.Appearances:
                    appearances.Draw();
                    break;
                case MainTab.Settings:
                    DrawSettings();
                    break;
            }
        }

        // ---- Status strip and banner -------------------------------------

        private void DrawStatusStrip(ServerConnectionStatus current, ClientPlace? here)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(ColorFor(current.State), FontAwesomeIcon.Circle.ToIconString());
            ImGui.SameLine();
            var parts = new List<string>(4) { StatusStrip.StateName(current.State) };
            if (StatusStrip.Host(connection.Configuration.ServerEndpoint) is { } host)
                parts.Add(host);
            if (StatusStrip.SyncName(current.State) is { } sync)
                parts.Add(sync);
            if (here is not null)
                parts.Add(PlaceLabels.Short(here.Location.DescribeLines(here.Names)));
            ImGui.TextWrapped(string.Join(" · ", parts));
            if (StatusStrip.SecondLine(current, DateTimeOffset.UtcNow) is { } second)
                ImGui.TextDisabled(second);
        }

        private void DrawBanner(ServerConnectionStatus current, bool locationKnown)
        {
            if (MainBanner.For(current, locationKnown) is not { } banner)
                return;
            ImGui.Spacing();
            ImGui.TextColored(banner.Action == BannerAction.None ? Waiting : Bad, banner.Text);
            if (banner.Action == BannerAction.None)
                return;
            ImGui.SameLine();
            if (!ImGui.Button($"{MainBanner.Label(banner.Action)}##rsc-banner"))
                return;
            switch (banner.Action)
            {
                case BannerAction.OpenSettings:
                    navigation.Request(MainTab.Settings);
                    break;
                case BannerAction.Connect:
                    connection.SetEnabled(true);
                    break;
                case BannerAction.Reconnect:
                    connection.Reconnect();
                    break;
            }
        }

        // ---- Settings ------------------------------------------------------

        private void DrawSettings()
        {
            // Exactly the sections in PluginNavigation; Release has no Development section.
            foreach (var (label, section) in PluginNavigation.SettingsSections)
            {
                var open = section is SettingsSection.Server or SettingsSection.About
                    ? ImGuiTreeNodeFlags.DefaultOpen
                    : ImGuiTreeNodeFlags.None;
                if (!ImGui.CollapsingHeader(label, open))
                    continue;
                switch (section)
                {
                    case SettingsSection.Server:
                        DrawServer();
                        break;
                    case SettingsSection.Plugins:
                        DrawDependencies();
                        break;
                    case SettingsSection.LocationDetails:
                        DrawLocation();
                        break;
                    case SettingsSection.About:
                        foreach (var line in AboutLines.For(typeof(PluginUi).Assembly.GetName().Version))
                            ImGui.TextUnformatted(line);
                        break;
                }
                ImGui.Spacing();
            }
        }

        private void DrawServer()
        {
            ImGui.SetNextItemWidth(360);
            ImGui.InputText("Address", ref endpointDraft, 256);
            ImGui.SetNextItemWidth(360);
            ImGui.InputText("Access key", ref keyDraft, 256, ImGuiInputTextFlags.Password);
            ImGui.TextDisabled(connection.HasStoredKey
                ? "A key is saved. Leave the field empty to keep it."
                : "Paste the access key you were given.");
            if (ImGui.Button("Save"))
            {
                saveMessage = connection.SaveSettings(endpointDraft, keyDraft)
                    ? "Saved."
                    : ConnectionText.KeyNotStoredSecurely;
                keyDraft = string.Empty;
            }
            ImGui.SameLine();
            if (connection.Configuration.ConnectEnabled)
            {
                if (ImGui.Button("Disconnect"))
                    connection.SetEnabled(false);
                ImGui.SameLine();
                if (ImGui.Button("Reconnect"))
                    connection.Reconnect();
            }
            else if (ImGui.Button("Connect"))
            {
                connection.SetEnabled(true);
            }
            if (connection.HasStoredKey)
            {
                ImGui.SameLine();
                if (ImGui.Button("Forget key"))
                {
                    saveMessage = connection.ForgetKey()
                        ? "Key removed."
                        : $"The key is forgotten for now, but its saved file could not be deleted and " +
                          $"will load again next time. Close the game and delete {ProtectedAccessKeyStore.FileName} " +
                          "in the plugin's config folder.";
                }
            }
            if (saveMessage is not null)
                ImGui.TextWrapped(saveMessage);

            var current = connection.Service.Status;
            if (current.IsConnected)
                ImGui.TextUnformatted($"Objects held from the server: {current.HeldObjects}");
        }

        private void DrawLocation()
        {
            ImGui.TextUnformatted("Location (read from your game client only)");
            if (location() is not { } here)
            {
                ImGui.TextWrapped("Not available (not logged in).");
                return;
            }
            var lines = here.Location.DescribeLines(here.Names);
            for (var index = 0; index < lines.Count; index++)
            {
                // The last line is the raw IDs, which are what scenes match on.
                if (index == lines.Count - 1)
                    ImGui.TextDisabled(lines[index]);
                else
                    ImGui.TextWrapped(lines[index]);
            }
        }

        private void DrawDependencies()
        {
            if (!status.HasObservation)
                status.Refresh();

            // Built from the flags; the failure text is logged, not shown.
            foreach (var dependency in status.Dependencies)
            {
                ImGui.PushTextWrapPos(0f);
                if (PluginLines.Ready(dependency))
                    ImGui.TextUnformatted(PluginLines.Line(dependency));
                else
                    ImGui.TextColored(Waiting, PluginLines.Line(dependency));
                ImGui.PopTextWrapPos();
                ImGui.TextDisabled(
                    $"  Installed {dependency.InstalledVersion ?? "?"}, API {dependency.ProviderApiVersion ?? "?"}");
            }

            foreach (var host in status.OptionalHosts)
                ImGui.TextUnformatted(PluginLines.Optional(host.DisplayName));
            if (ImGui.Button("Refresh plugin status"))
                status.Refresh();
        }

        private static Vector4 ColorFor(ServerConnectionState state) => state switch
        {
            ServerConnectionState.SyncReady or ServerConnectionState.Syncing => Good,
            ServerConnectionState.SignInRefused or ServerConnectionState.Unreachable or
                ServerConnectionState.NotConfigured => Bad,
            _ => Waiting,
        };
    }
}
