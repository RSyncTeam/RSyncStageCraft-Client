using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using RSyncStageCraft.Core.Lights;
using RenderLight = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Light;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Client-only point lights through ClientStructs' <c>Scene.Light.Create</c>
/// (no hooks, no RSC-owned signatures). They exist only in this client's
/// scene; the game server never learns of them. Creation, the transform
/// hookup and <c>MaxRange</c> follow Stagehand; refusing a light without its
/// render part, the apply-on-change update sequence and hide, render cleanup,
/// then free (for every created light, shown or not) follow
/// Izzy's Furniture.
/// Game thread only.
/// </summary>
internal sealed unsafe class SceneLightBackend : ILightSceneBackend
{
    private const byte DestroyFlagsFree = 1;
    private static readonly byte[] Pool = Encoding.ASCII.GetBytes("RSyncStageCraft.Light\0");

    public LightCreateResult Create()
    {
        SceneLight* light;
        fixed (byte* pool = Pool)
            light = SceneLight.Create(LightShape.PointLight, pool, null);
        if (light == null)
            return new(LightCreateStatus.Failed, 0);
        if (light->RenderLight == null)
        {
            // A partial allocation: free it at once (never shown, so no render cleanup).
            ((SceneObject*)light)->Dtor(DestroyFlagsFree);
            return new(LightCreateStatus.IncompleteFreed, 0);
        }
        ((DrawObject*)light)->IsVisible = false;
        return new(LightCreateStatus.Created, (nint)light);
    }

    public void Apply(nint instance, NativeLightSettings settings)
    {
        var light = (SceneLight*)instance;
        if (light == null)
            return;
        var render = light->RenderLight;
        if (render == null)
            throw new InvalidOperationException("The light lost its render part.");

        var sceneObject = (SceneObject*)light;
        sceneObject->Position = settings.Position;
        sceneObject->Rotation = System.Numerics.Quaternion.Identity;
        // Point lights have a fixed unit scale; range is its own value.
        sceneObject->Scale = System.Numerics.Vector3.One;

        render->Transform = (Transform*)&sceneObject->Position;
        render->LightShape = LightShape.PointLight;
        render->LightFlags = LightFlags.SpecularHighlights;
        render->Color = settings.Color;
        render->Intensity = settings.Intensity;
        render->Range = settings.Range;
        render->MaxRange = RenderLight.UnlimitedMaxRange;
        render->FalloffType = LightFalloffType.Quadratic;
        render->FalloffFactor = 1f;

        var drawObject = (DrawObject*)light;
        drawObject->NotifyTransformChanged();
        drawObject->UpdateTransforms(false);
        drawObject->UpdateCulling();
        drawObject->UpdateMaterials();
        sceneObject->UpdateRender();
    }

    public void Show(nint instance)
    {
        var light = (SceneLight*)instance;
        if (light != null)
            ((DrawObject*)light)->IsVisible = true;
    }

    // Freeing a created light (it always has its render part; Create frees a
    // partial one itself): hide, CleanupRender, then Dtor(1), as Izzy does,
    // whether or not it was ever shown.

    public void Hide(nint instance)
    {
        var light = (SceneLight*)instance;
        if (light != null)
            ((DrawObject*)light)->IsVisible = false;
    }

    public void CleanupRender(nint instance)
    {
        var light = (SceneLight*)instance;
        if (light != null && light->RenderLight != null)
            ((SceneObject*)light)->CleanupRender();
    }

    public void Free(nint instance)
    {
        var light = (SceneLight*)instance;
        if (light != null)
            ((SceneObject*)light)->Dtor(DestroyFlagsFree);
    }
}
