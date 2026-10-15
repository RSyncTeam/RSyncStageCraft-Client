namespace RSyncStageCraft;

/// <summary>
/// The compile-time development switch. Debug builds define
/// RSC_DEVELOPMENT; Release builds never do, so a Release artifact has no
/// development diagnostics even when Dalamud loads it as a dev plugin.
/// </summary>
internal static class DevelopmentBuild
{
#if RSC_DEVELOPMENT
    internal const bool Enabled = true;
#else
    internal const bool Enabled = false;
#endif

    /// <summary>Development diagnostics: a Debug build loaded as a dev plugin, nothing else.</summary>
    internal static bool ToolsEnabled(bool isDevPlugin) => Enabled && isDevPlugin;
}
