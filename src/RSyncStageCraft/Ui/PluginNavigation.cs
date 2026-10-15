namespace RSyncStageCraft.Ui;

/// <summary>A section of the Settings tab.</summary>
internal enum SettingsSection
{
    Server,
    Plugins,
    LocationDetails,
    About,
}

/// <summary>
/// The main window's navigation: the five tabs, the same in every
/// build, and the Settings sections, drawn exactly in this order.
/// </summary>
internal static class PluginNavigation
{
    internal static IReadOnlyList<(string Label, MainTab Tab)> Tabs { get; } =
    [
        ("Scene here", MainTab.SceneHere),
        ("Furniture & lights", MainTab.FurnitureAndLights),
        ("Actors", MainTab.Actors),
        ("Appearances", MainTab.Appearances),
        ("Settings", MainTab.Settings),
    ];

    internal static IReadOnlyList<(string Label, SettingsSection Section)> SettingsSections { get; } =
    [
        ("Server", SettingsSection.Server),
        ("Plugins", SettingsSection.Plugins),
        ("Location details", SettingsSection.LocationDetails),
        ("About", SettingsSection.About),
    ];
}
