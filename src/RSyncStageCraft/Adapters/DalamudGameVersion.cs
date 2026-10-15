namespace RSyncStageCraft.Adapters;

/// <summary>
/// This client's game version string (e.g. "2026.09.15.0000.0000"), read
/// locally from ClientStructs' <c>Framework.GameVersionString</c>. Used only to
/// match the curated animation allowlist; never sent anywhere.
/// </summary>
internal static unsafe class DalamudGameVersion
{
    public static string? Read()
    {
        try
        {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (framework == null)
                return null;
            var text = framework->GameVersionString;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
