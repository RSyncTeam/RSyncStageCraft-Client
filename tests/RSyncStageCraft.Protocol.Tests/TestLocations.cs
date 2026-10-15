using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// Protocol 3 requires every create to name its scene and carry a complete
/// location. Tests that are not about locations use these defaults.
/// </summary>
internal static class TestLocations
{
    /// <summary>An open-world zone (Limsa Lominsa Upper Decks).</summary>
    public static readonly SceneLocation Default = new(128, null);

    public static RscSceneId NewScene() => new(Guid.NewGuid());

    /// <summary>
    /// One fixed scene for helpers that previously created scene-less objects,
    /// so repeated identical payloads stay identical (create replay tests).
    /// </summary>
    public static readonly RscSceneId SharedScene = new(new Guid("2b7e1516-28ae-4d2a-abf7-158809cf4f3c"));
}
