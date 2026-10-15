using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

namespace RSyncStageCraft.Ui;

/// <summary>
/// Draws furniture icons from the game's own icon files through Dalamud's
/// texture provider (local files only; nothing is downloaded). Textures are
/// shared and cached by Dalamud, so this class owns nothing and never
/// disposes a texture. An item without an icon, or whose icon is missing or
/// still loading, gets a neutral placeholder instead.
/// </summary>
internal sealed class FurnitureIcons(ITextureProvider textures)
{
    private readonly ITextureProvider textures = textures ?? throw new ArgumentNullException(nameof(textures));

    /// <summary>A light's list icon: the placeholder frame with a light bulb glyph.</summary>
    internal static void DrawLight(float size) => DrawGlyph(size, FontAwesomeIcon.Lightbulb);

    /// <summary>Draws a square icon of the given size at the cursor, as one item.</summary>
    internal void Draw(uint iconId, float size)
    {
        if (TryGet(iconId) is { } texture)
        {
            ImGui.Image(texture.Handle, new Vector2(size));
            return;
        }
        DrawGlyph(size, FontAwesomeIcon.Couch);
    }

    private static void DrawGlyph(float size, FontAwesomeIcon icon)
    {
        var extent = new Vector2(size);
        var min = ImGui.GetCursorScreenPos();
        ImGui.Dummy(extent);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + extent, ImGui.GetColorU32(ImGuiCol.FrameBg), 4f);
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var glyph = icon.ToIconString();
        var glyphSize = ImGui.CalcTextSize(glyph);
        draw.AddText(min + (extent - glyphSize) / 2, ImGui.GetColorU32(ImGuiCol.TextDisabled), glyph);
    }

    private IDalamudTextureWrap? TryGet(uint iconId)
    {
        if (iconId == 0)
            return null;
        try
        {
            return textures.TryGetFromGameIcon(new GameIconLookup(iconId), out var shared) &&
                shared.TryGetWrap(out var texture, out _)
                ? texture
                : null;
        }
        catch
        {
            // A broken icon file only costs the picture, never the window.
            return null;
        }
    }
}
