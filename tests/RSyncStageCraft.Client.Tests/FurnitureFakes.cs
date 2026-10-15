using System.Numerics;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>Game files for tests: layouts by path, and which files exist.</summary>
internal sealed class FakeFurnitureAssets : IFurnitureAssetReader
{
    internal Dictionary<string, IReadOnlyList<FurnitureLayoutInstance>> Layouts { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Files { get; } = new(StringComparer.Ordinal);
    internal int LayoutReads { get; private set; }

    public bool FileExists(string gamePath) => Files.Contains(gamePath);

    /// <summary>Layout path to the item's default housing stain (absent = 0).</summary>
    internal Dictionary<string, byte> DefaultStains { get; } = new(StringComparer.Ordinal);

    public byte ReadDefaultStain(string gamePath) => DefaultStains.GetValueOrDefault(gamePath);

    public IReadOnlyList<FurnitureLayoutInstance> ReadLayout(string gamePath)
    {
        LayoutReads++;
        return Layouts.TryGetValue(gamePath, out var layout) ? layout : null;
    }

    /// <summary>A single-model item: its layout holds only its main model at the origin.</summary>
    internal void AddSingleModel(FurnitureCatalogRow row)
    {
        var model = FurnitureModelPaths.MainModel(row.Source, row.ModelKey);
        Layouts[FurnitureModelPaths.Layout(row.Source, row.ModelKey)] = [Model(model)];
        Files.Add(model);
    }

    internal static FurnitureLayoutInstance Model(string path, Vector3State? translation = null) =>
        new(FurnitureLayoutInstanceKind.Model, path, translation ?? new(0, 0, 0), new(0, 0, 0), new(1, 1, 1));

    internal static FurnitureLayoutInstance Helper(string type) =>
        new(FurnitureLayoutInstanceKind.Helper, null, new(0, 0, 0), new(0, 0, 0), new(1, 1, 1), type);

    internal static FurnitureLayoutInstance Other() =>
        new(FurnitureLayoutInstanceKind.Other, null, new(0, 0, 0), new(0, 0, 0), new(1, 1, 1));
}

/// <summary>Client-only objects for tests; a model loads after a number of checks.</summary>
internal sealed class FakeFurnitureBackend : IFurnitureSceneBackend
{
    private nint next = 0x1000;

    internal Dictionary<nint, Instance> Live { get; } = new();
    internal List<nint> Destroyed { get; } = new();
    internal int ChecksUntilLoaded { get; set; }
    internal bool FailCreate { get; set; }
    internal int Created { get; private set; }

    public nint Create(string modelPath)
    {
        if (FailCreate)
            return 0;
        Created++;
        var instance = next;
        next += 0x10;
        Live[instance] = new Instance(modelPath) { ChecksLeft = ChecksUntilLoaded };
        return instance;
    }

    internal bool ThrowOnCheck { get; set; }

    public bool IsLoaded(nint instance)
    {
        if (ThrowOnCheck)
            throw new InvalidOperationException("simulated native fault");
        var live = Live[instance];
        if (live.ChecksLeft <= 0)
            return live.Loaded = true;
        live.ChecksLeft--;
        return false;
    }

    internal int TransformWrites { get; private set; }
    internal bool ThrowOnTransform { get; set; }
    internal int TransformFaults { get; private set; }

    public void SetTransform(nint instance, Vector3 position, Quaternion rotation, bool loaded)
    {
        if (ThrowOnTransform)
        {
            TransformFaults++;
            throw new InvalidOperationException("simulated native transform fault");
        }
        TransformWrites++;
        Assert.True(float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z),
            "A non-finite position reached the native object.");
        Assert.InRange(rotation.Length(), 0.999f, 1.001f);
        var live = Live[instance];
        live.Position = position;
        live.Rotation = rotation;
    }

    internal int Shows { get; private set; }

    public void Show(nint instance)
    {
        var live = Live[instance];
        Assert.True(live.Loaded, "An instance was shown before its model loaded.");
        Shows++;
        live.Shown = true;
    }

    /// <summary>Polls before the stain buffer exists (per instance, counted from its first stain call).</summary>
    internal int StainChecksUntilReady { get; set; }
    internal bool ThrowOnStain { get; set; }
    internal List<(nint Instance, FurnitureStainColor Color)> StainCalls { get; } = new();

    public bool TrySetStain(nint instance, FurnitureStainColor color)
    {
        if (ThrowOnStain)
            throw new InvalidOperationException("simulated native stain fault");
        StainCalls.Add((instance, color));
        var live = Live[instance];
        Assert.True(live.Loaded, "A stain was set before the model loaded.");
        if (live.StainChecks++ < StainChecksUntilReady)
            return false;
        live.Stain = color;
        return true;
    }

    public void Destroy(nint instance)
    {
        Assert.True(Live.Remove(instance), "Destroyed an instance that was not live.");
        Destroyed.Add(instance);
    }

    internal sealed class Instance(string modelPath)
    {
        internal string ModelPath { get; } = modelPath;
        internal int ChecksLeft { get; set; }
        internal Vector3 Position { get; set; }
        internal Quaternion Rotation { get; set; }
        internal bool Shown { get; set; }
        internal bool Loaded { get; set; }
        internal int StainChecks { get; set; }
        internal FurnitureStainColor? Stain { get; set; }
    }
}
