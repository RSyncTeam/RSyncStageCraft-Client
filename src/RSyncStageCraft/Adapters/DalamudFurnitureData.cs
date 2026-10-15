using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Reads the housing catalogue and item layouts from the game's own files on
/// disk (IDataManager). Read-only and local: nothing here touches the network.
/// The catalogue mapping (item row ID plus indoor/outdoor to model key) follows
/// Izzy's Furniture.
/// </summary>
internal static class DalamudFurnitureData
{
    internal static FurnitureCatalog LoadCatalog(IDataManager data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var rows = new List<FurnitureCatalogRow>(4096);
        foreach (var row in data.GetExcelSheet<HousingFurniture>())
        {
            if (row.RowId == 0 || row.ModelKey == 0 || !row.Item.TryGetValue(out var item) || item.RowId == 0)
                continue;
            var name = item.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                rows.Add(new FurnitureCatalogRow(FurnitureSource.Indoor, item.RowId, row.ModelKey, name, item.Icon, item.DyeCount));
        }
        foreach (var row in data.GetExcelSheet<HousingYardObject>())
        {
            if (row.RowId == 0 || row.ModelKey == 0 || !row.Item.TryGetValue(out var item) || item.RowId == 0)
                continue;
            var name = item.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                rows.Add(new FurnitureCatalogRow(FurnitureSource.Outdoor, item.RowId, row.ModelKey, name, item.Icon, item.DyeCount));
        }
        return new FurnitureCatalog(rows);
    }

    /// <summary>
    /// The housing stains this client can draw: the <c>Stain</c> sheet
    /// filtered by the rules in <see cref="FurnitureStainPalette.Build"/>,
    /// with each colour from the game's own object-stain table (ClientStructs'
    /// <c>SharedGroupLayoutInstance.GetObjectStainColorByIndex</c>; no hooks
    /// and no RSC-owned signature). Sheet name and colour are for display.
    /// </summary>
    internal static unsafe FurnitureStainPalette LoadStains(IDataManager data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sheet = new List<FurnitureStainSheetRow>(256);
        foreach (var row in data.GetExcelSheet<Stain>())
        {
            sheet.Add(new FurnitureStainSheetRow(
                row.RowId,
                row.Name.ExtractText(),
                row.Color,
                row.IsHousingApplicable,
                row.Shade * 1000 + row.SubOrder));
        }
        return FurnitureStainPalette.Build(
            sheet,
            SharedGroupLayoutInstance.ObjectStainCount,
            id =>
            {
                var color = SharedGroupLayoutInstance.GetObjectStainColorByIndex(id);
                return color == null ? null : new FurnitureStainColor(color->R, color->G, color->B, color->A);
            });
    }
}

/// <summary>Local game files for furniture: existence checks and item layouts (.sgb).</summary>
internal sealed class DalamudFurnitureAssets(IDataManager data) : IFurnitureAssetReader
{
    public bool FileExists(string gamePath)
    {
        try
        {
            return data.FileExists(gamePath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<FurnitureLayoutInstance>? ReadLayout(string gamePath)
    {
        SgbFile? file;
        try
        {
            file = data.GetFile<SgbFile>(gamePath);
        }
        catch (Exception)
        {
            return null;
        }
        if (file?.LayerGroups is not { } groups)
            return null;

        var instances = new List<FurnitureLayoutInstance>();
        foreach (var group in groups)
        {
            foreach (var layer in group.Layers ?? [])
            {
                foreach (var instance in layer.InstanceObjects ?? [])
                {
                    var transform = instance.Transform;
                    var isModel = instance.AssetType == LayerEntryType.BG &&
                        instance.Object is LayerCommon.BGInstanceObject;
                    instances.Add(new FurnitureLayoutInstance(
                        isModel ? FurnitureLayoutInstanceKind.Model : KindOf(instance.AssetType),
                        isModel ? ((LayerCommon.BGInstanceObject)instance.Object).AssetPath : null,
                        new(transform.Translation.X, transform.Translation.Y, transform.Translation.Z),
                        new(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z),
                        new(transform.Scale.X, transform.Scale.Y, transform.Scale.Z),
                        instance.AssetType.ToString()));
                }
            }
        }
        return instances;
    }

    /// <summary>
    /// The layout's housing default colour (<c>HousingSettings.DefaultColorID</c>
    /// of the .sgb scene chunk), the stain the game applies to the undyed
    /// item. 0 when absent or outside the object stain table.
    /// </summary>
    public byte ReadDefaultStain(string gamePath)
    {
        try
        {
            var id = data.GetFile<SgbFile>(gamePath)?.ChunkHeader.HousingSettings?.DefaultColorID ?? 0;
            return id is > 0 and < SharedGroupLayoutInstance.ObjectStainCount ? (byte)id : (byte)0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Entry types that draw and sound nothing: interaction markers,
    /// collision and ranges. Everything else (lights, effects, sounds, nested
    /// groups, animation timelines, unknown types) counts as a part.
    /// </summary>
    private static FurnitureLayoutInstanceKind KindOf(LayerEntryType type) => type switch
    {
        LayerEntryType.PositionMarker or
        LayerEntryType.ChairMarker or
        LayerEntryType.TargetMarker or
        LayerEntryType.CollisionBox or
        LayerEntryType.ClickableRange or
        LayerEntryType.PrefetchRange or
        LayerEntryType.HelperObject or
        LayerEntryType.ControlPoint or
        LayerEntryType.EventRange or
        LayerEntryType.GimmickRange or
        LayerEntryType.DoorRange or
        LayerEntryType.MapRange or
        LayerEntryType.ExitRange or
        LayerEntryType.PopRange or
        LayerEntryType.NaviMeshRange or
        LayerEntryType.QuestMarker or
        LayerEntryType.RoutePath or
        LayerEntryType.ClientPath or
        LayerEntryType.ServerPath => FurnitureLayoutInstanceKind.Helper,
        _ => FurnitureLayoutInstanceKind.Other,
    };
}
