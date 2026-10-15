using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using RSyncStageCraft.Core.Furniture;

namespace RSyncStageCraft.Ui;

/// <summary>
/// Compact, colour-coded numeric scrub fields in the style of Brio's XYZ
/// controls (idea only, no code copied): a coloured bar on the left of each
/// field, horizontal drag to adjust, the mouse wheel over the field to step
/// it, and a double-click (or Ctrl+click) to type an exact value. Alt makes a
/// drag or wheel step finer, Shift coarser. These controls only change the
/// values passed in; they never send anything.
/// </summary>
internal static class ScrubControls
{
    internal static readonly Vector4 XColor = new(0.88f, 0.30f, 0.30f, 1f);
    internal static readonly Vector4 YColor = new(0.38f, 0.78f, 0.34f, 1f);
    internal static readonly Vector4 ZColor = new(0.32f, 0.55f, 0.95f, 1f);
    internal static readonly Vector4 FacingColor = new(0.90f, 0.72f, 0.30f, 1f);

    private const float BarWidth = 3f;
    private const string Help = "Drag sideways or scroll to adjust; double-click to type.\nAlt: finer. Shift: coarser.";

    /// <summary>True while the mouse is over a scrub field (the window should not scroll with the wheel then).</summary>
    internal static bool AnyHovered { get; private set; }

    /// <summary>Call once per frame before drawing scrub fields.</summary>
    internal static void BeginFrame() => AnyHovered = false;

    /// <summary>X, Y and Z in one row. Returns true when any value changed.</summary>
    internal static bool Position(string id, ref float x, ref float y, ref float z, float totalWidth)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(60f, (totalWidth - spacing * 2) / 3);
        var changed = Field($"{id}-x", ref x, XColor, width, FurnitureScrub.PositionStep, FurnitureScrub.PositionStep,
            "%.3f", $"X (yalms)\n{Help}");
        ImGui.SameLine();
        changed |= Field($"{id}-y", ref y, YColor, width, FurnitureScrub.PositionStep, FurnitureScrub.PositionStep,
            "%.3f", $"Y (yalms, height)\n{Help}");
        ImGui.SameLine();
        changed |= Field($"{id}-z", ref z, ZColor, width, FurnitureScrub.PositionStep, FurnitureScrub.PositionStep,
            "%.3f", $"Z (yalms)\n{Help}");
        return changed;
    }

    /// <summary>
    /// Facing in degrees, with left/right chevrons for one-degree steps.
    /// The caller normalizes the result. Returns true when it changed.
    /// </summary>
    internal static bool Facing(string id, ref float degrees, float fieldWidth)
    {
        var changed = false;
        using (ImRaii.PushId($"{id}-facing"))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.ChevronLeft))
            {
                degrees -= FurnitureScrub.FacingStep;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Turn 1 degree (decrease)");
            ImGui.SameLine(0, 2);
            changed |= Field("value", ref degrees, FacingColor, fieldWidth, FurnitureScrub.FacingStep,
                FurnitureScrub.FacingDragStep, "%.2f", $"Facing (degrees, 0 to 359.99)\n{Help}");
            ImGui.SameLine(0, 2);
            if (ImGuiComponents.IconButton(FontAwesomeIcon.ChevronRight))
            {
                degrees += FurnitureScrub.FacingStep;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Turn 1 degree (increase)");
        }
        return changed;
    }

    /// <summary>
    /// One scrub field for a light value (brightness, range), with the
    /// same wheel, drag and typed-entry behaviour as the position fields.
    /// The caller's draft validates the bounds. Returns true when it changed.
    /// </summary>
    internal static bool Value(string id, ref float value, Vector4 color, float width, float step, string format,
        string tooltip) =>
        Field(id, ref value, color, width, step, step, format, $"{tooltip}\n{Help}");

    private static bool Field(string id, ref float value, Vector4 color, float width, float wheelStep, float dragStep,
        string format, string tooltip)
    {
        var io = ImGui.GetIO();
        var start = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight();
        var rounding = ImGui.GetStyle().FrameRounding;
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(BarWidth + rounding, height),
            ImGui.GetColorU32(color), rounding, ImDrawFlags.RoundCornersLeft);
        ImGui.SetCursorScreenPos(start + new Vector2(BarWidth, 0));
        ImGui.SetNextItemWidth(width - BarWidth);

        bool changed;
        // Speed 0: ImGui's own drag is off (its modifiers differ and may not
        // reach it in game); the field still gives typed entry on
        // double-click or Ctrl+click. Dragging is applied below, with RSC's
        // own Alt/Shift factors, the same as the wheel.
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f))
        using (ImRaii.PushColor(ImGuiCol.Border, color with { W = 0.6f }))
            changed = ImGui.DragFloat($"##{id}", ref value, 0f, 0f, 0f, format);

        if (ImGui.IsItemActive() && !io.WantTextInput && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var next = FurnitureScrub.Drag(value, io.MouseDelta.X,
                FurnitureScrub.Step(dragStep, fine: io.KeyAlt, coarse: io.KeyShift));
            if (next != value)
            {
                value = next;
                changed = true;
            }
        }

        if (ImGui.IsItemHovered())
        {
            AnyHovered = true;
            if (!ImGui.IsItemActive())
            {
                ImGui.SetTooltip(tooltip);
                var next = FurnitureScrub.Wheel(value, io.MouseWheel,
                    FurnitureScrub.Step(wheelStep, fine: io.KeyAlt, coarse: io.KeyShift));
                if (next != value)
                {
                    value = next;
                    changed = true;
                }
            }
        }
        return changed;
    }
}
