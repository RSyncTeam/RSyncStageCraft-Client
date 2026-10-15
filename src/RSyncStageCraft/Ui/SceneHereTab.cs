using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Ui;

/// <summary>
/// Scene here: an overview of the shared objects at the player's
/// place, with who placed or owns each, and the one combined list of objects
/// not drawn on this client. Selecting a row opens the object in its own tab.
/// It only reads state; Retry all scene objects is the existing action.
/// </summary>
internal sealed class SceneHereTab
{
    private const float Row = 22f;
    private const float KindColumn = 260f;
    private const float OwnershipColumn = 350f;
    private static readonly Vector4 Waiting = new(0.95f, 0.8f, 0.35f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.4f, 1f);

    private readonly PluginConnection connection;
    private readonly Func<ClientPlace?> location;
    private readonly MainWindowNavigation navigation;

    internal SceneHereTab(PluginConnection connection, Func<ClientPlace?> location, MainWindowNavigation navigation)
    {
        this.connection = connection;
        this.location = location;
        this.navigation = navigation;
    }

    internal void Draw()
    {
        var here = location();
        ImGui.TextUnformatted(here is null
            ? "Scene here"
            : $"Scene here — {PlaceLabels.Full(here.Location.DescribeLines(here.Names))}");
        if (here?.Scene is not { } place)
        {
            ImGui.TextDisabled("Unknown location.");
        }
        else
        {
            var rows = SceneHere.Rows(
                connection.Runtime.DesiredLocalObjects(),
                place,
                connection.Service.SelfUserId,
                FurnitureName,
                ActorName,
                connection.Service.OwnerDisplayName);
            ImGui.TextDisabled(SceneHere.Counts(rows, ActorCapacityPolicy.ActiveMaximumActorsPerScene));
            DrawRows(rows);
        }
        DrawNotDrawn();
    }

    private void DrawRows(IReadOnlyList<SceneHereRow> rows)
    {
        if (rows.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextUnformatted("Objects here");
        var height = Math.Min(rows.Count, 10) * (Row + ImGui.GetStyle().ItemSpacing.Y) +
            ImGui.GetStyle().WindowPadding.Y * 2;
        using var list = ImRaii.Child("##scene-here-objects", new Vector2(-1, height), true);
        if (!list.Success)
            return;
        VirtualList.Draw(rows.Count, Row, index =>
        {
            var row = rows[index];
            using var id = ImRaii.PushId(row.ObjectId.Value.ToString("N"));
            if (ImGui.Selectable($"{row.Name}##row", false, ImGuiSelectableFlags.None, new Vector2(0, Row)))
                navigation.RequestSelect(row.Tab, row.ObjectId);
            ImGui.SameLine(KindColumn);
            ImGui.TextDisabled(row.Kind);
            ImGui.SameLine(OwnershipColumn);
            ImGui.TextDisabled(row.Ownership);
        });
    }

    /// <summary>
    /// Furniture, lights and actors not drawn on this client, by kind
    /// (permanently unsupported, being retried, or failed after the retries).
    /// With a failed entry, Retry all scene objects starts a fresh attempt for
    /// everything at this place (the whole-domain reset a location change does).
    /// </summary>
    private void DrawNotDrawn()
    {
        var reports = connection.Objects.Unsupported;
        if (reports.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(Bad, $"Not drawn on this client ({reports.Count})");
        foreach (var report in reports)
        {
            var color = report.Kind == LocalObjectReportKind.Retrying ? Waiting : Bad;
            ImGui.TextColored(color, $"{SceneHere.ReportName(report, CreatureName)}:");
            ImGui.SameLine();
            ImGui.TextWrapped(report.Reason);
        }
        if (!SceneHere.OffersRetry(reports))
            return;
        using (ImRaii.Disabled(!connection.Gizmo.View.CanEdit))
        {
            if (ImGui.Button("Retry all scene objects"))
                connection.RetryAllSceneObjects();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(SceneHere.RetryTooltip);
    }

    private string FurnitureName(FurnitureState furniture) =>
        connection.Resolver.Catalog.Find(furniture.FurnitureIdentity).Row?.Name ?? furniture.FurnitureIdentity;

    private string ActorName(ActorState actor) =>
        connection.ActorCatalog.TryGet(actor.BNpcBaseId, out var entry) ? entry.DisplayName : $"Creature #{actor.BNpcBaseId}";

    private string? CreatureName(uint baseId) =>
        connection.ActorCatalog.TryGet(baseId, out var entry) ? entry.DisplayName : null;
}
