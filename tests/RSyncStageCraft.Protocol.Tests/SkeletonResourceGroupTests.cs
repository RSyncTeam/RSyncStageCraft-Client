#nullable enable
using System.Buffers.Binary;
using System.Text;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The skeleton-resource classifier, structural checks and all-or-nothing group
/// assembly (SK-P1). Every byte here is synthetic: no game file or third-party
/// mod content is used.
/// </summary>
public sealed class SkeletonResourceGroupTests
{
    private const ushort Hyur = 101;
    private const ushort Viera = 1801;

    private const string BaseSklb = "chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb";
    private const string BasePhyb = "chara/human/c1801/skeleton/base/b0001/phy_c1801b0001.phyb";
    private const string HairSklb = "chara/human/c1801/skeleton/hair/h0003/skl_c1801h0003.sklb";
    private const string HeadSklbFallback = "chara/human/c0201/skeleton/met/m6020/skl_c0201m6020.sklb";
    private const string Deformer = SkeletonResourcePaths.DeformerGamePath;

    // ---------- synthetic bytes ----------

    /// <summary>
    /// A synthetic .sklb header. The version is a little-endian u32 of the four
    /// ASCII digits read as a big-endian number, so "1301" is 0x31333031 and is
    /// stored on disk as the bytes "1031".
    /// </summary>
    private static byte[] Sklb(string version = "1301", uint skeletonOffset = 16, int payload = 32)
    {
        var oldHeader = version is "1100" or "1110" or "1200";
        var data = new byte[(oldHeader ? 12 : 16) + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x736B6C62);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(version)));
        if (oldHeader)
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)skeletonOffset);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), skeletonOffset);
        return data;
    }

    private static byte[] Phyb(uint version = 1, uint collision = 16, uint simulator = 24, int length = 32)
    {
        var data = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, version);
        var at = version == 0 ? 4 : 8;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), collision);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 4), simulator);
        return data;
    }

    /// <summary>Entries for c0101 and c1801; c0101 has an empty body, c1801 a 4-byte body after the tables.</summary>
    private static byte[] Pbd(ushort[]? races = null, int? count = null, int? bodyOffset = null, bool trailer = false, ulong? trailerTotal = null)
    {
        races ??= [Hyur, Viera];
        var n = races.Length;
        var tablesEnd = 4 + n * 20;
        var core = new byte[tablesEnd + 4];
        BinaryPrimitives.WriteInt32LittleEndian(core, count ?? n);
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(4 + i * 12), races[i]);
            BinaryPrimitives.WriteInt32LittleEndian(core.AsSpan(4 + i * 12 + 4), i == n - 1 ? bodyOffset ?? tablesEnd : 0);
        }
        if (!trailer)
            return core;
        var pack = new byte[16 + 8 + 24];
        Encoding.ASCII.GetBytes("EPBD").CopyTo(pack, 0);
        var footer = pack.AsSpan(24);
        Encoding.ASCII.GetBytes("PACK").CopyTo(pack, 24);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[16..], trailerTotal ?? (ulong)pack.Length);
        return [.. core, .. pack];
    }

    private static SkeletonGroupCandidate C(string path, byte[] bytes) => new(path, bytes);

    private static SkeletonGroupResult Assemble(ushort race, string[] required, params SkeletonGroupCandidate[] offered) =>
        SkeletonResourceGroupAssembler.Assemble(race, required, offered);

    // ---------- classifier ----------

    [Theory]
    [InlineData(BaseSklb, SkeletonResourceRole.Skeleton, SkeletonPartialSlot.Base, 1801, 1)]
    [InlineData(BasePhyb, SkeletonResourceRole.Physics, SkeletonPartialSlot.Base, 1801, 1)]
    [InlineData("chara/human/c0101/skeleton/face/f0004/skl_c0101f0004.sklb", SkeletonResourceRole.Skeleton, SkeletonPartialSlot.Face, 101, 4)]
    [InlineData(HairSklb, SkeletonResourceRole.Skeleton, SkeletonPartialSlot.Hair, 1801, 3)]
    [InlineData(HeadSklbFallback, SkeletonResourceRole.Skeleton, SkeletonPartialSlot.Head, 201, 6020)]
    [InlineData("chara/human/c0904/skeleton/top/t0100/phy_c0904t0100.phyb", SkeletonResourceRole.Physics, SkeletonPartialSlot.Body, 904, 100)]
    [InlineData(Deformer, SkeletonResourceRole.Deformer, SkeletonPartialSlot.None, 0, 0)]
    public void RecognisedPathsAreClassified(string gamePath, SkeletonResourceRole role, SkeletonPartialSlot slot, int race, int set)
    {
        Assert.True(SkeletonResourcePaths.TryClassify(gamePath, out var path));
        Assert.Equal(new SkeletonResourcePath(gamePath, role, slot, (ushort)race, (ushort)set), path);
    }

    [Theory]
    // Not canonical: extension matching alone must not accept these.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chara/xls/boneDeformer/human.pbd")]
    [InlineData("Chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb")]
    [InlineData("/chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb")]
    [InlineData("chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb/")]
    [InlineData("chara/human/c1801/skeleton/base//b0001/skl_c1801b0001.sklb")]
    [InlineData("chara/human/c1801/skeleton/../base/b0001/skl_c1801b0001.sklb")]
    [InlineData("chara\\human\\c1801\\skeleton\\base\\b0001\\skl_c1801b0001.sklb")]
    [InlineData("other.sklb")]
    [InlineData("x/human.pbd")]
    // Canonical but not a recognised family.
    [InlineData("chara/xls/bonedeformer/human_extra.pbd")]
    [InlineData("chara/xls/bonedeformer/other.pbd")]
    [InlineData("chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.skp")]
    [InlineData("chara/human/c1801/skeleton/base/b0001/kdi_c1801b0001.kdb")]
    [InlineData("chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.bnmb")]
    [InlineData("chara/human/c1801/skeleton/base/b0002/skl_c1801b0002.sklb")]
    [InlineData("chara/human/c1801/skeleton/hair/h0000/skl_c1801h0000.sklb")]
    [InlineData("chara/human/c1801/skeleton/hair/f0003/skl_c1801f0003.sklb")]
    [InlineData("chara/human/c1801/skeleton/hair/h0003/skl_c1801h0004.sklb")]
    [InlineData("chara/human/c1801/skeleton/hair/h0003/skl_c0101h0003.sklb")]
    [InlineData("chara/human/c1801/skeleton/hair/h003/skl_c1801h003.sklb")]
    [InlineData("chara/human/c1801/skeleton/tail/l0003/skl_c1801l0003.sklb")]
    [InlineData("chara/human/c1901/skeleton/base/b0001/skl_c1901b0001.sklb")]
    [InlineData("chara/human/c18011/skeleton/base/b0001/skl_c18011b0001.sklb")]
    [InlineData("chara/monster/m0001/skeleton/base/b0001/skl_m0001b0001.sklb")]
    [InlineData("chara/human/c1801/skeleton/base/b0001/extra/skl_c1801b0001.sklb")]
    [InlineData("chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.mdl")]
    [InlineData("chara/common/animation/skl_material.sklb")]
    public void AnythingElseIsNotASkeletonResource(string? gamePath)
    {
        Assert.False(SkeletonResourcePaths.TryClassify(gamePath, out var path));
        Assert.Equal(default, path);
    }

    [Fact]
    public void GearFallbackFollowsTheRacialModelFallback()
    {
        Assert.Equal(101, SkeletonResourcePaths.GearFallback(101));
        Assert.Equal(101, SkeletonResourcePaths.GearFallback(201));
        Assert.Equal(201, SkeletonResourcePaths.GearFallback(401));
        Assert.Equal(201, SkeletonResourcePaths.GearFallback(1801));
        Assert.Equal(101, SkeletonResourcePaths.GearFallback(1701));
        Assert.Equal(1101, SkeletonResourcePaths.GearFallback(1201));
        Assert.Equal(901, SkeletonResourcePaths.GearFallback(1501));
        Assert.Equal(201, SkeletonResourcePaths.GearFallback(1601));
        Assert.Equal(18, SkeletonResourcePaths.PlayerRaceCodes.Count);
    }

    // ---------- structural checks ----------

    [Theory]
    [InlineData("1100")]
    [InlineData("1110")]
    [InlineData("1200")]
    [InlineData("1300")]
    [InlineData("1301")]
    public void ValidSkeletonHeadersPass(string version) =>
        Assert.Null(SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, Sklb(version, version.StartsWith("13") ? 16u : 12u)));

    [Fact]
    public void MalformedSkeletonsAreRefused()
    {
        Assert.Equal(SkeletonStructureProblem.TooShort, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, Sklb()[..15]));
        Assert.Equal(SkeletonStructureProblem.TooShort, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, []));
        var badMagic = Sklb();
        badMagic[0] = (byte)'x';
        Assert.Equal(SkeletonStructureProblem.BadMagic, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, badMagic));
        Assert.Equal(SkeletonStructureProblem.UnknownVersion, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, Sklb("1400")));
        // The version is a u32, not forward ASCII: the digits stored in reading order are refused.
        var forwardAscii = Sklb();
        Encoding.ASCII.GetBytes("1301").CopyTo(forwardAscii, 4);
        Assert.Equal(SkeletonStructureProblem.UnknownVersion, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, forwardAscii));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, Sklb(skeletonOffset: 8)));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, Sklb(skeletonOffset: 48)));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, Sklb(skeletonOffset: uint.MaxValue)));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton, Sklb("1200", skeletonOffset: 11)));
        Assert.Equal(SkeletonStructureProblem.TooLarge, SkeletonResourceValidation.Check(SkeletonResourceRole.Skeleton,
            Sklb(payload: (int)SkeletonResourceValidation.MaximumSkeletonBytes)));
    }

    [Fact]
    public void PhysicsHeadersAreBounded()
    {
        Assert.Null(SkeletonResourceValidation.Check(SkeletonResourceRole.Physics, Phyb()));
        Assert.Null(SkeletonResourceValidation.Check(SkeletonResourceRole.Physics, Phyb(version: 0, collision: 12, simulator: 12, length: 12)));
        Assert.Null(SkeletonResourceValidation.Check(SkeletonResourceRole.Physics, Phyb(collision: 16, simulator: 32, length: 32)));
        Assert.Equal(SkeletonStructureProblem.TooShort, SkeletonResourceValidation.Check(SkeletonResourceRole.Physics, Phyb()[..15]));
        Assert.Equal(SkeletonStructureProblem.TooShort, SkeletonResourceValidation.Check(SkeletonResourceRole.Physics, new byte[11]));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Physics, Phyb(collision: 8)));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Physics, Phyb(collision: 24, simulator: 20)));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Physics, Phyb(simulator: 33)));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Physics, Phyb(collision: uint.MaxValue, simulator: uint.MaxValue)));
    }

    [Fact]
    public void DeformerTablesAndTrailerAreBounded()
    {
        Assert.Null(SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd()));
        Assert.Null(SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(trailer: true)));
        Assert.Equal(SkeletonStructureProblem.TooShort, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, new byte[3]));
        Assert.Equal(SkeletonStructureProblem.BadCount, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(count: 0)));
        Assert.Equal(SkeletonStructureProblem.BadCount, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(count: -1)));
        Assert.Equal(SkeletonStructureProblem.BadCount, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(count: 65)));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(count: 3)));
        Assert.Equal(SkeletonStructureProblem.BadRaceCode, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd([Hyur, 999])));
        Assert.Equal(SkeletonStructureProblem.BadRaceCode, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd([Hyur, Hyur])));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(bodyOffset: 8)));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(bodyOffset: -4)));
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(bodyOffset: 45)));
        // The body must end before the trailer, not merely inside the file.
        Assert.Equal(SkeletonStructureProblem.BadOffsets, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(bodyOffset: 48, trailer: true)));
        Assert.Equal(SkeletonStructureProblem.BadTrailer, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(trailer: true, trailerTotal: 10)));
        Assert.Equal(SkeletonStructureProblem.BadTrailer, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer, Pbd(trailer: true, trailerTotal: ulong.MaxValue)));
        Assert.Equal(SkeletonStructureProblem.TooLarge, SkeletonResourceValidation.Check(SkeletonResourceRole.Deformer,
            new byte[SkeletonResourceValidation.MaximumDeformerBytes + 1]));
    }

    [Fact]
    public void ChecksNeverThrowOnArbitraryBytes()
    {
        var random = new Random(20261101);
        foreach (var role in Enum.GetValues<SkeletonResourceRole>())
        {
            var seed = role switch
            {
                SkeletonResourceRole.Skeleton => Sklb(),
                SkeletonResourceRole.Physics => Phyb(),
                _ => Pbd(trailer: true),
            };
            for (var i = 0; i < 5000; i++)
            {
                var data = i % 4 == 0 ? seed[..random.Next(seed.Length + 1)] : (byte[])seed.Clone();
                for (var flips = random.Next(1, 5); flips > 0 && data.Length > 0; flips--)
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                _ = SkeletonResourceValidation.Check(role, data); // must return, never throw
            }
        }
    }

    // ---------- group assembly ----------

    [Fact]
    public void ASkeletonWithTheDeformerAndNoPhysicsIsAWholeGroup()
    {
        var sklb = Sklb();
        var pbd = Pbd();
        var result = Assemble(Viera, [BaseSklb, Deformer], C(Deformer, pbd), C(BaseSklb, sklb));

        Assert.True(result.IsAccepted);
        Assert.Null(result.Refusal);
        var group = result.Group!;
        Assert.Equal(Viera, group.RaceCode);
        Assert.Equal(new[] { BaseSklb, Deformer }, group.Members.Select(m => m.Path.GamePath));
        Assert.Equal(new AppearanceBlobRef(ContentHash.Of(sklb), sklb.Length), group.Members[0].Content);
        Assert.Equal(new AppearanceBlobRef(ContentHash.Of(pbd), pbd.Length), group.Members[1].Content);
        Assert.DoesNotContain(group.Members, m => m.Path.Role == SkeletonResourceRole.Physics);
    }

    [Fact]
    public void OtherDependencyShapedGroupsAreWhole()
    {
        Assert.True(Assemble(Viera, [BaseSklb], C(BaseSklb, Sklb())).IsAccepted);
        Assert.True(Assemble(Viera, [Deformer], C(Deformer, Pbd())).IsAccepted);
        Assert.True(Assemble(Viera, [BasePhyb], C(BasePhyb, Phyb())).IsAccepted);
        Assert.True(Assemble(Viera, [BaseSklb, BasePhyb, HairSklb, HeadSklbFallback, Deformer],
            C(BaseSklb, Sklb()), C(BasePhyb, Phyb()), C(HairSklb, Sklb()), C(HeadSklbFallback, Sklb()), C(Deformer, Pbd(trailer: true))).IsAccepted);
    }

    [Fact]
    public void AnIncompleteGroupIsRefusedWhole()
    {
        var missingDeformer = Assemble(Viera, [BaseSklb, Deformer], C(BaseSklb, Sklb()));
        Assert.Equal(SkeletonGroupRefusal.MissingRequiredMember, missingDeformer.Refusal);
        Assert.Equal(Deformer, missingDeformer.GamePath);
        Assert.Null(missingDeformer.Group);

        var missingPhysics = Assemble(Viera, [BaseSklb, BasePhyb, Deformer], C(BaseSklb, Sklb()), C(Deformer, Pbd()));
        Assert.Equal(SkeletonGroupRefusal.MissingRequiredMember, missingPhysics.Refusal);
        Assert.Equal(BasePhyb, missingPhysics.GamePath);

        var missingSkeleton = Assemble(Viera, [BaseSklb, Deformer], C(Deformer, Pbd()));
        Assert.Equal(SkeletonGroupRefusal.MissingRequiredMember, missingSkeleton.Refusal);
        Assert.Equal(BaseSklb, missingSkeleton.GamePath);

        Assert.Equal(SkeletonGroupRefusal.UnexpectedMember, Assemble(Viera, [BaseSklb], C(BaseSklb, Sklb()), C(Deformer, Pbd())).Refusal);
    }

    [Fact]
    public void OneBadMemberRefusesTheWholeGroup()
    {
        var corrupt = Sklb();
        corrupt[0] = 0;
        var result = Assemble(Viera, [BaseSklb, Deformer], C(BaseSklb, corrupt), C(Deformer, Pbd()));
        Assert.Equal(SkeletonGroupRefusal.Structure, result.Refusal);
        Assert.Equal(SkeletonStructureProblem.BadMagic, result.Problem);
        Assert.Equal(BaseSklb, result.GamePath);
        Assert.Null(result.Group);

        var oversize = Assemble(Viera, [Deformer], C(Deformer, new byte[SkeletonResourceValidation.MaximumDeformerBytes + 1]));
        Assert.Equal(SkeletonGroupRefusal.Structure, oversize.Refusal);
        Assert.Equal(SkeletonStructureProblem.TooLarge, oversize.Problem);
    }

    [Fact]
    public void PathsAreHeldToTheCanonicalRules()
    {
        var upper = Assemble(Viera, ["chara/xls/boneDeformer/human.pbd"], C("chara/xls/boneDeformer/human.pbd", Pbd()));
        Assert.Equal(SkeletonGroupRefusal.InvalidGamePath, upper.Refusal);

        var model = "chara/human/c1801/obj/body/b0001/model/c1801b0001_top.mdl";
        Assert.Equal(SkeletonGroupRefusal.NotASkeletonResource, Assemble(Viera, [model], C(model, Sklb())).Refusal);
        Assert.Equal(SkeletonGroupRefusal.InvalidGamePath, Assemble(Viera, [BaseSklb], C("../" + BaseSklb, Sklb())).Refusal);
        var skp = "chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.skp";
        Assert.Equal(SkeletonGroupRefusal.NotASkeletonResource, Assemble(Viera, [skp], C(skp, Sklb())).Refusal);
    }

    [Fact]
    public void DuplicatesAndSecondGroupsAreRefused()
    {
        Assert.Equal(SkeletonGroupRefusal.DuplicateGamePath,
            Assemble(Viera, [BaseSklb], C(BaseSklb, Sklb()), C(BaseSklb, Sklb())).Refusal);
        Assert.Equal(SkeletonGroupRefusal.DuplicateGamePath,
            SkeletonResourceGroupAssembler.Assemble(Viera, new List<string> { BaseSklb, BaseSklb }, [C(BaseSklb, Sklb())]).Refusal);

        var hairA = "chara/human/c1801/skeleton/hair/h0003/skl_c1801h0003.sklb";
        var hairB = "chara/human/c1801/skeleton/hair/h0007/skl_c1801h0007.sklb";
        var twoHair = Assemble(Viera, [hairA, hairB], C(hairA, Sklb()), C(hairB, Sklb()));
        Assert.Equal(SkeletonGroupRefusal.MoreThanOneGroup, twoHair.Refusal);
        Assert.Equal(hairB, twoHair.GamePath);
    }

    [Fact]
    public void MembersMustFitTheTargetRace()
    {
        var hyurBase = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
        Assert.Equal(SkeletonGroupRefusal.RaceMismatch, Assemble(Viera, [hyurBase], C(hyurBase, Sklb())).Refusal);
        var otherHair = "chara/human/c0201/skeleton/hair/h0003/skl_c0201h0003.sklb";
        Assert.Equal(SkeletonGroupRefusal.RaceMismatch, Assemble(Viera, [otherHair], C(otherHair, Sklb())).Refusal);

        // Gear may resolve for the character's own race, its fallback, or c0101; not for an unrelated race.
        var ownHead = "chara/human/c1801/skeleton/met/m6020/skl_c1801m6020.sklb";
        var hyurBody = "chara/human/c0101/skeleton/top/t0100/skl_c0101t0100.sklb";
        Assert.True(Assemble(Viera, [ownHead], C(ownHead, Sklb())).IsAccepted);
        Assert.True(Assemble(Viera, [HeadSklbFallback], C(HeadSklbFallback, Sklb())).IsAccepted);
        Assert.True(Assemble(Viera, [hyurBody], C(hyurBody, Sklb())).IsAccepted);
        var lalaHead = "chara/human/c1101/skeleton/met/m6020/skl_c1101m6020.sklb";
        Assert.Equal(SkeletonGroupRefusal.RaceMismatch, Assemble(Viera, [lalaHead], C(lalaHead, Sklb())).Refusal);
        var npcHead = "chara/human/c1804/skeleton/met/m6020/skl_c1804m6020.sklb";
        Assert.Equal(SkeletonGroupRefusal.RaceMismatch, Assemble(Viera, [npcHead], C(npcHead, Sklb())).Refusal);

        Assert.Equal(SkeletonGroupRefusal.InvalidTargetRace, Assemble(1804, [Deformer], C(Deformer, Pbd())).Refusal);
        Assert.Equal(SkeletonGroupRefusal.InvalidTargetRace, Assemble(0, [Deformer], C(Deformer, Pbd())).Refusal);
    }

    [Fact]
    public void ASlotsSkeletonAndPhysicsMustAgree()
    {
        var head = "chara/human/c1801/skeleton/met/m6020/skl_c1801m6020.sklb";
        var headPhysicsOtherRace = "chara/human/c0201/skeleton/met/m6020/phy_c0201m6020.phyb";
        var headPhysicsOtherSet = "chara/human/c1801/skeleton/met/m6021/phy_c1801m6021.phyb";
        Assert.Equal(SkeletonGroupRefusal.InconsistentSlot,
            Assemble(Viera, [head, headPhysicsOtherRace], C(head, Sklb()), C(headPhysicsOtherRace, Phyb())).Refusal);
        Assert.Equal(SkeletonGroupRefusal.InconsistentSlot,
            Assemble(Viera, [head, headPhysicsOtherSet], C(head, Sklb()), C(headPhysicsOtherSet, Phyb())).Refusal);
    }

    [Fact]
    public void EmptyAndOversizedRequestsAreRefused()
    {
        Assert.Equal(SkeletonGroupRefusal.Empty, Assemble(Viera, []).Refusal);
        Assert.Equal(SkeletonGroupRefusal.Empty, Assemble(Viera, [], C(BaseSklb, Sklb())).Refusal);
        var many = Enumerable.Range(1, SkeletonResourceGroupAssembler.MaximumMembers + 1)
            .Select(i => $"chara/human/c1801/skeleton/hair/h{i:D4}/skl_c1801h{i:D4}.sklb").ToArray();
        Assert.Equal(SkeletonGroupRefusal.TooManyMembers, Assemble(Viera, many).Refusal);
        Assert.Equal(SkeletonGroupRefusal.TooManyMembers,
            Assemble(Viera, [BaseSklb], many.Select(p => C(p, Sklb())).ToArray()).Refusal);
    }

    [Fact]
    public void TheResultDoesNotDependOnOfferOrder()
    {
        SkeletonGroupCandidate[] offered = [C(Deformer, Pbd()), C(HairSklb, Sklb()), C(BaseSklb, Sklb())];
        var a = Assemble(Viera, [HairSklb, Deformer, BaseSklb], offered);
        var b = Assemble(Viera, [BaseSklb, HairSklb, Deformer], Enumerable.Reverse(offered).ToArray());
        Assert.Equal(a.Group!.Members, b.Group!.Members);
    }

    [Fact]
    public void SkeletonResourcesAreAllowedOnlyByAllowlistVersionTwo()
    {
        // AM-4: allowlist and manifest format 2; the version-1 list and the
        // one-argument check (every existing caller) still refuse skeleton resources.
        // HV-6: version 3 (format 3) is exactly the version 2 set.
        Assert.Equal(3, AppearanceFileTypes.Version);
        Assert.Equal(3, AppearanceLimits.FormatVersion);
        Assert.Equal(new[] { ".mdl", ".mtrl", ".tex" }, AppearanceFileTypes.Allowed);
        Assert.False(AppearanceFileTypes.IsAllowed(BaseSklb));
        Assert.False(AppearanceFileTypes.IsAllowed(Deformer));
        Assert.True(AppearanceFileTypes.IsAllowed(BaseSklb, 2));
        Assert.True(AppearanceFileTypes.IsAllowed(Deformer, 2));
    }
}
