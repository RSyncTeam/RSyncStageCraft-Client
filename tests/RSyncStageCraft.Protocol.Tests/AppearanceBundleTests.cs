#nullable enable
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>The immutable, owner-authored appearance bundle contract.</summary>
public sealed class AppearanceBundleTests
{
    private static readonly RscUserId Owner = new("alice");
    private const string Top = "chara/equipment/e0001/model/c0101e0001_top.mdl";
    private const string Texture = "chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex";

    [Fact]
    public void AContentHashIsSha256InLowercaseHexAndOnlyThatFormParses()
    {
        var abc = ContentHash.Of(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", abc.Value);
        Assert.True(abc.IsValid);
        Assert.True(ContentHash.TryParse(abc.Value, out var parsed));
        Assert.Equal(abc, parsed);
        // Never normalized: upper case, wrong length or non-hex are refused.
        Assert.False(ContentHash.TryParse(abc.Value.ToUpperInvariant(), out _));
        Assert.False(ContentHash.TryParse(abc.Value[..63], out _));
        Assert.False(ContentHash.TryParse(abc.Value[..63] + "g", out _));
        Assert.False(ContentHash.TryParse(null, out _));
        Assert.False(default(ContentHash).IsValid);
    }

    [Fact]
    public void TheCanonicalFormIsPinned()
    {
        var glamourer = Blob("g", 10);
        var file = Blob("top", 2048);
        var manifest = Create(glamourer, null, null, [new AppearanceFileEntry(Top, file)]);

        var expected =
            "{\"formatVersion\":1,\"ownerId\":\"alice\"," +
            $"\"glamourer\":{{\"hash\":\"{glamourer.Hash.Value}\",\"size\":10}}," +
            "\"penumbraMeta\":null,\"customizePlus\":null," +
            $"\"files\":[{{\"gamePath\":\"{Top}\",\"hash\":\"{file.Hash.Value}\",\"size\":2048}}]}}";
        Assert.Equal(expected, Encoding.UTF8.GetString(manifest.CanonicalBytes));
        Assert.Equal(ContentHash.Of(Encoding.UTF8.GetBytes(expected)), manifest.BundleId);
        Assert.Equal(1, manifest.FormatVersion);
    }

    [Fact]
    public void TheBundleIdDependsOnContentAndOwnerNeverOnInputOrder()
    {
        var glamourer = Blob("g", 10);
        AppearanceFileEntry[] files = [new(Top, Blob("top", 2048)), new(Texture, Blob("tex", 4096))];
        var first = Create(glamourer, null, null, files);
        var reversed = Create(glamourer, null, null, Enumerable.Reverse(files));

        Assert.Equal(first.BundleId, reversed.BundleId);
        Assert.Equal(first.CanonicalBytes, reversed.CanonicalBytes);
        Assert.Equal(new[] { Top, Texture }.Order(StringComparer.Ordinal), first.Files.Select(file => file.GamePath));

        // Owner-authored: the same content by another owner is another bundle.
        var other = AppearanceBundleManifest.TryCreate(new RscUserId("bob"), glamourer, null, null, files, out var error);
        Assert.Null(error);
        Assert.NotEqual(first.BundleId, other!.BundleId);
        // Any change of content is a different bundle (immutable by identity).
        Assert.NotEqual(first.BundleId, Create(Blob("g2", 10), null, null, files).BundleId);
        Assert.NotEqual(first.BundleId, Create(glamourer, Blob("meta", 7), null, files).BundleId);
        Assert.NotEqual(first.BundleId, Create(glamourer, null, Blob("cplus", 9), files).BundleId);
        Assert.NotEqual(first.BundleId, Create(glamourer, null, null, files.Take(1)).BundleId);
    }

    [Fact]
    public void TheCanonicalFormRoundTripsAndNoOtherFormIsAccepted()
    {
        var manifest = Create(Blob("g", 10), Blob("meta", 7), null,
            [new AppearanceFileEntry(Top, Blob("top", 2048)), new AppearanceFileEntry(Texture, Blob("tex", 4096))]);
        var canonical = Encoding.UTF8.GetString(manifest.CanonicalBytes);

        var parsed = AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, out var error);
        Assert.Null(error);
        Assert.Equal(manifest.BundleId, parsed!.BundleId);
        Assert.Equal(manifest.Files, parsed.Files);
        Assert.Equal(manifest.PenumbraMeta, parsed.PenumbraMeta);
        Assert.Null(parsed.CustomizePlus);

        string[] altered =
        [
            canonical.Replace("{\"formatVersion\"", "{ \"formatVersion\""),                       // whitespace
            canonical[..^1] + ",\"extra\":1}",                                                    // unknown member
            canonical.Replace("\"customizePlus\":null,", ""),                                    // missing member
            canonical.Replace("\"formatVersion\":1", "\"formatVersion\":2"),                     // another format
            canonical.Replace(manifest.Glamourer.Hash.Value, manifest.Glamourer.Hash.Value.ToUpperInvariant()), // hash form
            canonical.Replace("{\"formatVersion\":1,\"ownerId\":\"alice\"", "{\"ownerId\":\"alice\",\"formatVersion\":1"), // member order
            SwapFiles(canonical),                                                                 // unsorted files
            canonical.Replace("\"size\":2048", "\"size\":0"),                                     // empty blob
            canonical.Replace("\"customizePlus\":null,", "\"customizePlus\":null,\"customizePlus\":null,"), // duplicate member
            canonical.Replace(Top, "chara/equipment/e0001/animation/a0001/c0101e0001_top.pap"),  // refused file type
            "[]",
            "not json",
            "",
        ];
        foreach (var text in altered)
        {
            Assert.Null(AppearanceBundleManifest.TryParse(Encoding.UTF8.GetBytes(text), out error));
            Assert.NotNull(error);
        }
    }

    [Theory]
    [InlineData(Top, true)]
    [InlineData("chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl", true)]
    [InlineData("vfx/common/eff/x.avfx", true)]  // syntax only; the file type is refused separately
    [InlineData("/chara/x.mdl", false)]      // absolute
    [InlineData("chara/x.mdl/", false)]      // trailing slash
    [InlineData("chara//x.mdl", false)]      // empty segment
    [InlineData("chara/../x.mdl", false)]    // parent segment
    [InlineData("chara/./x.mdl", false)]     // current segment
    [InlineData("chara\\x.mdl", false)]      // backslash
    [InlineData("Chara/x.mdl", false)]       // upper case: never normalized
    [InlineData("chara/x", false)]           // no extension
    [InlineData("chara/.mdl", false)]        // no file name
    [InlineData("x.mdl", false)]             // no folder
    [InlineData("c:/chara/x.mdl", false)]    // a disk path
    [InlineData("chara/x y.mdl", false)]     // space
    [InlineData("", false)]
    public void GamePathsAreStrictRelativeGamePaths(string path, bool valid) =>
        Assert.Equal(valid, AppearanceGamePath.IsValid(path));

    [Fact]
    public void TheFileTypeAllowlistIsVersionTwoAndItsVersionOneListIsModelsMaterialsAndTexturesOnly()
    {
        // Pinned: any change to the allowlist is an explicit, reviewed compatibility decision.
        // Version 2 (AM-4) adds skeleton resources on recognised paths only, for format 2;
        // the version-1 list, and so every format-1 manifest, is unchanged.
        Assert.Equal(2, AppearanceFileTypes.Version);
        Assert.Equal(AppearanceLimits.FormatVersion, AppearanceFileTypes.Version);
        Assert.Equal(new[] { ".mdl", ".mtrl", ".tex" }, AppearanceFileTypes.Allowed);
    }

    [Theory]
    [InlineData("chara/x.mdl", true)]
    [InlineData("chara/x.mtrl", true)]
    [InlineData("chara/x.tex", true)]
    [InlineData("chara/x.pap", false)]      // animation
    [InlineData("chara/x.tmb", false)]      // timeline
    [InlineData("chara/x.scd", false)]      // sound
    [InlineData("vfx/x.avfx", false)]       // visual effect
    [InlineData("shader/x.shpk", false)]    // shader
    [InlineData("chara/x.atex", false)]
    [InlineData("chara/x.exe", false)]
    [InlineData("chara/x.dll", false)]
    [InlineData("chara/x.lua", false)]
    [InlineData("chara/x.zip", false)]
    [InlineData("chara/x.mdl.pap", false)]  // only the last extension counts
    [InlineData("chara/x.tex.bak", false)]
    [InlineData("chara/x.texx", false)]
    [InlineData("chara/xmdl", false)]
    [InlineData("chara/.mdl", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAllowlistedFileTypesAreAccepted(string? path, bool allowed)
    {
        Assert.Equal(allowed, AppearanceFileTypes.IsAllowed(path));
        if (path is not null && AppearanceGamePath.IsValid(path))
            Assert.Equal(allowed, new AppearanceFileEntry(path, Blob("x", 1)).IsValid);
    }

    [Fact]
    public void TheLargestPermittedManifestFitsTheManifestLimit()
    {
        // 4096 files with 260-character paths: more than 1 MiB, within the 2 MiB limit.
        var owner = new RscUserId(new string('z', ProtocolLimits.MaximumIdentifierLength));
        var files = Enumerable.Range(0, AppearanceLimits.MaximumFiles)
            .Select(index => new AppearanceFileEntry($"chara/{index:D4}/{new string('a', 245)}.mdl",
                new AppearanceBlobRef(ContentHash.Of(BitConverter.GetBytes(index)), 1)))
            .ToArray();
        Assert.All(files, file => Assert.Equal(AppearanceLimits.MaximumGamePathLength, file.GamePath.Length));

        var manifest = AppearanceBundleManifest.TryCreate(owner, Blob("g", 10), Blob("meta", 7), Blob("cplus", 9), files, out var error);
        Assert.Null(error);
        Assert.InRange(manifest!.CanonicalBytes.Length, 1024 * 1024 + 1, AppearanceLimits.MaximumManifestBytes);
        Assert.Equal(manifest.BundleId, AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, out error)!.BundleId);
        Assert.Null(error);
    }

    [Fact]
    public void AGamePathOverTheLimitIsRefused()
    {
        var path = "chara/" + new string('a', AppearanceLimits.MaximumGamePathLength) + ".mdl";
        Assert.False(AppearanceGamePath.IsValid(path));
    }

    [Fact]
    public void InvalidBundlesAreRefusedWithAReason()
    {
        var glamourer = Blob("g", 10);
        var top = Blob("top", 2048);
        (RscUserId Owner, AppearanceBlobRef Glamourer, AppearanceBlobRef? Meta, IEnumerable<AppearanceFileEntry> Files, string Reason)[] cases =
        [
            (new RscUserId(""), glamourer, null, [], "owner"),
            (Owner, new AppearanceBlobRef(default, 10), null, [], "Glamourer"),
            (Owner, glamourer, new AppearanceBlobRef(Blob("meta", 1).Hash, 0), [], "meta"),
            (Owner, glamourer, null, [new(Top, top), new(Top, Blob("other", 5))], "more than once"),
            (Owner, glamourer, null, [new(Top, top), new(Top, top)], "more than once"),
            (Owner, glamourer, null, [new("chara/action/a.pap", top)], "file type is not allowed"),
            (Owner, glamourer, null, [new(Top, top), new(Texture, top with { Size = 99 })], "different sizes"),
            (Owner, glamourer, null, [new("/abs.mdl", top)], "redirect is invalid"),
            (Owner, glamourer, null, [new(Top, top with { Size = AppearanceLimits.MaximumBlobBytes + 1 })], "redirect is invalid"),
            (Owner, glamourer, null,
                Enumerable.Range(0, AppearanceLimits.MaximumFiles + 1).Select(index => new AppearanceFileEntry($"chara/f/{index}.tex", top)),
                "Too many files"),
            (Owner, glamourer, null,
                Enumerable.Range(0, 17).Select(index => new AppearanceFileEntry($"chara/f/{index}.tex",
                    new AppearanceBlobRef(ContentHash.Of([(byte)index]), AppearanceLimits.MaximumBlobBytes))),
                "too large"),
        ];
        foreach (var (owner, glam, meta, files, reason) in cases)
        {
            Assert.Null(AppearanceBundleManifest.TryCreate(owner, glam, meta, null, files, out var error));
            Assert.Contains(reason, error);
        }
    }

    [Fact]
    public void TheBlobsAreListedOnceEachWithTheirTotal()
    {
        var shared = Blob("shared", 100);
        var manifest = Create(Blob("g", 10), Blob("meta", 7), Blob("cplus", 9),
            [new AppearanceFileEntry(Top, shared), new AppearanceFileEntry(Texture, shared)]);
        Assert.Equal(4, manifest.Blobs.Count);
        Assert.Equal(10 + 7 + 9 + 100, manifest.TotalBytes);
    }

    private static AppearanceBlobRef Blob(string content, long size) =>
        new(ContentHash.Of(Encoding.UTF8.GetBytes(content)), size);

    private static AppearanceBundleManifest Create(AppearanceBlobRef glamourer, AppearanceBlobRef? meta, AppearanceBlobRef? customize,
        IEnumerable<AppearanceFileEntry> files)
    {
        var manifest = AppearanceBundleManifest.TryCreate(Owner, glamourer, meta, customize, files, out var error);
        Assert.Null(error);
        return manifest!;
    }

    /// <summary>The canonical text with its two file entries swapped (so the list is no longer sorted).</summary>
    private static string SwapFiles(string canonical)
    {
        var start = canonical.IndexOf("\"files\":[", StringComparison.Ordinal) + "\"files\":[".Length;
        var end = canonical.LastIndexOf(']');
        var entries = canonical[start..end];
        var split = entries.IndexOf("},{", StringComparison.Ordinal) + 1;
        return canonical[..start] + entries[(split + 1)..] + "," + entries[..split] + canonical[end..];
    }
}
