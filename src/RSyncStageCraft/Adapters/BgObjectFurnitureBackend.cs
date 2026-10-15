using System.Numerics;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using RSyncStageCraft.Core.Furniture;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Draws furniture as client-only background objects (BgObject). They exist
/// only in this client's memory: the game server never learns of them. The
/// lifecycle follows Stagehand's BgObject handling and Izzy's Furniture
/// (create hidden, show once the model has loaded, CleanupRender only after
/// the object entered the scene, then Dtor).
/// Game thread only.
/// </summary>
internal sealed unsafe class BgObjectFurnitureBackend : IFurnitureSceneBackend
{
    private const int LoadedState = 7;
    private const int DestroyFlagsFree = 1;
    private static readonly byte[] Pool = "RSyncStageCraft.Furniture\0"u8.ToArray();

    // Instances that were shown at least once; only those need CleanupRender.
    private readonly HashSet<nint> activated = new();

    public nint Create(string modelPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        var path = new byte[Encoding.UTF8.GetByteCount(modelPath) + 1];
        Encoding.UTF8.GetBytes(modelPath, path);
        BgObject* created;
        fixed (byte* pathPointer = path)
        fixed (byte* poolPointer = Pool)
        {
            created = BgObject.Create(pathPointer, poolPointer, null);
        }
        if (created == null)
            return 0;
        created->IsVisible = false;
        return (nint)created;
    }

    public bool IsLoaded(nint instance)
    {
        var bgObject = (BgObject*)instance;
        return bgObject != null &&
            bgObject->ModelResourceHandle != null &&
            bgObject->ModelResourceHandle->LoadState == LoadedState;
    }

    public void SetTransform(nint instance, Vector3 position, Quaternion rotation, bool loaded)
    {
        var bgObject = (BgObject*)instance;
        if (bgObject == null)
            return;
        bgObject->Position = position;
        bgObject->Rotation = rotation;
        // Furniture has no scale.
        bgObject->Scale = Vector3.One;
        ((DrawObject*)bgObject)->NotifyTransformChanged();
        if (!loaded)
            return;
        bgObject->UpdateTransforms(false);
    }

    public void Show(nint instance)
    {
        var bgObject = (BgObject*)instance;
        if (bgObject == null)
            return;
        bgObject->IsVisible = true;
        activated.Add(instance);
    }

    /// <summary>
    /// ClientStructs' hook-free <c>BgObject.TrySetStainColor</c> (as Stagehand
    /// uses it): false until the model's stain buffer exists.
    /// </summary>
    public bool TrySetStain(nint instance, FurnitureStainColor color)
    {
        var bgObject = (BgObject*)instance;
        if (bgObject == null)
            return false;
        return bgObject->TrySetStainColor(new ByteColor { R = color.R, G = color.G, B = color.B, A = color.A });
    }

    public void Destroy(nint instance)
    {
        var bgObject = (BgObject*)instance;
        if (bgObject == null)
            return;
        bgObject->IsVisible = false;
        if (activated.Remove(instance))
            bgObject->CleanupRender();
        bgObject->Dtor(DestroyFlagsFree);
    }
}
