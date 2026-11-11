#if RSC_DEVELOPMENT
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Actors.Humans;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// HV-1 only (Debug build, dev plugin): the visible human actor's native
/// primitives through ClientStructs members resolved by Dalamud's resolver.
/// No hooks, no RSC-owned signatures, no IPC and no object-kind write. Reads
/// catch managed failures and return a value that fails closed. No member can
/// catch an access violation from a stale address: only the host's checks
/// 1–5 before each call guard against that. Game thread only.
/// </summary>
internal sealed unsafe class HumanActorNativeBackend : IHumanActorBackend
{
    // An unreadable object: no index, and reported targetable, so every check fails closed.
    private static readonly HumanPuppetFacts None = new(-1, 0, 0, -1, null, false, 0, 0, 0, false, null, true);

    public bool IsAvailable
    {
        get
        {
            try
            {
                return ClientObjectManager.Addresses.Instance.Value != 0 &&
                    ClientObjectManager.Addresses.CreateBattleCharacter.Value != 0 &&
                    ClientObjectManager.Addresses.GetObjectByIndex.Value != 0 &&
                    ClientObjectManager.Addresses.GetIndexByObject.Value != 0 &&
                    ClientObjectManager.Addresses.DeleteObjectByIndex.Value != 0 &&
                    CharacterSetupContainer.Addresses.CopyFromCharacter.Value != 0 &&
                    GameObjectStruct.Addresses.SetName.Value != 0 &&
                    GameObjectStruct.Addresses.IsReadyToDraw.Value != 0 &&
                    GameObjectManager.Addresses.Instance.Value != 0 &&
                    ClientObjectManager.Instance() != null &&
                    GameObjectManager.Instance() != null;
            }
            catch
            {
                return false;
            }
        }
    }

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

    public int CreateCharacter()
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return -1;
        var used = manager->CreateBattleCharacter();
        return used == 0xFFFFFFFF || used > int.MaxValue ? -1 : (int)used;
    }

    public nint ComEntry(int comIndex)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null || comIndex < 0 || comIndex >= manager->BattleCharas.Length)
            return 0;
        return (nint)manager->BattleCharas[comIndex].BattleChara;
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

    public nint ObjectTableEntry(int globalIndex)
    {
        var objects = GameObjectManager.Instance();
        if (objects == null || globalIndex < 0 || globalIndex >= objects->Objects.IndexSorted.Length)
            return 0;
        return (nint)objects->Objects.IndexSorted[globalIndex].Value;
    }

    public nint LocalPlayer => ObjectTableEntry(0);

    public int ObjectTableLength
    {
        get
        {
            var objects = GameObjectManager.Instance();
            return objects == null ? 0 : objects->Objects.IndexSorted.Length;
        }
    }

    public HumanPuppetScanEntry? ScanEntry(int globalIndex)
    {
        try
        {
            var gameObject = (GameObjectStruct*)ObjectTableEntry(globalIndex);
            if (gameObject == null)
                return null;
            var name = gameObject->NameString;
            if (string.IsNullOrEmpty(name))
                return null;
            var world = gameObject->IsCharacter() ? ((GameCharacter*)gameObject)->HomeWorld : (ushort)0;
            return new(globalIndex, name, world);
        }
        catch
        {
            return null;
        }
    }

    public HumanPuppetFacts ReadFacts(nint address)
    {
        try
        {
            var gameObject = (GameObjectStruct*)address;
            if (gameObject == null || !gameObject->IsCharacter())
                return None;
            var character = (GameCharacter*)address;
            var name = gameObject->NameString;
            var terminator = gameObject->Name.IndexOf((byte)0);
            var customize = new ReadOnlySpan<byte>((byte*)&character->DrawData.CustomizeData, HumanPuppetFacts.CustomizeLength).ToArray();
            return new(
                gameObject->ObjectIndex,
                (ulong)gameObject->GetGameObjectId(),
                gameObject->EntityId,
                (byte)gameObject->ObjectKind,
                name,
                terminator >= 0 && terminator == Encoding.UTF8.GetByteCount(name),
                character->HomeWorld,
                character->NameId,
                gameObject->OwnerId,
                IsHumanDrawObject(gameObject),
                customize,
                (gameObject->TargetableStatus & ObjectTargetableFlags.IsTargetable) != 0);
        }
        catch
        {
            return None;
        }
    }

    public void WriteName(nint actor, string name)
    {
        var gameObject = (GameObjectStruct*)actor;
        if (gameObject != null && !string.IsNullOrEmpty(name))
            gameObject->SetName(name);
    }

    public void WriteHomeWorld(nint actor, ushort homeWorld)
    {
        // HV-4 only, on RSC's own proven actor: this one field. A non-character is not written, so the read-back stops.
        var gameObject = (GameObjectStruct*)actor;
        if (gameObject == null || !gameObject->IsCharacter())
            return;
        ((GameCharacter*)gameObject)->HomeWorld = homeWorld;
    }

    public void CopyCharacter(nint actor, nint source)
    {
        var target = (GameCharacter*)actor;
        var from = (GameCharacter*)source;
        if (target == null || from == null)
            return;
        target->CharacterSetup.CopyFromCharacter(from,
            actor == source ? CharacterSetupContainer.CopyFlags.None : CharacterSetupContainer.CopyFlags.Position);
    }

    public HumanActorPlacement? ReadPlacement(nint address)
    {
        try
        {
            var gameObject = (GameObjectStruct*)address;
            return gameObject == null ? null : new HumanActorPlacement(gameObject->Position, gameObject->Rotation);
        }
        catch
        {
            return null;
        }
    }

    public void Place(nint actor, HumanActorPlacement placement)
    {
        var gameObject = (GameObjectStruct*)actor;
        if (gameObject == null)
            return;
        gameObject->Position = placement.Position;
        gameObject->DefaultPosition = placement.Position;
        gameObject->Rotation = placement.Rotation;
        gameObject->DefaultRotation = placement.Rotation;
    }

    public void MakeUntargetable(nint actor)
    {
        var gameObject = (GameObjectStruct*)actor;
        if (gameObject == null)
            return;
        gameObject->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        gameObject->RenderFlags |= VisibilityFlags.Nameplate;
    }

    public void SetAlpha(nint actor, float alpha)
    {
        var character = (GameCharacter*)actor;
        if (character != null)
            character->Alpha = alpha;
    }

    public bool IsReadyToDraw(nint actor)
    {
        var gameObject = (GameObjectStruct*)actor;
        return gameObject != null && gameObject->IsReadyToDraw();
    }

    public void EnableDraw(nint actor)
    {
        var gameObject = (GameObjectStruct*)actor;
        if (gameObject != null)
            gameObject->EnableDraw();
    }

    public void DisableDraw(nint actor)
    {
        var gameObject = (GameObjectStruct*)actor;
        if (gameObject != null)
            gameObject->DisableDraw();
    }

    public void DeleteComObject(int comIndex)
    {
        var manager = ClientObjectManager.Instance();
        if (manager != null && comIndex is >= 0 and <= ushort.MaxValue)
            manager->DeleteObjectByIndex((ushort)comIndex, 0);
    }

    private static bool IsHumanDrawObject(GameObjectStruct* gameObject)
    {
        var draw = gameObject->DrawObject;
        if (draw == null || ((SceneObject*)draw)->GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            return false;
        return ((CharacterBase*)draw)->GetModelType() == CharacterBase.ModelType.Human;
    }
}
#endif
