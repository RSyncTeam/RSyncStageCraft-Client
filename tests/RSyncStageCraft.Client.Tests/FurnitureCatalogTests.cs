using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

public sealed class FurnitureCatalogTests
{
    private static readonly FurnitureCatalogRow Stool = new(FurnitureSource.Indoor, 6000, 12, "Wooden Stool");
    private static readonly FurnitureCatalogRow Bench = new(FurnitureSource.Outdoor, 6000, 40, "Garden Bench");

    [Fact]
    public void ResolvesByItemAndSourceAndFailsClosed()
    {
        var catalog = new FurnitureCatalog([
            Stool,
            Bench,
            new(FurnitureSource.Indoor, 7000, 50, "Twin Table"),
            new(FurnitureSource.Indoor, 7000, 51, "Twin Table"),
            new(FurnitureSource.Indoor, 8000, 60, "Listed Twice"),
            new(FurnitureSource.Indoor, 8000, 60, "Listed Twice"),
            new(FurnitureSource.Indoor, 9000, 0, "No Model"),
        ]);

        // Indoor and outdoor rows with the same item ID stay distinct.
        Assert.Equal(Stool, catalog.Find("furniture:indoor:6000").Row);
        Assert.Equal(Bench, catalog.Find("furniture:outdoor:6000").Row);
        // One item with two different models is ambiguous, never guessed.
        Assert.Equal(FurnitureLookupStatus.Ambiguous, catalog.Find("furniture:indoor:7000").Status);
        // The same row twice is harmless.
        Assert.Equal(FurnitureLookupStatus.Found, catalog.Find("furniture:indoor:8000").Status);
        Assert.Equal(FurnitureLookupStatus.NotInCatalog, catalog.Find("furniture:indoor:9000").Status);
        Assert.Equal(FurnitureLookupStatus.NotInCatalog, catalog.Find("furniture:outdoor:7000").Status);
        Assert.Equal(FurnitureLookupStatus.InvalidIdentity, catalog.Find("chair").Status);
        Assert.Equal(FurnitureLookupStatus.InvalidIdentity, catalog.Find("furniture:indoor:06000").Status);
        Assert.Equal(3, catalog.Count);
    }

    [Fact]
    public void SearchIsCaseInsensitiveInNameOrderAndBounded()
    {
        var catalog = new FurnitureCatalog([Stool, Bench, new(FurnitureSource.Indoor, 6001, 13, "Oak Stool")]);

        Assert.Equal(["Oak Stool", "Wooden Stool"], catalog.Search("STOOL", 10).Select(row => row.Name));
        Assert.Single(catalog.Search("stool", 1));
        Assert.Empty(catalog.Search("stool", 0));
        Assert.Equal(3, catalog.Search("", 10).Count);
        Assert.Equal("furniture:outdoor:6000", Bench.Identity);
    }

    [Fact]
    public void EmptyFilterListsTheWholeCatalogueInNameOrder()
    {
        var catalog = new FurnitureCatalog([
            Stool,
            Bench,
            new(FurnitureSource.Indoor, 6001, 13, "oak stool", 52001),
            new(FurnitureSource.Outdoor, 6002, 41, "Alpine Chair"),
        ]);

        // No minimum length and no cap: everything, in name order.
        Assert.Equal(["Alpine Chair", "Garden Bench", "oak stool", "Wooden Stool"],
            catalog.Filter("", FurnitureSourceFilter.All).Select(row => row.Name));
        Assert.Equal(catalog.Rows, catalog.Filter("   ", FurnitureSourceFilter.All));
        Assert.Equal(4, catalog.Rows.Count);
        // Indoor/outdoor filtering, alone and with a name filter.
        Assert.Equal(["oak stool", "Wooden Stool"],
            catalog.Filter(null, FurnitureSourceFilter.Indoor).Select(row => row.Name));
        Assert.Equal(["Alpine Chair", "Garden Bench"],
            catalog.Filter("", FurnitureSourceFilter.Outdoor).Select(row => row.Name));
        Assert.Equal(["oak stool", "Wooden Stool"],
            catalog.Filter(" STOOL ", FurnitureSourceFilter.All).Select(row => row.Name));
        Assert.Empty(catalog.Filter("stool", FurnitureSourceFilter.Outdoor));
        // The icon is display data: carried on the row, absent means 0.
        Assert.Equal(52001u, catalog.Find("furniture:indoor:6001").Row.IconId);
        Assert.Equal(0u, Stool.IconId);
        // The full list cannot be changed through the read-only view.
        Assert.Throws<NotSupportedException>(() => ((IList<FurnitureCatalogRow>)catalog.Rows)[0] = Bench);
    }
}

public sealed class FurnitureCatalogBrowserTests
{
    private static readonly FurnitureCatalogRow Stool = new(FurnitureSource.Indoor, 6000, 12, "Wooden Stool", 52000);
    private static readonly FurnitureCatalogRow Bench = new(FurnitureSource.Outdoor, 6000, 40, "Garden Bench", 52100);
    private static readonly FurnitureCatalogRow Chair = new(FurnitureSource.Outdoor, 6002, 41, "Alpine Chair");

    [Fact]
    public void StartsWithTheFullCatalogueAndRecomputesOnlyOnChange()
    {
        var browser = new FurnitureCatalogBrowser(new FurnitureCatalog([Stool, Bench, Chair]));

        Assert.Equal([Chair, Bench, Stool], browser.Rows);
        Assert.False(browser.SetFilter("", FurnitureSourceFilter.All));
        Assert.False(browser.SetFilter("  ", FurnitureSourceFilter.All));

        Assert.True(browser.SetFilter("ch", FurnitureSourceFilter.All));
        var first = browser.Rows;
        Assert.Equal([Chair, Bench], first);
        // Same filter again (case or spacing only): the same list, not recomputed.
        Assert.False(browser.SetFilter(" CH ", FurnitureSourceFilter.All));
        Assert.Same(first, browser.Rows);

        Assert.True(browser.SetFilter("ch", FurnitureSourceFilter.Indoor));
        Assert.Empty(browser.Rows);
        Assert.True(browser.SetFilter(null, FurnitureSourceFilter.Outdoor));
        Assert.Equal([Chair, Bench], browser.Rows);
        // An undefined filter value falls back to everything.
        Assert.True(browser.SetFilter(null, (FurnitureSourceFilter)99));
        Assert.Equal(FurnitureSourceFilter.All, browser.Source);
        Assert.Equal(3, browser.Rows.Count);
    }

    [Fact]
    public void SelectionSurvivesFilteringAndRefusesForeignRows()
    {
        var browser = new FurnitureCatalogBrowser(new FurnitureCatalog([Stool, Bench, Chair]));

        Assert.True(browser.Select(Stool));
        browser.SetFilter("bench", FurnitureSourceFilter.Outdoor);
        // Filtered out of the list, but still selected (its properties stay shown).
        Assert.DoesNotContain(Stool, browser.Rows);
        Assert.Equal(Stool, browser.Selected);

        // A row this catalogue does not hold is refused; the selection stays.
        Assert.False(browser.Select(new FurnitureCatalogRow(FurnitureSource.Indoor, 9999, 1, "Elsewhere")));
        Assert.False(browser.Select(Stool with { ModelKey = 99 }));
        Assert.Equal(Stool, browser.Selected);

        Assert.True(browser.Select(null));
        Assert.Null(browser.Selected);
    }
}

public sealed class FurnitureSupportResolverTests
{
    private static readonly FurnitureCatalogRow Stool = new(FurnitureSource.Indoor, 6000, 12, "Wooden Stool");
    private static readonly FurnitureCatalogRow Swing = new(FurnitureSource.Outdoor, 6001, 7, "Swing");

    [Fact]
    public void PathsFollowTheHousingLayout()
    {
        Assert.Equal("bgcommon/hou/indoor/general/0012/asset/fun_b0_m0012.sgb",
            FurnitureModelPaths.Layout(FurnitureSource.Indoor, 12));
        Assert.Equal("bgcommon/hou/indoor/general/0012/bgparts/fun_b0_m0012.mdl",
            FurnitureModelPaths.MainModel(FurnitureSource.Indoor, 12));
        Assert.Equal("bgcommon/hou/outdoor/general/0007/asset/gar_b0_m0007.sgb",
            FurnitureModelPaths.Layout(FurnitureSource.Outdoor, 7));
        Assert.Equal("bgcommon/hou/outdoor/general/0007/bgparts/gar_b0_m0007.mdl",
            FurnitureModelPaths.MainModel(FurnitureSource.Outdoor, 7));
    }

    [Fact]
    public void SingleModelItemsAreSupportedAndCached()
    {
        var assets = new FakeFurnitureAssets();
        assets.AddSingleModel(Stool);
        assets.AddSingleModel(Swing);
        var resolver = new FurnitureSupportResolver(new FurnitureCatalog([Stool, Swing]), assets);

        var stool = resolver.Resolve("furniture:indoor:6000");
        var again = resolver.Resolve("furniture:indoor:6000");
        var swing = resolver.Resolve("furniture:outdoor:6001");

        Assert.True(stool.IsSupported);
        Assert.Equal(FurnitureModelPaths.MainModel(FurnitureSource.Indoor, 12), stool.ModelPath);
        Assert.Same(stool, again);
        Assert.Equal(FurnitureModelPaths.MainModel(FurnitureSource.Outdoor, 7), swing.ModelPath);
        Assert.Equal(2, assets.LayoutReads);
    }

    [Fact]
    public void AnythingButExactlyOneModelAtTheOriginIsReportedNotDrawnPartially()
    {
        var assets = new FakeFurnitureAssets();
        var rows = new List<FurnitureCatalogRow>();
        FurnitureCatalogRow Row(uint item, ushort key, params FurnitureLayoutInstance[] layout)
        {
            var row = new FurnitureCatalogRow(FurnitureSource.Indoor, item, key, $"Item {item}");
            rows.Add(row);
            assets.Layouts[FurnitureModelPaths.Layout(row.Source, key)] = layout;
            return row;
        }
        string Main(ushort key) => FurnitureModelPaths.MainModel(FurnitureSource.Indoor, key);
        assets.Files.Add(Main(1));
        assets.Files.Add(Main(2));
        assets.Files.Add(Main(3));
        assets.Files.Add(Main(4));

        Row(1, 1, FakeFurnitureAssets.Model(Main(1)), FakeFurnitureAssets.Model(Main(1) + "x"));
        Row(2, 2, FakeFurnitureAssets.Model(Main(2)), FakeFurnitureAssets.Other());
        Row(3, 3, FakeFurnitureAssets.Model(Main(3), new Vector3State(0, 0.5f, 0)));
        Row(4, 4, FakeFurnitureAssets.Other());
        Row(5, 5, FakeFurnitureAssets.Model(Main(5)));
        Row(6, 6, null);
        Row(7, 7, FakeFurnitureAssets.Model("chara/monster/m0001.mdl"));
        rows.Add(new(FurnitureSource.Indoor, 8, 8, "Twin"));
        rows.Add(new(FurnitureSource.Indoor, 8, 9, "Twin"));
        var resolver = new FurnitureSupportResolver(new FurnitureCatalog(rows), assets);

        Assert.Equal(FurnitureSupportStatus.Multipart, resolver.Resolve("furniture:indoor:1").Status);
        Assert.Equal(FurnitureSupportStatus.Multipart, resolver.Resolve("furniture:indoor:2").Status);
        Assert.Equal(FurnitureSupportStatus.TransformedPart, resolver.Resolve("furniture:indoor:3").Status);
        Assert.Equal(FurnitureSupportStatus.Multipart, resolver.Resolve("furniture:indoor:4").Status);
        Assert.Equal(FurnitureSupportStatus.ModelMissing, resolver.Resolve("furniture:indoor:5").Status);
        Assert.Equal(FurnitureSupportStatus.LayoutUnreadable, resolver.Resolve("furniture:indoor:6").Status);
        Assert.Equal(FurnitureSupportStatus.Multipart, resolver.Resolve("furniture:indoor:7").Status);
        Assert.Equal(FurnitureSupportStatus.Ambiguous, resolver.Resolve("furniture:indoor:8").Status);
        Assert.Equal(FurnitureSupportStatus.NotInCatalog, resolver.Resolve("furniture:indoor:99").Status);
        Assert.Equal(FurnitureSupportStatus.InvalidIdentity, resolver.Resolve("chair").Status);
        Assert.Equal(FurnitureSupportStatus.InvalidIdentity, resolver.Resolve(null).Status);
        // The player text does not name the parts.
        Assert.Equal("Has several parts (for example a built-in light); this version draws single-model items only.",
            resolver.Resolve("furniture:indoor:1").Describe());
        Assert.Equal("Has several parts (for example a built-in light); this version draws single-model items only.",
            resolver.Resolve("furniture:indoor:2").Describe());
        // The parts found are still reported, in the log text.
        Assert.Equal("Multipart item: 2 Model", resolver.Resolve("furniture:indoor:1").DescribeForLog());
        Assert.Equal("Multipart item: Model, Other", resolver.Resolve("furniture:indoor:2").DescribeForLog());
        Assert.Equal("Item 1", resolver.Resolve("furniture:indoor:1").Row!.Name);
    }

    [Fact]
    public void NonVisualHelpersDoNotMakeAnItemMultipart()
    {
        var assets = new FakeFurnitureAssets();
        var chair = new FurnitureCatalogRow(FurnitureSource.Indoor, 7000, 20, "Chair");
        var model = FurnitureModelPaths.MainModel(chair.Source, chair.ModelKey);
        assets.Files.Add(model);
        assets.Layouts[FurnitureModelPaths.Layout(chair.Source, chair.ModelKey)] =
        [
            FakeFurnitureAssets.Model(model) with { Type = "BG" },
            FakeFurnitureAssets.Helper("ChairMarker"),
            FakeFurnitureAssets.Helper("ChairMarker"),
            FakeFurnitureAssets.Helper("CollisionBox"),
        ];
        var lamp = new FurnitureCatalogRow(FurnitureSource.Indoor, 7001, 21, "Lamp");
        var lampModel = FurnitureModelPaths.MainModel(lamp.Source, lamp.ModelKey);
        assets.Files.Add(lampModel);
        assets.Layouts[FurnitureModelPaths.Layout(lamp.Source, lamp.ModelKey)] =
        [
            FakeFurnitureAssets.Model(lampModel) with { Type = "BG" },
            FakeFurnitureAssets.Helper("TargetMarker"),
            FakeFurnitureAssets.Other() with { Type = "LayLight" },
        ];
        var resolver = new FurnitureSupportResolver(new FurnitureCatalog([chair, lamp]), assets);

        var seat = resolver.Resolve(chair.Identity);
        var light = resolver.Resolve(lamp.Identity);

        Assert.True(seat.IsSupported);
        Assert.Equal("BG, 2 ChairMarker, CollisionBox", seat.Layout);
        Assert.Equal(FurnitureSupportStatus.Multipart, light.Status);
        Assert.Equal("Has several parts (for example a built-in light); this version draws single-model items only.",
            light.Describe());
        Assert.Equal("Multipart item: BG, LayLight", light.DescribeForLog());
        Assert.Equal("BG, TargetMarker, LayLight", light.Layout);
    }

    [Fact]
    public void ThrowingFileAccessFailsClosed()
    {
        var resolver = new FurnitureSupportResolver(new FurnitureCatalog([Stool]), new ThrowingAssets());

        Assert.Equal(FurnitureSupportStatus.LayoutUnreadable, resolver.Resolve("furniture:indoor:6000").Status);
    }

    private sealed class ThrowingAssets : IFurnitureAssetReader
    {
        public bool FileExists(string gamePath) => throw new IOException();
        public IReadOnlyList<FurnitureLayoutInstance> ReadLayout(string gamePath) => throw new IOException();
    }
}
