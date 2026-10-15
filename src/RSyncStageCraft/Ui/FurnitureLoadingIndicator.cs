using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Furniture;

namespace RSyncStageCraft.Ui;

/// <summary>
/// A small, input-transparent "Loading scene objects: 1/3 drawn" box at the top
/// of the screen while shared furniture and lights at the player's place are still being
/// drawn. It is independent of the furniture window and only reads state.
/// </summary>
internal sealed class FurnitureLoadingIndicator(PluginConnection connection, Func<ClientPlace?> location)
{
    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoMove;

    internal void Draw()
    {
        if (!connection.Service.Status.IsConnected)
            return;
        var progress = SceneObjectLoadProgress.Compute(
            connection.Runtime.DesiredLocalObjects(),
            location()?.Scene,
            connection.Objects.IsDrawn,
            SceneObjectReports.Excluded(connection.Objects.Unsupported));
        if (!progress.IsLoading)
            return;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            new Vector2(viewport.Pos.X + viewport.Size.X / 2, viewport.Pos.Y + 48 * ImGuiHelpers.GlobalScale),
            ImGuiCond.Always,
            new Vector2(0.5f, 0));
        ImGui.SetNextWindowBgAlpha(0.75f);
        if (ImGui.Begin("##rsc-furniture-loading", Flags))
        {
            ImGui.TextUnformatted(progress.Describe());
            ImGui.ProgressBar((float)progress.Drawn / progress.Total, new Vector2(200 * ImGuiHelpers.GlobalScale, 6 * ImGuiHelpers.GlobalScale), "");
        }
        ImGui.End();
    }
}
