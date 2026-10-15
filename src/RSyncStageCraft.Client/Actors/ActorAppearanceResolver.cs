namespace RSyncStageCraft.Core.Actors;

/// <summary>How a creature's body is built (ModelChara type 3 or 2).</summary>
public enum ActorModelKind
{
    /// <summary>ModelChara type 3: the ModelChara row alone draws the body.</summary>
    Monster,
    /// <summary>ModelChara type 2: the body also needs the NpcEquip parts.</summary>
    Demihuman,
}

/// <summary>The BNpcBase columns the resolver reads.</summary>
public readonly record struct ActorBNpcBaseRow(uint ModelCharaId, float Scale, uint NpcEquipId);

/// <summary>The ModelChara columns the resolver reads.</summary>
public readonly record struct ActorModelCharaRow(byte Type, ushort Model, byte Base, byte Variant);

/// <summary>One NpcEquip slot as the game stores it: packed model (id, variant) and two dyes.</summary>
public readonly record struct ActorNpcEquipSlot(uint Model, byte Dye, byte Dye2);

/// <summary>One NpcEquip weapon as the game stores it: packed model (id, type, variant) and two dyes.</summary>
public readonly record struct ActorNpcEquipWeapon(ulong Model, byte Dye, byte Dye2);

/// <summary>
/// The NpcEquip columns the resolver reads. <see cref="Slots"/> is in the game's draw
/// order: head, body, hands, legs, feet, ears, neck, wrists, right ring,
/// left ring.
/// </summary>
public sealed record ActorNpcEquipRow(
    ActorNpcEquipWeapon MainHand,
    ActorNpcEquipWeapon OffHand,
    IReadOnlyList<ActorNpcEquipSlot> Slots);

/// <summary>Read-only access to the player's own game data (Lumina sheets). No network.</summary>
public interface IActorGameData
{
    bool TryGetBNpcBase(uint bnpcBaseId, out ActorBNpcBaseRow row);
    bool TryGetModelChara(uint modelCharaId, out ActorModelCharaRow row);
    bool TryGetNpcEquip(uint npcEquipId, out ActorNpcEquipRow row);

    /// <summary>Every BNpcBase row id (for the catalogue).</summary>
    IEnumerable<uint> BNpcBaseIds();
}

/// <summary>An equipment slot model, unpacked: id, variant and the two dyes.</summary>
public readonly record struct ActorEquipmentModel(ushort Id, byte Variant, byte Stain0, byte Stain1);

/// <summary>A weapon model, unpacked: id, type, variant and the two dyes.</summary>
public readonly record struct ActorWeaponModel(ushort Id, ushort Type, ushort Variant, byte Stain0, byte Stain1)
{
    public bool IsEmpty => Id == 0;
}

/// <summary>A demihuman's body parts: ten equipment slots plus the two weapons.</summary>
public sealed record ActorDemihumanEquipment(
    IReadOnlyList<ActorEquipmentModel> Slots,
    ActorWeaponModel MainHand,
    ActorWeaponModel OffHand);

/// <summary>
/// Everything needed to build one creature on this client, derived from its
/// own game data. Never sent: only <see cref="BNpcBaseId"/> travels.
/// </summary>
public sealed record ActorDrawPlan(
    uint BNpcBaseId,
    ActorModelKind Kind,
    uint ModelCharaId,
    ushort Model,
    byte Base,
    byte Variant,
    float RowScale,
    uint NpcEquipId,
    ActorDemihumanEquipment? Equipment)
{
    /// <summary>The model code shown in the UI, e.g. "m0024 b1 v1" or "d1003 b1 v2".</summary>
    public string ModelCode =>
        $"{(Kind == ActorModelKind.Monster ? 'm' : 'd')}{Model:D4} b{Base} v{Variant}";

    /// <summary>The kind as shown in the UI.</summary>
    public string KindName => Kind == ActorModelKind.Monster ? "Monster" : "Demihuman";
}

/// <summary>A draw plan, or why this client does not draw the creature.</summary>
public readonly record struct ActorDrawPlanResult(ActorDrawPlan? Plan, string? NotDrawnReason)
{
    public bool Drawable => Plan is not null;

    public static ActorDrawPlanResult NotDrawn(string reason) => new(null, reason);
}

/// <summary>
/// Maps a <c>BNpcBaseId</c> to a draw plan from the player's own game data, or
/// to "not drawn" with a reason. Pure: the same data always gives the same
/// answer. Never uses BNpcCustomize (monsters and demihumans do not use it).
/// Humans are not drawn here.
/// </summary>
public sealed class ActorAppearanceResolver
{
    public const string UnknownCreature = "Unknown creature";
    public const string DemihumanEquipmentMissing = "Demihuman equipment missing";
    public const string NotMonsterOrDemihuman = "Not a monster or demihuman";

    public const byte DemihumanModelType = 2;
    public const byte MonsterModelType = 3;
    public const int EquipmentSlotCount = 10;

    private readonly IActorGameData data;

    public ActorAppearanceResolver(IActorGameData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.data = data;
    }

    public ActorDrawPlanResult Resolve(uint bnpcBaseId)
    {
        if (bnpcBaseId == 0 ||
            !data.TryGetBNpcBase(bnpcBaseId, out var row) ||
            row.ModelCharaId == 0 ||
            !data.TryGetModelChara(row.ModelCharaId, out var model))
            return ActorDrawPlanResult.NotDrawn(UnknownCreature);

        // A missing or nonsensical row scale draws at the creature's unit size.
        var rowScale = float.IsFinite(row.Scale) && row.Scale > 0 ? row.Scale : 1f;
        switch (model.Type)
        {
            case MonsterModelType:
                return new(new ActorDrawPlan(
                    bnpcBaseId, ActorModelKind.Monster, row.ModelCharaId,
                    model.Model, model.Base, model.Variant, rowScale, row.NpcEquipId, null), null);
            case DemihumanModelType:
                if (row.NpcEquipId == 0 ||
                    !data.TryGetNpcEquip(row.NpcEquipId, out var equip) ||
                    equip.Slots.Count != EquipmentSlotCount ||
                    equip.Slots.All(slot => slot.Model == 0))
                    return ActorDrawPlanResult.NotDrawn(DemihumanEquipmentMissing);
                return new(new ActorDrawPlan(
                    bnpcBaseId, ActorModelKind.Demihuman, row.ModelCharaId,
                    model.Model, model.Base, model.Variant, rowScale, row.NpcEquipId,
                    Unpack(equip)), null);
            default:
                return ActorDrawPlanResult.NotDrawn(NotMonsterOrDemihuman);
        }
    }

    /// <summary>
    /// Unpacks NpcEquip as the game stores it. Dyes are kept. The hat-hidden
    /// flag is not part of the plan: the backend always draws slot 0, which
    /// is the demihuman's head.
    /// </summary>
    public static ActorDemihumanEquipment Unpack(ActorNpcEquipRow equip)
    {
        ArgumentNullException.ThrowIfNull(equip);
        var slots = equip.Slots
            .Select(slot => new ActorEquipmentModel(
                (ushort)(slot.Model & 0xFFFF),
                (byte)((slot.Model >> 16) & 0xFF),
                slot.Dye,
                slot.Dye2))
            .ToArray();
        return new ActorDemihumanEquipment(slots, Weapon(equip.MainHand), Weapon(equip.OffHand));
    }

    private static ActorWeaponModel Weapon(ActorNpcEquipWeapon weapon) => new(
        (ushort)(weapon.Model & 0xFFFF),
        (ushort)((weapon.Model >> 16) & 0xFFFF),
        (ushort)((weapon.Model >> 32) & 0xFFFF),
        weapon.Dye,
        weapon.Dye2);
}
