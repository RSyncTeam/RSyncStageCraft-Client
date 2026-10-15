using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Lights;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Ui;

/// <summary>
/// The Furniture &amp; lights tab of the main window (a window of its
/// own before, hence the name). Browse the furniture catalogue, place items
/// and point lights, and edit the shared furniture and lights at the
/// player's current place. Every change goes to the server; what is drawn
/// comes back from the server, for this player exactly as for everyone else.
/// Lists are virtualized: only visible rows and their icons are drawn. The
/// main window draws the connection and location banner, clears the
/// selection when the tab is left, and lists what is not drawn in Scene here.
/// </summary>
internal sealed class FurnitureWindow
{
    private const float CatalogueRow = 40f;
    private const float PlacedRow = 28f;
    private const float PropertiesIcon = 56f;
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Waiting = new(0.95f, 0.8f, 0.35f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.4f, 1f);

    private readonly PluginConnection connection;
    private readonly FurnitureIcons icons;
    private readonly Func<ClientPlace?> location;
    private readonly Func<PlayerPose?> pose;
    private readonly FurnitureCatalogBrowser browser;
    private string search = string.Empty;
    private FurnitureSourceFilter sourceFilter = FurnitureSourceFilter.All;
    private FurnitureSupport? selectedSupport;
    private readonly FurnitureTransformDraft draft = new();
    private readonly LightDraft lightDraft = new();
    private string? draftMessage;
    private volatile string? message;
    private int pending;

    internal FurnitureWindow(
        PluginConnection connection,
        FurnitureIcons icons,
        Func<ClientPlace?> location,
        Func<PlayerPose?> pose)
    {
        this.connection = connection;
        this.icons = icons;
        this.location = location;
        this.pose = pose;
        browser = new FurnitureCatalogBrowser(connection.Resolver.Catalog);
    }

    /// <summary>Draws the tab's content. The main window handles scrolling over scrub fields.</summary>
    internal void Draw()
    {
        var connected = connection.Service.Status.IsConnected;
        var here = location()?.Scene;
        if (Volatile.Read(ref pending) > 0)
            ImGui.TextDisabled("Sending...");
        if (message is { } text)
            ImGui.TextWrapped(text);

        ImGui.Spacing();
        ImGui.Separator();
        DrawCatalogue(connected, here);
        ImGui.Spacing();
        ImGui.Separator();
        DrawPlaced(connected, here);
        DrawNotDrawnLink();
    }

    // ---- Catalogue -------------------------------------------------------

    private void DrawCatalogue(bool connected, SceneLocation? here)
    {
        ImGui.TextUnformatted("Catalogue");
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##furniture-search", "Filter by name (optional)", ref search, 64);
        ImGui.SameLine();
        SourceRadio("All", FurnitureSourceFilter.All);
        ImGui.SameLine();
        SourceRadio("Indoor", FurnitureSourceFilter.Indoor);
        ImGui.SameLine();
        SourceRadio("Outdoor", FurnitureSourceFilter.Outdoor);
        // Recomputed only when a filter changed, not every frame.
        browser.SetFilter(search, sourceFilter);

        var rows = browser.Rows;
        ImGui.TextDisabled(rows.Count == 1 ? "1 item" : $"{rows.Count} items");
        using (var list = ImRaii.Child("##furniture-catalogue", new Vector2(-1, 220), true))
        {
            if (list.Success)
            {
                VirtualList.Draw(rows.Count, CatalogueRow, index =>
                {
                    var row = rows[index];
                    if (RowSelectable(row.Identity, browser.Selected == row, CatalogueRow, row.IconId,
                            row.Name, SourceName(row.Source)))
                        SelectCatalogue(row);
                });
            }
        }

        DrawCatalogueSelection(connected, here);
    }

    private void SourceRadio(string label, FurnitureSourceFilter value)
    {
        if (ImGui.RadioButton(label, sourceFilter == value))
            sourceFilter = value;
    }

    private void SelectCatalogue(FurnitureCatalogRow row)
    {
        if (!browser.Select(row))
            return;
        // Checks the item's layout on disk once; cached afterwards.
        selectedSupport = connection.Resolver.Resolve(row.Identity);
    }

    private void DrawCatalogueSelection(bool connected, SceneLocation? here)
    {
        if (browser.Selected is not { } selected || selectedSupport is null)
        {
            ImGui.TextDisabled("Select an item to place it, or place a light.");
            DrawPlaceLight(connected, here);
            return;
        }

        icons.Draw(selected.IconId, PropertiesIcon);
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted(selected.Name);
            ImGui.TextDisabled($"{SourceName(selected.Source)}, shared as {selected.Identity}");
            ImGui.TextColored(selectedSupport.IsSupported ? Good : Bad, selectedSupport.Describe());
            if (selectedSupport.Layout is { } layout)
                ImGui.TextDisabled($"Layout: {layout}");
        }

        var blocked = PlaceBlock.Reason(connected, here is not null && pose() is not null,
            selectedSupport.IsSupported ? null : selectedSupport.Describe(), connection.Gizmo.View.CanEdit);
        using (ImRaii.Disabled(blocked is not null))
        {
            if (ImGui.Button("Place where I stand"))
            {
                var player = pose()!;
                Send(connection.Editor.PlaceAsync(here, selected.Identity, player.Position, player.Rotation),
                    $"Placed {selected.Name}.");
            }
        }
        if (blocked is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked);
        ImGui.SameLine();
        DrawPlaceLight(connected, here);
        if (!selectedSupport.IsSupported)
            ImGui.TextDisabled("Only single-model items can be shared in this version.");
    }

    /// <summary>
    /// Places one point light at the player's position with the
    /// defaults: white, brightness 1, range 8 yalms.
    /// </summary>
    private void DrawPlaceLight(bool connected, SceneLocation? here)
    {
        var blocked = PlaceBlock.Reason(connected, here is not null && pose() is not null, null,
            connection.Gizmo.View.CanEdit);
        using (ImRaii.Disabled(blocked is not null))
        {
            if (ImGui.Button("Place light"))
                Send(connection.Editor.PlaceLightAsync(here, pose()!.Position), "Placed a light.");
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ??
                $"A white point light where you stand (brightness {LightLimits.DefaultIntensity:0.##}, range {LightLimits.DefaultRange:0.##} yalms).");
    }

    // ---- Placed furniture and lights, and properties ---------------------

    private void DrawPlaced(bool connected, SceneLocation? here)
    {
        ImGui.TextUnformatted("Furniture and lights here");
        if (here is not { } place)
        {
            ImGui.TextDisabled("Unknown location.");
            return;
        }

        var placed = connection.Runtime.DesiredLocalObjects()
            .Where(state => state is FurnitureState or LightState && state.Location == place)
            .OrderBy(state => state is LightState ? 1 : 0)
            .ThenBy(state => state.ObjectId.Value)
            .ToArray();
        var lights = placed.Count(state => state is LightState);
        ImGui.TextDisabled(
            $"{placed.Length - lights} item(s), {lights} light(s); {connection.Objects.OwnedCount} drawn on this client.");
        var gizmo = connection.Gizmo.View;
        // Every tab change clears the selection; this only drops a
        // selection that left the place. An actor is not deselected here.
        if (gizmo.Selected is { } chosen && placed.All(state => state.ObjectId != chosen) &&
            !connection.Runtime.DesiredLocalObjects().Any(state => state is ActorState && state.ObjectId == chosen))
            connection.Gizmo.Select(null);
        if (placed.Length == 0)
            return;

        var player = pose();
        var height = Math.Min(placed.Length, 6) * (PlacedRow + ImGui.GetStyle().ItemSpacing.Y) +
            ImGui.GetStyle().WindowPadding.Y * 2;
        using (var list = ImRaii.Child("##furniture-placed", new Vector2(-1, height), true))
        {
            if (list.Success)
            {
                VirtualList.Draw(placed.Length, PlacedRow, index =>
                {
                    var isSelected = gizmo.Selected == placed[index].ObjectId;
                    if (placed[index] is LightState light)
                    {
                        var lightDetail = player is null
                            ? $"range {light.Range:0.##} y"
                            : $"{Vector3.Distance(player.Position, LightLocalObjectHost.PositionOf(light)):0.0} y away";
                        if (RowSelectable(light.ObjectId.Value.ToString("N"), isSelected, PlacedRow, 0,
                                "Point light", lightDetail, isLight: true))
                            connection.Gizmo.Select(isSelected ? null : light.ObjectId);
                        return;
                    }
                    var furniture = (FurnitureState)placed[index];
                    var row = connection.Resolver.Catalog.Find(furniture.FurnitureIdentity).Row;
                    var detail = player is null
                        ? $"facing {FurnitureEditor.YawDegrees(furniture.Rotation):0} deg"
                        : $"{Vector3.Distance(player.Position, PositionOf(furniture)):0.0} y away";
                    if (RowSelectable(furniture.ObjectId.Value.ToString("N"), isSelected, PlacedRow,
                            row?.IconId ?? 0, row?.Name ?? furniture.FurnitureIdentity, detail))
                        connection.Gizmo.Select(isSelected ? null : furniture.ObjectId);
                });
            }
        }

        var selectedState = placed.FirstOrDefault(state => state.ObjectId == gizmo.Selected);
        ImGui.Spacing();
        switch (selectedState)
        {
            case FurnitureState selectedFurniture:
                DrawProperties(connected, selectedFurniture, gizmo, player);
                break;
            case LightState selectedLight:
                DrawLightProperties(connected, selectedLight, gizmo, player);
                break;
            default:
                ImGui.TextDisabled("Select an item or light above to edit it.");
                break;
        }
    }

    /// <summary>
    /// The selected placed item: its icon and name, the gizmo, the typed
    /// transform, move to me and delete. The X, Y, Z and Facing fields are one
    /// draft (<see cref="FurnitureTransformDraft"/>): typing sends and draws
    /// nothing; Apply sends exactly one transform update from the shown
    /// revision through the gizmo's commit path; Reset shows the shared
    /// values again. While a drag or any furniture update is in progress no
    /// edit is offered: the item's shown revision is about to change.
    /// </summary>
    private void DrawProperties(bool connected, FurnitureState furniture, FurnitureGizmoView gizmo, PlayerPose? player)
    {
        var row = connection.Resolver.Catalog.Find(furniture.FurnitureIdentity).Row;
        var name = row?.Name ?? furniture.FurnitureIdentity;
        var position = PositionOf(furniture);

        ImGui.TextUnformatted("Furniture properties");
        icons.Draw(row?.IconId ?? 0, PropertiesIcon);
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted(name);
            ImGui.TextDisabled(row is null ? furniture.FurnitureIdentity : $"{SourceName(row.Source)}, {row.Identity}");
        }

        using var id = ImRaii.PushId(furniture.ObjectId.Value.ToString("N"));
        DrawGizmoControls(gizmo, canRotate: true);

        var locked = !connected || !gizmo.CanEdit || Volatile.Read(ref pending) > 0;
        if (draft.ObjectId != furniture.ObjectId)
            draftMessage = null;
        draft.Sync(furniture, locked);
        using var disabled = ImRaii.Disabled(locked);

        // Scrubbing, scrolling or typing changes only the local draft.
        float x = draft.X, y = draft.Y, z = draft.Z, facing = draft.Facing;
        ImGui.TextUnformatted("Position");
        ScrubControls.Position("position", ref x, ref y, ref z, MathF.Min(ImGui.GetContentRegionAvail().X, 330f));
        ImGui.TextUnformatted("Facing (degrees)");
        ScrubControls.Facing("transform", ref facing, 100f);
        if (!locked)
            draft.Edit(x, y, z, facing);

        if (ImGui.Button("Apply"))
            ApplyDraft();
        ImGui.SameLine();
        using (ImRaii.Disabled(!draft.IsDirty))
        {
            if (ImGui.Button("Reset"))
            {
                draft.Reset();
                draftMessage = null;
            }
        }
        if (draft.IsDirty)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Not applied yet.");
        }
        if (draftMessage is { } note)
            ImGui.TextColored(Waiting, note);

        DrawDye(furniture, row);

        if (player is not null)
        {
            if (ImGui.Button("Move to me"))
                Send(connection.Editor.NudgeAsync(furniture, player.Position - position, 0), $"Moved {name}.");
            ImGui.SameLine();
        }
        if (ImGui.Button("Delete"))
            Send(connection.Editor.DeleteAsync(furniture), $"Deleted {name}.");
    }

    /// <summary>
    /// The selected light: the gizmo (Move only), and position, colour,
    /// brightness and range as one draft (<see cref="LightDraft"/>). Apply
    /// sends exactly one light update from the shown revision and locks all
    /// editing until the server's event, like a dye change; Reset shows the
    /// shared values again. Move to me and Delete as for furniture.
    /// </summary>
    private void DrawLightProperties(bool connected, LightState light, FurnitureGizmoView gizmo, PlayerPose? player)
    {
        ImGui.TextUnformatted("Light properties");
        FurnitureIcons.DrawLight(PropertiesIcon);
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted("Point light");
            ImGui.TextDisabled($"Shared as {LightIdentity.Point}");
        }

        using var id = ImRaii.PushId(light.ObjectId.Value.ToString("N"));
        DrawGizmoControls(gizmo, canRotate: false);

        var locked = !connected || !gizmo.CanEdit || Volatile.Read(ref pending) > 0;
        if (lightDraft.ObjectId != light.ObjectId)
            draftMessage = null;
        lightDraft.Sync(light, locked);
        using var disabled = ImRaii.Disabled(locked);

        // Scrubbing, scrolling, picking or typing changes only the local draft.
        var draftPosition = lightDraft.Position;
        float x = draftPosition.X, y = draftPosition.Y, z = draftPosition.Z;
        ImGui.TextUnformatted("Position");
        ScrubControls.Position("light-position", ref x, ref y, ref z, MathF.Min(ImGui.GetContentRegionAvail().X, 330f));

        var color = new Vector3(lightDraft.Color.R, lightDraft.Color.G, lightDraft.Color.B);
        ImGui.TextUnformatted("Colour");
        ImGui.SetNextItemWidth(MathF.Min(ImGui.GetContentRegionAvail().X, 330f));
        ImGui.ColorEdit3("##light-colour", ref color,
            ImGuiColorEditFlags.Float | ImGuiColorEditFlags.DisplayRgb | ImGuiColorEditFlags.InputRgb);

        float intensity = lightDraft.Intensity, range = lightDraft.Range;
        ImGui.TextUnformatted("Brightness");
        ImGui.SameLine(110);
        ScrubControls.Value("light-brightness", ref intensity, ScrubControls.FacingColor, 110f, 0.05f, "%.2f",
            $"Brightness ({LightLimits.MinimumIntensity} to {LightLimits.MaximumIntensity})");
        ImGui.TextUnformatted("Range");
        ImGui.SameLine(110);
        ScrubControls.Value("light-range", ref range, ScrubControls.FacingColor, 110f, 0.5f, "%.2f",
            $"Range in yalms ({LightLimits.MinimumRange} to {LightLimits.MaximumRange})");
        ImGui.SameLine();
        ImGui.TextDisabled("yalms");
        if (!locked)
            lightDraft.Edit(new Vector3(x, y, z), new LightColor(color.X, color.Y, color.Z), intensity, range);

        if (ImGui.Button("Apply"))
            ApplyLightDraft();
        ImGui.SameLine();
        using (ImRaii.Disabled(!lightDraft.IsDirty))
        {
            if (ImGui.Button("Reset"))
            {
                lightDraft.Reset();
                draftMessage = null;
            }
        }
        if (lightDraft.IsDirty)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Not applied yet.");
        }
        if (draftMessage is { } note)
            ImGui.TextColored(Waiting, note);

        if (player is not null)
        {
            if (ImGui.Button("Move to me"))
                Send(connection.Editor.SetLightAsync(light, new LightPointPatch(
                        new Vector3State(player.Position.X, player.Position.Y, player.Position.Z), null, null, null)),
                    "Moved the light.");
            ImGui.SameLine();
        }
        if (ImGui.Button("Delete"))
            Send(connection.Editor.DeleteAsync(light), "Deleted the light.");
    }

    private void ApplyLightDraft()
    {
        var result = lightDraft.TryApply();
        switch (result.Status)
        {
            case FurnitureDraftApplyStatus.Ready:
                draftMessage = null;
                if (!connection.Gizmo.ApplyLight(result.Basis!, result.Patch!))
                {
                    lightDraft.Reset();
                    draftMessage = "Another change is in progress; not applied.";
                }
                break;
            case FurnitureDraftApplyStatus.NothingChanged:
                draftMessage = "Nothing to apply.";
                break;
            default:
                draftMessage = result.Message;
                break;
        }
    }

    /// <summary>
    /// The dye selector, styled after Brio's (idea only): the current
    /// dye as a swatch and name; a popup lists Default and the housing dyes
    /// this client can draw. Opening, hovering or scrolling sends nothing;
    /// picking a different dye sends exactly one stain update through the
    /// controller, which then locks all editing until the server's event.
    /// </summary>
    private void DrawDye(FurnitureState furniture, FurnitureCatalogRow? row)
    {
        ImGui.TextUnformatted("Dye");
        var dyeable = row is { IsDyeable: true };
        if (!dyeable && furniture.Stain == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("This item cannot be dyed.");
            return;
        }

        var known = connection.Stains.TryGet(furniture.Stain, out var current);
        if (!dyeable)
        {
            // A dye on an item this client cannot dye (from another client,
            // changed game data or bad data): it is not drawn here. Offer
            // only the way back to Default, never another dye.
            ImGui.SameLine();
            ImGui.TextColored(Bad, known ? current.Name : $"Dye {furniture.Stain}");
            ImGui.SameLine();
            ImGui.TextDisabled("(this item cannot be dyed here; not drawn)");
            if (ImGui.Button("Reset to Default##dye-reset"))
                PickDye(furniture, 0);
            return;
        }

        ImGui.SameLine();
        if (furniture.Stain == 0)
            DefaultSwatch();
        else
            ImGui.ColorButton("##dye-current", known ? Swatch(current.Swatch) : new Vector4(0, 0, 0, 1),
                ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFrameHeight()));
        ImGui.SameLine();
        var label = furniture.Stain == 0 ? "Default" : known ? current.Name : $"Unknown dye {furniture.Stain}";
        if (ImGui.Button($"{label}##dye-open"))
            ImGui.OpenPopup("##dye-popup");
        if (row is { DyeCount: > 1 })
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Primary dye only in this version.");
        }

        using var popup = ImRaii.Popup("##dye-popup");
        if (!popup.Success)
            return;
        using var list = ImRaii.Child("##dye-list", new Vector2(260, 320), false);
        if (!list.Success)
            return;
        if (DyeEntry("##dye-0", null, "Default (undyed)", furniture.Stain == 0))
            PickDye(furniture, 0);
        foreach (var entry in connection.Stains.Entries)
        {
            if (DyeEntry($"##dye-{entry.Id}", entry.Swatch, $"{entry.Name} ({entry.Id})", furniture.Stain == entry.Id))
                PickDye(furniture, entry.Id);
        }
    }

    private void PickDye(FurnitureState furniture, byte stain)
    {
        ImGui.CloseCurrentPopup();
        if (furniture.Stain == stain)
            return; // The dye it already has: nothing to send.
        if (!connection.Gizmo.ApplyStain(furniture, stain))
            draftMessage = "Another change is in progress; the dye was not changed.";
    }

    private static bool DyeEntry(string id, uint? swatch, string name, bool selected)
    {
        var size = new Vector2(ImGui.GetFrameHeight());
        var start = ImGui.GetCursorPos();
        var clicked = ImGui.Selectable(id, selected, ImGuiSelectableFlags.None, new Vector2(0, size.Y));
        var end = ImGui.GetCursorPos();
        if (ImGui.IsItemVisible())
        {
            ImGui.SetCursorPos(start);
            if (swatch is { } color)
                ImGui.ColorButton($"{id}-swatch", Swatch(color), ImGuiColorEditFlags.NoTooltip, size);
            else
                DefaultSwatch();
            ImGui.SameLine();
            ImGui.TextUnformatted(name);
            ImGui.SetCursorPos(end);
        }
        return clicked;
    }

    /// <summary>Default (undyed): an empty frame with a diagonal line.</summary>
    private static void DefaultSwatch()
    {
        var size = new Vector2(ImGui.GetFrameHeight());
        var min = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRect(min, min + size, ImGui.GetColorU32(ImGuiCol.TextDisabled), 2f);
        draw.AddLine(min + new Vector2(3, size.Y - 3), min + new Vector2(size.X - 3, 3), ImGui.GetColorU32(ImGuiCol.TextDisabled));
    }

    private static Vector4 Swatch(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    private void ApplyDraft()
    {
        var result = draft.TryApply();
        switch (result.Status)
        {
            case FurnitureDraftApplyStatus.Ready:
                draftMessage = null;
                if (!connection.Gizmo.ApplyTransform(result.Basis!, result.Position, result.Rotation))
                {
                    draft.Reset();
                    draftMessage = "Another change is in progress; not applied.";
                }
                break;
            case FurnitureDraftApplyStatus.NothingChanged:
                draftMessage = "Nothing to apply.";
                break;
            default:
                draftMessage = result.Message;
                break;
        }
    }

    private void DrawGizmoControls(FurnitureGizmoView gizmo, bool canRotate)
    {
        ImGui.TextUnformatted("Gizmo:");
        ImGui.SameLine();
        if (canRotate)
        {
            if (ImGui.RadioButton("Move", gizmo.Mode == FurnitureGizmoMode.Move))
                connection.Gizmo.SetMode(FurnitureGizmoMode.Move);
            ImGui.SameLine();
            if (ImGui.RadioButton("Rotate", gizmo.Mode == FurnitureGizmoMode.Rotate))
                connection.Gizmo.SetMode(FurnitureGizmoMode.Rotate);
        }
        else
        {
            // A point light has no rotation: its gizmo always moves it.
            ImGui.RadioButton("Move", true);
        }
        ImGui.SameLine();
        ImGui.TextDisabled(gizmo.Phase switch
        {
            FurnitureGizmoPhase.Dragging => "Dragging (only you see this until you let go).",
            FurnitureGizmoPhase.Committing => "Saving (waiting for the server)...",
            _ => "Drag the gizmo in the world.",
        });
        if (gizmo.Message is { } note)
            ImGui.TextColored(Waiting, note);
    }

    /// <summary>
    /// What is not drawn is listed in Scene here; this tab keeps one
    /// line with the furniture and light count.
    /// </summary>
    private void DrawNotDrawnLink()
    {
        var count = connection.Objects.Furniture.Unsupported.Count + connection.Objects.Lights.Unsupported.Count;
        if (SceneHere.NotDrawnLink(count) is { } link)
        {
            ImGui.Spacing();
            ImGui.TextColored(Bad, link);
        }
    }

    // ---- Helpers ---------------------------------------------------------

    /// <summary>
    /// One list row: a full-width selectable with the icon, the name and a
    /// dimmed detail line drawn over it. Returns true when clicked.
    /// </summary>
    private bool RowSelectable(string id, bool selected, float height, uint iconId, string name, string detail,
        bool isLight = false)
    {
        using var scope = ImRaii.PushId(id);
        var start = ImGui.GetCursorPos();
        var clicked = ImGui.Selectable("##row", selected, ImGuiSelectableFlags.None, new Vector2(0, height));
        var end = ImGui.GetCursorPos();
        if (ImGui.IsItemVisible())
        {
            ImGui.SetCursorPos(start);
            if (isLight)
                FurnitureIcons.DrawLight(height);
            else
                icons.Draw(iconId, height);
            ImGui.SameLine();
            using (ImRaii.Group())
            {
                ImGui.TextUnformatted(name);
                if (height >= ImGui.GetTextLineHeightWithSpacing() * 2)
                    ImGui.TextDisabled(detail);
                else
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(detail);
                }
            }
            ImGui.SetCursorPos(end);
        }
        return clicked;
    }

    private static string SourceName(FurnitureSource source) =>
        source == FurnitureSource.Outdoor ? "Outdoor" : "Indoor";

    private static Vector3 PositionOf(FurnitureState furniture) =>
        new(furniture.Position.X, furniture.Position.Y, furniture.Position.Z);

    private void Send(Task<ServerCommandResult> command, string? success)
    {
        Interlocked.Increment(ref pending);
        _ = command.ContinueWith(task =>
        {
            Interlocked.Decrement(ref pending);
            message = task.IsCompletedSuccessfully
                ? task.Result.Completed ? success : task.Result.Describe()
                : ServerCommandResult.NotSentText;
        }, TaskScheduler.Default);
    }
}

/// <summary>
/// A virtualized list body: ImGui's list clipper asks only for the rows that
/// are visible, so only those rows (and their icons) are processed.
/// </summary>
internal static class VirtualList
{
    internal static unsafe void Draw(int count, float rowHeight, Action<int> drawRow)
    {
        if (count <= 0)
            return;
        var pitch = rowHeight + ImGui.GetStyle().ItemSpacing.Y;
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper());
        try
        {
            clipper.Begin(count, pitch);
            while (clipper.Step())
            {
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                    drawRow(index);
            }
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
    }
}
