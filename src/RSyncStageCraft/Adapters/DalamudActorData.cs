using System.Reflection;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using RSyncStageCraft.Core.Actors;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// The actor catalogue's game data, read from the player's own game
/// files through IDataManager (Lumina sheets): BNpcBase, ModelChara,
/// NpcEquip and BNpcName, and ActionTimeline. Read-only and local: nothing here touches the
/// network. The BNpcName-to-BNpcBase links come from the pinned, embedded
/// LuminaSupplemental dataset (Data/BNpcLink.csv, GPL-3.0).
/// </summary>
internal sealed class DalamudActorData(IDataManager data) : IActorGameData, IActorTimelineData
{
    /// <summary>One ActionTimeline row from the local sheet: its key and whether it loops.</summary>
    public bool TryGetTimeline(ushort timelineId, out ActorTimelineRow row)
    {
        if (timelineId != 0 && data.GetExcelSheet<ActionTimeline>().TryGetRow(timelineId, out var source))
        {
            row = new ActorTimelineRow(timelineId, source.Key.ExtractText(), source.IsLoop);
            return true;
        }
        row = null!;
        return false;
    }

    /// <summary>Looping ActionTimeline rows whose key contains the text (case-insensitive) or whose id is it (one-shots are not supported).</summary>
    public IReadOnlyList<ActorTimelineRow> Search(string text, int limit)
    {
        var results = new List<ActorTimelineRow>();
        var byId = ushort.TryParse(text, out var id) ? id : (ushort)0;
        foreach (var source in data.GetExcelSheet<ActionTimeline>())
        {
            if (results.Count >= limit)
                break;
            if (source.RowId == 0 || source.RowId > ushort.MaxValue)
                continue;
            if (!source.IsLoop)
                continue;
            var key = source.Key.ExtractText();
            if (key.Length == 0)
                continue;
            if (source.RowId == byId || key.Contains(text, StringComparison.OrdinalIgnoreCase))
                results.Add(new ActorTimelineRow((ushort)source.RowId, key, source.IsLoop));
        }
        return results;
    }

    private const string LinksResource = "RSyncStageCraft.Data.BNpcLink.csv";

    public bool TryGetBNpcBase(uint bnpcBaseId, out ActorBNpcBaseRow row)
    {
        if (data.GetExcelSheet<BNpcBase>().TryGetRow(bnpcBaseId, out var source))
        {
            row = new ActorBNpcBaseRow(source.ModelChara.RowId, source.Scale, source.NpcEquip.RowId);
            return true;
        }
        row = default;
        return false;
    }

    public bool TryGetModelChara(uint modelCharaId, out ActorModelCharaRow row)
    {
        if (data.GetExcelSheet<ModelChara>().TryGetRow(modelCharaId, out var source))
        {
            row = new ActorModelCharaRow((byte)source.Type, (ushort)source.Model, (byte)source.Base, (byte)source.Variant);
            return true;
        }
        row = default;
        return false;
    }

    public bool TryGetNpcEquip(uint npcEquipId, out ActorNpcEquipRow row)
    {
        if (!data.GetExcelSheet<NpcEquip>().TryGetRow(npcEquipId, out var e))
        {
            row = null!;
            return false;
        }
        // The game's draw order: head, body, hands, legs, feet, ears, neck,
        // wrists, right ring, left ring.
        row = new ActorNpcEquipRow(
            new ActorNpcEquipWeapon((ulong)e.ModelMainHand, Dye(e.DyeMainHand.RowId), Dye(e.Dye2MainHand.RowId)),
            new ActorNpcEquipWeapon((ulong)e.ModelOffHand, Dye(e.DyeOffHand.RowId), Dye(e.Dye2OffHand.RowId)),
            [
                new((uint)e.ModelHead, Dye(e.DyeHead.RowId), Dye(e.Dye2Head.RowId)),
                new((uint)e.ModelBody, Dye(e.DyeBody.RowId), Dye(e.Dye2Body.RowId)),
                new((uint)e.ModelHands, Dye(e.DyeHands.RowId), Dye(e.Dye2Hands.RowId)),
                new((uint)e.ModelLegs, Dye(e.DyeLegs.RowId), Dye(e.Dye2Legs.RowId)),
                new((uint)e.ModelFeet, Dye(e.DyeFeet.RowId), Dye(e.Dye2Feet.RowId)),
                new((uint)e.ModelEars, Dye(e.DyeEars.RowId), Dye(e.Dye2Ears.RowId)),
                new((uint)e.ModelNeck, Dye(e.DyeNeck.RowId), Dye(e.Dye2Neck.RowId)),
                new((uint)e.ModelWrists, Dye(e.DyeWrists.RowId), Dye(e.Dye2Wrists.RowId)),
                new((uint)e.ModelRightRing, Dye(e.DyeRightRing.RowId), Dye(e.Dye2RightRing.RowId)),
                new((uint)e.ModelLeftRing, Dye(e.DyeLeftRing.RowId), Dye(e.Dye2LeftRing.RowId)),
            ]);
        return true;
    }

    public IEnumerable<uint> BNpcBaseIds() => data.GetExcelSheet<BNpcBase>().Select(row => row.RowId).ToArray();

    /// <summary>The local BNpcName sheet's singular name, or null.</summary>
    public string? NameOf(uint bnpcNameId) =>
        data.GetExcelSheet<BNpcName>().TryGetRow(bnpcNameId, out var row) ? row.Singular.ExtractText() : null;

    /// <summary>The pinned, embedded name links; empty if the resource is missing or unreadable.</summary>
    public static ActorNameLinks LoadNameLinks()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(LinksResource);
        if (stream is null)
            return ActorNameLinks.Empty;
        using var reader = new StreamReader(stream);
        return ActorNameLinks.Parse(reader);
    }

    private static byte Dye(uint stain) => stain <= byte.MaxValue ? (byte)stain : (byte)0;
}
