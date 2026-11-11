#if RSC_DEVELOPMENT
using Dalamud.Game;
using Dalamud.Plugin.Services;
using RSyncStageCraft.Core.Actors.Humans;
using LuminaWorld = Lumina.Excel.Sheets.World;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// HV-4 only (Debug build, dev plugin): W-3's World row and the player's
/// current world, read without writing. The client-language read is the same
/// sheet read the providers build their world list from, and it reports the
/// name's first raw byte, the byte that list tests. Any failure reads as
/// unreadable (null), which refuses.
/// </summary>
internal sealed class HumanActorWorldData(IDataManager data, IPlayerState playerState) : IHumanActorWorldSource
{
    public HumanActorWorldRow? EnglishRow(ushort rowId) => Read(rowId, ClientLanguage.English);

    public HumanActorWorldRow? ClientRow(ushort rowId) => Read(rowId, null);

    public ushort? PlayerCurrentWorld()
    {
        try
        {
            if (!playerState.IsLoaded)
                return null;
            var world = playerState.CurrentWorld.RowId;
            return world <= ushort.MaxValue ? (ushort)world : null;
        }
        catch
        {
            return null;
        }
    }

    private HumanActorWorldRow? Read(ushort rowId, ClientLanguage? language)
    {
        try
        {
            var sheet = language is { } named ? data.GetExcelSheet<LuminaWorld>(named) : data.GetExcelSheet<LuminaWorld>();
            if (sheet.GetRowOrDefault(rowId) is not { } row)
                return null;
            var raw = row.Name.Data.Span;
            return new HumanActorWorldRow(row.RowId, row.InternalName.ExtractText(), row.Name.ExtractText(),
                raw.IsEmpty ? -1 : raw[0], row.DataCenter.RowId, row.UserType, row.Region, row.IsPublic);
        }
        catch
        {
            return null;
        }
    }
}
#endif
