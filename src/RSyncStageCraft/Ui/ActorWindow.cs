using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Ui;

/// <summary>
/// The Actors tab of the main window (a window of its own before,
/// hence the name). Actors: browse the creature catalogue (base-game monsters and
/// demihumans), place one where the player stands, pick the current target's
/// creature (read-only), and edit the actors at this place. Only the owner can
/// edit an actor: others' actors are listed read-only, and the server refuses
/// their edits anyway. Every change goes to the server; what is drawn comes
/// back through the event stream. Controls: the allowlisted loops and
/// clearing a loop. RSC has no freeze and no look-at.
/// The main window draws the connection and location banner, clears the
/// selection when the tab is left, and lists what is not drawn in Scene here.
/// </summary>
internal sealed class ActorWindow
{
    private const float CatalogueRow = 22f;
    private const float PlacedRow = 22f;
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Waiting = new(0.95f, 0.8f, 0.35f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.4f, 1f);

    private readonly PluginConnection connection;
    private readonly Func<ClientPlace?> location;
    private readonly Func<PlayerPose?> pose;
    private readonly ActorDraft draft = new();
    private string search = string.Empty;
    private string appliedSearch = string.Empty;
    private ActorCatalogEntry[] rows;
    private uint? selectedBase;
    private string? targetMessage;
    private string? draftMessage;
    private volatile string? message;
    private int pending;

    internal ActorWindow(PluginConnection connection, Func<ClientPlace?> location, Func<PlayerPose?> pose)
    {
        this.connection = connection;
        this.location = location;
        this.pose = pose;
        rows = connection.ActorCatalog.Entries.ToArray();
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
        ImGui.TextUnformatted("Creatures");
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##actor-search", "Name, model (m0024) or #row", ref search, 64);
        if (!string.Equals(search, appliedSearch, StringComparison.Ordinal))
        {
            appliedSearch = search;
            rows = connection.ActorCatalog.Search(search).ToArray();
        }
        ImGui.SameLine();
        if (ImGui.Button("Use my target"))
        {
            // Read-only: reads the current target, never changes it.
            var picked = connection.ReadTargetCreature();
            if (picked.Plan is { } plan)
            {
                selectedBase = plan.BNpcBaseId;
                targetMessage = null;
            }
            else
            {
                targetMessage = picked.NotDrawnReason;
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Selects the creature you have targeted (a supported monster or demihuman).\nYour target is only read, never changed.");
        if (targetMessage is { } note)
            ImGui.TextColored(Waiting, note);

        ImGui.TextDisabled(rows.Length == 1 ? "1 creature" : $"{rows.Length} creatures");
        using (var list = ImRaii.Child("##actor-catalogue", new Vector2(-1, 200), true))
        {
            if (list.Success)
            {
                VirtualList.Draw(rows.Length, CatalogueRow, index =>
                {
                    var row = rows[index];
                    using var id = ImRaii.PushId(row.BNpcBaseId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (ImGui.Selectable($"{row.DisplayName}##row", selectedBase == row.BNpcBaseId,
                            ImGuiSelectableFlags.None, new Vector2(0, CatalogueRow)))
                        selectedBase = row.BNpcBaseId;
                    if (row.Names.Count > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled(row.Label);
                    }
                });
            }
        }

        if (selectedBase is not { } baseId)
        {
            ImGui.TextDisabled("Select a creature to place it.");
            return;
        }
        var resolved = connection.ActorResolver.Resolve(baseId);
        if (resolved.Plan is not { } chosen)
        {
            ImGui.TextColored(Bad, resolved.NotDrawnReason ?? ActorAppearanceResolver.UnknownCreature);
            return;
        }
        var name = connection.ActorCatalog.TryGet(baseId, out var entry) ? entry.DisplayName : chosen.KindName;
        ImGui.TextUnformatted(name);
        ImGui.TextDisabled($"{chosen.KindName} · {chosen.ModelCode} · shared as #{baseId}");

        var blocked = PlaceBlock.Reason(connected, here is not null && pose() is not null, null,
            connection.Gizmo.View.CanEdit);
        using (ImRaii.Disabled(blocked is not null))
        {
            if (ImGui.Button("Place actor here"))
            {
                var player = pose()!;
                Send(connection.Editor.PlaceActorAsync(here, baseId, player.Position, player.Rotation),
                    $"Placed {name}.");
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ??
                $"Where you stand, facing your way, at scale 1. At most {ActorCapacityPolicy.ActiveMaximumActorsPerScene} actors per scene.");
    }

    // ---- Placed actors and properties ------------------------------------

    private void DrawPlaced(bool connected, SceneLocation? here)
    {
        ImGui.TextUnformatted("Actors here");
        if (here is not { } place)
        {
            ImGui.TextDisabled("Unknown location.");
            return;
        }

        var placed = connection.Runtime.DesiredLocalObjects()
            .OfType<ActorState>()
            .Where(actor => actor.Location == place)
            .OrderBy(actor => actor.ObjectId.Value)
            .ToArray();
        var self = connection.Service.SelfUserId;
        ImGui.TextDisabled(
            $"{placed.Length} of at most {ActorCapacityPolicy.ActiveMaximumActorsPerScene}; " +
            $"{connection.Objects.Actors?.OwnedCount ?? 0} drawn on this client.");
        if (ActorNotices.Hidden(connection.CurrentSuppression) is { } hidden)
            ImGui.TextColored(Waiting, hidden);
        if (ActorNotices.LeftUntouched(connection.Objects.Actors?.Abandoned.Count ?? 0) is { } orphaned)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), orphaned);
        var gizmo = connection.Gizmo.View;
        if (gizmo.Selected is { } chosen && placed.All(actor => actor.ObjectId != chosen) &&
            connection.Runtime.DesiredLocalObjects().OfType<ActorState>().Any(actor => actor.ObjectId == chosen))
            connection.Gizmo.Select(null);
        if (placed.Length == 0)
            return;

        var player = pose();
        var height = Math.Min(placed.Length, 8) * (PlacedRow + ImGui.GetStyle().ItemSpacing.Y) +
            ImGui.GetStyle().WindowPadding.Y * 2;
        using (var list = ImRaii.Child("##actor-placed", new Vector2(-1, height), true))
        {
            if (list.Success)
            {
                foreach (var actor in placed)
                {
                    using var id = ImRaii.PushId(actor.ObjectId.Value.ToString("N"));
                    var isSelected = gizmo.Selected == actor.ObjectId;
                    var detail = player is null
                        ? $"facing {ActorDraft.FacingOf(actor):0} deg"
                        : $"{Vector3.Distance(player.Position, PositionOf(actor)):0.0} y away";
                    var owner = IsMine(actor, self) ? "yours" : $"owned by {OwnerLabel(actor)}";
                    if (ImGui.Selectable($"{NameOf(actor)}##row", isSelected, ImGuiSelectableFlags.None,
                            new Vector2(0, PlacedRow)))
                        connection.Gizmo.Select(isSelected ? null : actor.ObjectId);
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{detail}, {owner}");
                }
            }
        }

        var selected = placed.FirstOrDefault(actor => actor.ObjectId == gizmo.Selected);
        ImGui.Spacing();
        if (selected is null)
        {
            ImGui.TextDisabled("Select an actor above to edit it.");
            return;
        }
        if (!IsMine(selected, self))
        {
            ImGui.TextUnformatted(NameOf(selected));
            ImGui.TextDisabled($"Owned by {OwnerLabel(selected)}: read-only (only its owner can change it).");
            return;
        }
        DrawProperties(connected, selected, gizmo, player);
        using (ImRaii.PushId("controls-" + selected.ObjectId.Value.ToString("N")))
            DrawControls(connected, selected);
    }

    /// <summary>
    /// The selected own actor: the gizmo (Move; Rotate turns the facing only),
    /// the typed X/Y/Z, Facing and Scale as one draft (Apply sends exactly one
    /// update from the shown revision and locks editing until the server's
    /// event; Reset shows the shared values again), Move to me and Delete.
    /// </summary>
    private void DrawProperties(bool connected, ActorState actor, FurnitureGizmoView gizmo, PlayerPose? player)
    {
        ImGui.TextUnformatted("Actor properties");
        ImGui.TextUnformatted(NameOf(actor));
        if (connection.ActorResolver.Resolve(actor.BNpcBaseId).Plan is { } plan)
            ImGui.TextDisabled($"{plan.KindName} · {plan.ModelCode} · #{actor.BNpcBaseId}");
        else
            ImGui.TextDisabled($"#{actor.BNpcBaseId}");

        using var id = ImRaii.PushId(actor.ObjectId.Value.ToString("N"));
        DrawGizmoControls(gizmo);

        var locked = !connected || !gizmo.CanEdit || Volatile.Read(ref pending) > 0;
        if (draft.ObjectId != actor.ObjectId)
            draftMessage = null;
        draft.Sync(actor, locked);
        using var disabled = ImRaii.Disabled(locked);

        var position = draft.Position;
        float x = position.X, y = position.Y, z = position.Z, facing = draft.Facing, scale = draft.Scale;
        ImGui.TextUnformatted("Position");
        ScrubControls.Position("actor-position", ref x, ref y, ref z, MathF.Min(ImGui.GetContentRegionAvail().X, 330f));
        ImGui.TextUnformatted("Facing (degrees)");
        ScrubControls.Facing("actor", ref facing, 100f);
        ImGui.TextUnformatted("Scale");
        ImGui.SameLine(110);
        ScrubControls.Value("actor-scale", ref scale, ScrubControls.FacingColor, 110f, 0.05f, "%.3f",
            $"Scale ({ActorLimits.MinimumScale} to {ActorLimits.MaximumScale})");
        if (!locked)
            draft.Edit(new Vector3(x, y, z), facing, scale);

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

        if (player is not null)
        {
            if (ImGui.Button("Move to me"))
                Send(connection.Editor.SetActorTransformAsync(actor, new ActorTransformPatch(
                        new Vector3State(player.Position.X, player.Position.Y, player.Position.Z), null, null)),
                    "Moved the actor.");
            ImGui.SameLine();
        }
        if (ImGui.Button("Delete"))
            Send(connection.Editor.DeleteAsync(actor), "Deleted the actor.");
    }

    // The row's ActionTimeline key from this client's game data, or its number.
    private string Describe(ushort timelineId) =>
        connection.Objects.Actors?.TimelineKey(timelineId) ?? $"row {timelineId}";

    /// <summary>
    /// The selected own actor's controls, shown disabled with the reason
    /// while this client cannot apply them: animation offering only the loops
    /// the curated allowlist verified for this creature on this game version,
    /// plus clearing it. No freeze and no look-at (removed).
    /// Every change is one update from the shown revision; the server refuses
    /// anything else.
    /// </summary>
    private void DrawControls(bool connected, ActorState actor)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Controls");
        // The same verified availability synchronized actors use.
        var offer = connection.Objects.Actors?.ControlOfferFor(actor.BNpcBaseId) ??
            ActorControlOffer.None(ActorLocalObjectHost.ControlsUnavailableReason);
        var busy = !connected || Volatile.Read(ref pending) > 0;
        var controls = actor.Controls;

        // Animation: verified (allowlisted) loops for this creature only.
        // One-shots are not supported and never offered.
        ImGui.TextUnformatted("Animation");
        ImGui.SameLine();
        var allowed = offer.Animations;
        var current = controls.TimelineId is { } row
            ? allowed.FirstOrDefault(entry => entry.TimelineId == row) is { } known
                ? $"{Describe(known.TimelineId)} (#{row}, loop)"
                : $"#{row}"
            : "none";
        ImGui.TextDisabled(current);
        if (offer.AnimationUnavailable is { } why)
            ImGui.TextDisabled(why + ".");
        else if (allowed.Count == 0)
            ImGui.TextDisabled("No verified animations for this creature.");
        foreach (var entry in allowed)
        {
            using var rowId = ImRaii.PushId($"anim-{entry.TimelineId}");
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.SmallButton("Loop"))
                    Send(connection.Editor.SetActorControlsAsync(actor,
                            new ActorControlPatch(null, null, false, entry.TimelineId, false)),
                        $"Playing {Describe(entry.TimelineId)}.");
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"{Describe(entry.TimelineId)} (#{entry.TimelineId}, loop)");
        }
        using (ImRaii.Disabled(busy || controls.TimelineId is null))
        {
            if (ImGui.Button("Clear animation"))
                Send(connection.Editor.SetActorControlsAsync(actor, new ActorControlPatch(null, null, false, null, true)),
                    "Animation cleared.");
        }
    }

    private void ApplyDraft()
    {
        var result = draft.TryApply();
        switch (result.Status)
        {
            case FurnitureDraftApplyStatus.Ready:
                draftMessage = null;
                if (!connection.Gizmo.ApplyActor(result.Basis!, result.Patch!))
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

    private void DrawGizmoControls(FurnitureGizmoView gizmo)
    {
        ImGui.TextUnformatted("Gizmo:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Move", gizmo.Mode == FurnitureGizmoMode.Move))
            connection.Gizmo.SetMode(FurnitureGizmoMode.Move);
        ImGui.SameLine();
        if (ImGui.RadioButton("Rotate", gizmo.Mode == FurnitureGizmoMode.Rotate))
            connection.Gizmo.SetMode(FurnitureGizmoMode.Rotate);
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
    /// line with the actor count.
    /// </summary>
    private void DrawNotDrawnLink()
    {
        if (SceneHere.NotDrawnLink(connection.Objects.Actors?.Unsupported.Count ?? 0) is { } link)
        {
            ImGui.Spacing();
            ImGui.TextColored(Bad, link);
        }
    }

    // ---- Helpers ---------------------------------------------------------

    // Editable strictly by stable id; the display name is only the label.
    private static bool IsMine(ActorState actor, RscUserId? self) => ActorOwnership.IsEditableBy(actor, self);

    private string OwnerLabel(ActorState actor) => ActorOwnership.OwnerLabel(actor, connection.Service.OwnerDisplayName);

    private string NameOf(ActorState actor) =>
        connection.ActorCatalog.TryGet(actor.BNpcBaseId, out var entry) ? entry.DisplayName : $"Creature #{actor.BNpcBaseId}";

    private static Vector3 PositionOf(ActorState actor) => new(actor.Position.X, actor.Position.Y, actor.Position.Z);

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
