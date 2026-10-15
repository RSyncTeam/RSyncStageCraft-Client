using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using RSyncStageCraft.Core.Actors;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Client-only battle characters through ClientStructs'
/// ClientObjectManager: no hooks and no RSC-owned signatures; every member
/// function is resolved by Dalamud's ClientStructs resolver, and
/// <see cref="IsAvailable"/> is false unless all of them resolved. The object
/// exists only in this client's memory (non-networked COM range); the game
/// server never learns of it, and nothing here acts on the player. Every
/// decision (slot, verification, ownership, readiness, freeing) is made by
/// <see cref="ActorLocalObjectHost"/>; this class only performs the primitive
/// reads and writes. Game thread only.
/// </summary>
internal sealed unsafe class BattleCharaActorBackend : IActorSceneBackend
{
    public bool IsAvailable =>
        ClientObjectManager.Addresses.Instance.Value != 0 &&
        ClientObjectManager.Addresses.CreateBattleCharacter.Value != 0 &&
        ClientObjectManager.Addresses.GetObjectByIndex.Value != 0 &&
        ClientObjectManager.Addresses.GetIndexByObject.Value != 0 &&
        ClientObjectManager.Addresses.DeleteObjectByIndex.Value != 0 &&
        CharacterSetupContainer.Addresses.SetupBNpc.Value != 0 &&
        GameObjectStruct.Addresses.IsReadyToDraw.Value != 0 &&
        GameObjectStruct.Addresses.SetName.Value != 0 &&
        GameObjectManager.Addresses.Instance.Value != 0 &&
        ClientObjectManager.Instance() != null &&
        GameObjectManager.Instance() != null;

    public nint[] ComEntriesSnapshot()
    {
        var entries = new nint[ActorSlotRange.ComSlotCount];
        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return entries;
        var count = Math.Min(entries.Length, manager->BattleCharas.Length);
        for (var index = 0; index < count; index++)
            entries[index] = (nint)manager->BattleCharas[index].BattleChara;
        return entries;
    }

    public nint ComEntry(int comIndex)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null || comIndex < 0 || comIndex >= manager->BattleCharas.Length)
            return 0;
        return (nint)manager->BattleCharas[comIndex].BattleChara;
    }

    public int CreateBattleCharacter()
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return -1;
        // The automatic allocator (index 0xFFFFFFFF, param 0), as Brio, Izzy
        // and HDM use it; the return is the COM index of the new object.
        var used = manager->CreateBattleCharacter();
        return used == 0xFFFFFFFF || used > int.MaxValue ? -1 : (int)used;
    }

    public nint ComObjectByIndex(int comIndex)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null || comIndex < 0 || comIndex > ushort.MaxValue)
            return 0;
        return (nint)manager->GetObjectByIndex((ushort)comIndex);
    }

    public int ComIndexOf(nint address)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null || address == 0)
            return -1;
        var index = manager->GetIndexByObject((GameObjectStruct*)address);
        return index == 0xFFFFFFFF || index > int.MaxValue ? -1 : (int)index;
    }

    public void DeleteComObject(int comIndex)
    {
        var manager = ClientObjectManager.Instance();
        if (manager != null && comIndex is >= 0 and <= ushort.MaxValue)
            manager->DeleteObjectByIndex((ushort)comIndex, 0);
    }

    public nint ObjectTableEntry(int globalIndex)
    {
        var objects = GameObjectManager.Instance();
        if (objects == null || globalIndex < 0 || globalIndex >= objects->Objects.IndexSorted.Length)
            return 0;
        return (nint)objects->Objects.IndexSorted[globalIndex].Value;
    }

    public nint LocalPlayer => ObjectTableEntry(0);

    public ActorObjectFacts ReadFacts(nint address)
    {
        var gameObject = (GameObjectStruct*)address;
        if (gameObject == null)
            return new(-1, 0, 0, false);
        return new(
            gameObject->ObjectIndex,
            (ulong)gameObject->GetGameObjectId(),
            gameObject->EntityId,
            gameObject->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc,
            gameObject->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.None,
            gameObject->NameString,
            (int)(byte)gameObject->ObjectKind);
    }

    public void Setup(nint address, ActorDrawPlan plan, string tag)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ActorOwnershipTag.IsWellFormed(tag))
            throw new ArgumentException("Not an RSC ownership tag.", nameof(tag));
        var character = (GameCharacter*)address;
        if (character == null)
            throw new InvalidOperationException("No actor object.");
        // Invisible before anything else, so a wrong or unfinished body is never seen.
        character->Alpha = 0f;
        // The game's own BNpc setup for this row (no network involved).
        character->CharacterSetup.SetupBNpc(plan.BNpcBaseId, 0);
        // Written explicitly before the first draw, from the local plan.
        character->ModelContainer.ModelCharaId = (int)plan.ModelCharaId;
        if (plan.Equipment is { } equipment)
        {
            var slots = character->DrawData.EquipmentModelIds;
            for (var slot = 0; slot < slots.Length && slot < equipment.Slots.Count; slot++)
            {
                var part = equipment.Slots[slot];
                slots[slot] = new EquipmentModelId
                {
                    Id = part.Id,
                    Variant = part.Variant,
                    Stain0 = part.Stain0,
                    Stain1 = part.Stain1,
                };
            }
            var weapons = character->DrawData.WeaponData;
            if (weapons.Length > 1)
            {
                weapons[0].ModelId = Weapon(equipment.MainHand);
                weapons[1].ModelId = Weapon(equipment.OffHand);
            }
            // Slot 0 is the demihuman's head: never hidden.
            character->DrawData.IsHatHidden = false;
        }
        var gameObject = (GameObjectStruct*)address;
        // A fresh COM object may not have its kind yet: RSC puppets are battle NPCs.
        // A fresh client-only battle character starts as None or EventNpc (the
        // only approved fresh kinds besides BattleNpc): RSC puppets are battle NPCs.
        if (ActorObjectKinds.BecomesBattleNpcAtSetup((byte)gameObject->ObjectKind))
            gameObject->ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc;
        // Never targetable: targeting a puppet could carry it into a
        // server-visible action, chat placeholder or emote (rule zero).
        gameObject->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        // The per-spawn ownership nonce, read back before every later write
        // and the delete (one of several facts; never sufficient alone).
        gameObject->SetName(tag);
        gameObject->RenderFlags |= VisibilityFlags.Nameplate;
    }

    public void SetTransform(nint address, Vector3 position, float yaw, float scale)
    {
        var gameObject = (GameObjectStruct*)address;
        if (gameObject == null)
            throw new InvalidOperationException("No actor object.");
        gameObject->Position = position;
        gameObject->DefaultPosition = position;
        gameObject->Rotation = yaw;
        gameObject->DefaultRotation = yaw;
        gameObject->Scale = scale;
        // Kept off on every write, in case the setup or the first draw set it again.
        gameObject->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        var draw = gameObject->DrawObject;
        if (draw != null)
        {
            var sceneObject = (SceneObject*)draw;
            sceneObject->Position = position;
            sceneObject->Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            sceneObject->Scale = new Vector3(scale);
        }
    }

    public void SetAlpha(nint address, float alpha)
    {
        var character = (GameCharacter*)address;
        if (character != null)
            character->Alpha = alpha;
    }

    public bool IsReadyToDraw(nint address)
    {
        var gameObject = (GameObjectStruct*)address;
        return gameObject != null && gameObject->IsReadyToDraw();
    }

    public void EnableDraw(nint address)
    {
        var gameObject = (GameObjectStruct*)address;
        if (gameObject != null)
            gameObject->EnableDraw();
    }

    public void DisableDraw(nint address)
    {
        var gameObject = (GameObjectStruct*)address;
        if (gameObject != null)
            gameObject->DisableDraw();
    }

    public bool HasDrawnModel(nint address, uint modelCharaId)
    {
        var character = (GameCharacter*)address;
        if (character == null || character->ModelContainer.ModelCharaId != (int)modelCharaId)
            return false;
        var draw = ((GameObjectStruct*)address)->DrawObject;
        if (draw == null || ((SceneObject*)draw)->GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            return false;
        var type = ((CharacterBase*)draw)->GetModelType();
        return type is CharacterBase.ModelType.Monster or CharacterBase.ModelType.DemiHuman;
    }

    private static WeaponModelId Weapon(ActorWeaponModel weapon) => new()
    {
        Id = weapon.Id,
        Type = weapon.Type,
        Variant = weapon.Variant,
        Stain0 = weapon.Stain0,
        Stain1 = weapon.Stain1,
    };
}
