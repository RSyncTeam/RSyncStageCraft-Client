#if RSC_DEVELOPMENT
using System.Text;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-5 M3: the owner-approved Customize+ profile of a catalogue entry: the
/// canonical form and its refusals, the bone classification against the
/// captured skeleton, and the capture's read with its gate and answer codes.
/// Synthetic JSON, synthetic bone names and a fake Customize+ only: no game,
/// no IPC, and no real character, profile or template.
/// </summary>
public sealed class HumanCatalogueProfileTests
{
    /// <summary>The canonical text of one bone with every field at its default.</summary>
    private const string Defaults =
        "{\"Translation\":{\"X\":0,\"Y\":0,\"Z\":0},\"Rotation\":{\"X\":0,\"Y\":0,\"Z\":0},\"Scaling\":{\"X\":0,\"Y\":0,\"Z\":0}," +
        "\"ChildScaling\":{\"X\":0,\"Y\":0,\"Z\":0},\"PropagateTranslation\":false,\"PropagateRotation\":false,\"PropagateScale\":false," +
        "\"ChildScaleIndependent\":false}";

    private static string Canonical(string json)
    {
        var canonical = HumanCatalogueProfile.Canonicalize(json, out _, out var refusal);
        Assert.True(canonical is not null, refusal);
        return canonical!;
    }

    // ---------- the canonical form ----------

    [Fact]
    public void HvFoursFixedPayloadIsAlreadyCanonical()
    {
        Assert.Equal(HumanActorCustomizePlusStage.PayloadJson, Canonical(HumanActorCustomizePlusStage.PayloadJson));
    }

    [Fact]
    public void APartialVectorIsFilledWithZerosAndEveryFieldIsWritten()
    {
        Assert.Equal(
            "{\"Bones\":{\"j_kosi\":{\"Translation\":{\"X\":0,\"Y\":0,\"Z\":0},\"Rotation\":{\"X\":0,\"Y\":0,\"Z\":0}," +
            "\"Scaling\":{\"X\":0,\"Y\":0.02,\"Z\":0},\"ChildScaling\":{\"X\":0,\"Y\":0,\"Z\":0},\"PropagateTranslation\":false," +
            "\"PropagateRotation\":false,\"PropagateScale\":false,\"ChildScaleIndependent\":false}}}",
            Canonical("{\"Bones\":{\"j_kosi\":{\"Scaling\":{\"Y\":0.02}}}}"));
    }

    [Fact]
    public void AnOmittedScalingIsZeroNotOneAndAnEmptyBoneIsAllDefaults()
    {
        // IPCBoneTransform's default: Customize+ omits a zero vector, so a missing Scaling reads as zero.
        Assert.Equal("{\"Bones\":{\"j_kosi\":" + Defaults + "}}", Canonical("{\"Bones\":{\"j_kosi\":{}}}"));
        Assert.Equal("{\"Bones\":{\"j_kosi\":" + Defaults + "}}", Canonical("{\"Bones\":{\"j_kosi\":{\"Scaling\":{}}}}"));
    }

    [Fact]
    public void TheFlagsAreWrittenAsReadAndDefaultToFalse()
    {
        var text = Canonical("{\"Bones\":{\"j_kosi\":{\"PropagateScale\":true,\"ChildScaleIndependent\":false}}}");
        Assert.Contains("\"PropagateTranslation\":false,\"PropagateRotation\":false,\"PropagateScale\":true,\"ChildScaleIndependent\":false}", text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.15")]
    [InlineData("1.0")]
    [InlineData("1E-3")]
    [InlineData("2.5e+2")]
    [InlineData("-0")]
    [InlineData("-0.0")]
    [InlineData("0.30000001192092896")]
    [InlineData("3.4028234E+38")]
    [InlineData("3.4028235E+38")]
    [InlineData("123456789012345678")]
    public void EveryNumberKeepsItsTokenExactlyAsWritten(string token)
    {
        var text = Canonical("{\"Bones\":{\"j_kosi\":{\"Rotation\":{\"Z\":" + token + "}}}}");
        Assert.Contains("\"Rotation\":{\"X\":0,\"Y\":0,\"Z\":" + token + "}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBonesAreSortedOrdinallyAndListed()
    {
        var text = HumanCatalogueProfile.Canonicalize("{\"Bones\":{\"b\":{},\"A\":{},\"a\":{},\"_\":{}}}", out var bones, out var refusal);
        Assert.Null(refusal);
        Assert.Equal(new[] { "A", "_", "a", "b" }, bones);
        Assert.Equal("{\"Bones\":{\"A\":" + Defaults + ",\"_\":" + Defaults + ",\"a\":" + Defaults + ",\"b\":" + Defaults + "}}", text);
    }

    [Fact]
    public void CanonicalizingTwiceChangesNothing()
    {
        var once = Canonical(ProfileSample.Json);
        Assert.Equal(once, Canonical(once));
        // Whitespace and property order in the source do not matter.
        var spaced = "{ \"Bones\" : { \"j_kosi\" : { \"Scaling\" : { \"Z\" : 1.05 , \"X\" : 1.05, \"Y\": 1.05 } } ," +
            " \"iv_ko_c_l\": {\"PropagateScale\": true, \"Scaling\": {\"Y\":1.1,\"X\":1.1,\"Z\":1.1}, \"Translation\": {\"Y\": 0.02}}," +
            " \"j_absent_x\": {\"Rotation\": {\"Z\": 5.5}} } }";
        Assert.Equal(once, Canonical(spaced));
    }

    [Theory]
    [InlineData("", "the profile text is empty")]
    [InlineData("not json", "the profile is not valid JSON")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{}},}", "the profile is not valid JSON")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{}}} // x", "the profile is not valid JSON")]
    [InlineData("[]", "the profile is not a JSON object")]
    [InlineData("{}", "the profile has no Bones object")]
    [InlineData("{\"Bones\":null}", "the profile has no Bones object")]
    [InlineData("{\"Bones\":[]}", "the profile has no Bones object")]
    [InlineData("{\"bones\":{\"secret_bone_key\":{}}}", "the profile has a property other than one Bones")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{}},\"Name\":\"x\"}", "the profile has a property other than one Bones")]
    [InlineData("{\"Bones\":{},\"Bones\":{\"secret_bone_key\":{}}}", "the profile has a property other than one Bones")]
    [InlineData("{\"Bones\":{}}", "the profile has no bone (every template may be disabled)")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{},\"secret_bone_key\":{}}}", "a bone name appears twice")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":null}}", "a bone is not a JSON object")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scale\":{}}}}", "a bone has an unknown or repeated property")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"scaling\":{}}}}", "a bone has an unknown or repeated property")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{},\"Scaling\":{}}}}", "a bone has an unknown or repeated property")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":null}}}", "a vector is not a JSON object")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":[1,1,1]}}}", "a vector is not a JSON object")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"W\":1}}}}", "a vector has an unknown or repeated component")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"x\":1}}}}", "a vector has an unknown or repeated component")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":1,\"X\":2}}}}", "a vector has an unknown or repeated component")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":\"1\"}}}}", "a number is not finite, within float range, and at most 32 characters")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":null}}}}", "a number is not finite, within float range, and at most 32 characters")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":1e39}}}}", "a number is not finite, within float range, and at most 32 characters")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":-1e39}}}}", "a number is not finite, within float range, and at most 32 characters")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":1.0000000000000000000000000000000}}}}", "a number is not finite, within float range, and at most 32 characters")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":3.5E+38}}}}", "a number is not finite, within float range, and at most 32 characters")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":1234567890123456789}}}}", "a number is not finite, within float range, and at most 32 characters")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":-1234567890123456789}}}}", "a number is not finite, within float range, and at most 32 characters")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"PropagateScale\":1}}}", "a flag is not true or false")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"PropagateScale\":\"true\"}}}", "a flag is not true or false")]
    [InlineData("{\"Bones\":{\"secret_bone_key\":{\"PropagateScale\":null}}}", "a flag is not true or false")]
    public void AProfileOutsideTheShapeIsRefusedWithAFixedTextThatNamesNoBone(string json, string why)
    {
        Assert.Null(HumanCatalogueProfile.Canonicalize(json, out var bones, out var refusal));
        Assert.Empty(bones);
        Assert.Equal(why, refusal);
        Assert.DoesNotContain("secret", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret\\u0022key")]
    [InlineData("secret\\\\key")]
    [InlineData("secret\\u0001key")]
    [InlineData("secret\\tkey")]
    [InlineData("secret\\u007fkey")]
    [InlineData("secretékey")]
    [InlineData("secret\\u00e9key")]
    [InlineData("secret_bone_key_0123456789012345678901234567890123456789012345678")]
    public void ABoneKeyOutsidePrintableAsciiIsRefused(string escapedKey)
    {
        Assert.Null(HumanCatalogueProfile.Canonicalize("{\"Bones\":{\"" + escapedKey + "\":{}}}", out _, out var refusal));
        Assert.Equal("a bone name is not 1 to 64 printable ASCII characters other than quote and backslash", refusal);
    }

    [Theory]
    [InlineData("j_kosi")]
    [InlineData("iv_ko_c_l")]
    [InlineData("Mixed Case-Bone.01")]
    [InlineData("~!#$%&'()*+,-./:;<=>?@[]^_`{|}")]
    [InlineData(" ")]
    public void APrintableAsciiBoneKeyIsAccepted(string key)
    {
        Assert.True(HumanCatalogueProfile.IsBoneKey(key));
        Assert.Equal("{\"Bones\":{\"" + key + "\":" + Defaults + "}}", Canonical("{\"Bones\":{\"" + key + "\":{}}}"));
    }

    [Fact]
    public void AnEscapedBoneKeyIsCheckedAndWrittenUnescaped()
    {
        Assert.Equal("{\"Bones\":{\"n_root\":" + Defaults + "}}", Canonical("{\"Bones\":{\"n\\u005froot\":{}}}"));
        Assert.True(HumanCatalogueProfile.IsBoneKey(new string('k', 64)));
        Assert.False(HumanCatalogueProfile.IsBoneKey(new string('k', 65)));
    }

    [Fact]
    public void TheBoneCountIsBoundedAtOneThousandAndTwentyFour()
    {
        static string Bones(int count) =>
            "{\"Bones\":{" + string.Join(",", Enumerable.Range(0, count).Select(index => $"\"b{index:D4}\":{{}}")) + "}}";

        Assert.NotNull(HumanCatalogueProfile.Canonicalize(Bones(HumanCatalogueProfile.MaximumBones), out var bones, out _));
        Assert.Equal(1024, bones.Count);
        Assert.Null(HumanCatalogueProfile.Canonicalize(Bones(HumanCatalogueProfile.MaximumBones + 1), out _, out var refusal));
        Assert.Equal("the profile has more than 1024 bones", refusal);
    }

    [Fact]
    public void TheTextReadAndTheCanonicalTextAreEachBoundedAtTwoHundredFiftySixKibibytes()
    {
        var padded = "{\"Bones\":{\"j_kosi\":{}}" + new string(' ', HumanCatalogueProfile.MaximumTextBytes) + "}";
        Assert.Null(HumanCatalogueProfile.Canonicalize(padded, out _, out var tooLarge));
        Assert.Equal("the profile text is larger than 262144 bytes", tooLarge);

        // About 70 bytes a bone as read, about 300 written out: within the bound read, beyond it canonical.
        var wide = "{\"Bones\":{" + string.Join(",", Enumerable.Range(0, 1000).Select(index => $"\"{index:D4}{new string('k', 60)}\":{{}}")) + "}}";
        Assert.True(Encoding.UTF8.GetByteCount(wide) < HumanCatalogueProfile.MaximumTextBytes);
        Assert.Null(HumanCatalogueProfile.Canonicalize(wide, out _, out var canonicalTooLarge));
        Assert.Equal("the canonical profile text is larger than 262144 bytes", canonicalTooLarge);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Bones\":{\"a\":{\"Scaling\":{\"X\":1}}")]
    [InlineData("\"Bones\"")]
    [InlineData("null")]
    [InlineData("{\"Bones\":{\"a\":{\"Scaling\":{\"X\":NaN}}}}")]
    [InlineData("{\"Bones\":{\"a\":{\"Scaling\":{\"X\":Infinity}}}}")]
    [InlineData("{\"Bones\":{\"a\":{\"Scaling\":{\"X\":{\"Y\":{\"Z\":{\"W\":{\"V\":{\"U\":1}}}}}}}}}")]
    public void MalformedTextIsRefusedAndNeverThrows(string json)
    {
        Assert.Null(HumanCatalogueProfile.Canonicalize(json, out _, out var refusal));
        Assert.NotNull(refusal);
    }

    // ---------- the classification (D-M3-3, D-M3-8, D-M3-10) ----------

    [Fact]
    public void BonesAreCountedAsBaseOtherPartialOrAbsentAndNoneIsDropped()
    {
        IReadOnlyList<IReadOnlyList<string>?> skeleton = [["n_root", "j_kosi", "iv_ko_c_l"], null, ["j_kami_a", "j_kosi"]];
        var counts = HumanCatalogueProfile.Classify(["iv_ko_c_l", "j_absent_x", "j_kami_a", "j_kosi", "J_KOSI"], skeleton);
        // j_kosi counts once, as base; the case differs for J_KOSI, which is absent (names compare ordinally).
        Assert.Equal(new HumanCatalogueProfileCounts(5, 2, 1, 2), counts);
        Assert.Equal("5 bone(s) (2 base, 1 other partial, 2 absent)", counts.Describe());
    }

    [Fact]
    public void TheBoneNamesMustComeFromTheSkeletonTheSummaryDescribes()
    {
        Assert.True(HumanCatalogueProfile.MatchesSummary(ProfileSample.Skeleton, ProfileSample.Summary));
        Assert.True(HumanCatalogueProfile.MatchesSummary([["a"], ["b"], ["c"], ["d"], null], HumanCatalogueFixture.FiveSlotSummary));
        Assert.False(HumanCatalogueProfile.MatchesSummary([["a"], ["b"], ["c"], ["d"], ["e"]], HumanCatalogueFixture.FiveSlotSummary));
        Assert.False(HumanCatalogueProfile.MatchesSummary([["a"], ["b"], ["c"], ["d"]], HumanCatalogueFixture.FiveSlotSummary));
        Assert.False(HumanCatalogueProfile.MatchesSummary([null], ["skeleton partials: 1", "skeleton 0: no resource"]));
        Assert.False(HumanCatalogueProfile.MatchesSummary([["a"]], ["skeleton partials: 1", "skeleton 0: file 0000000000000000, bones 1"]));
        Assert.False(HumanCatalogueProfile.MatchesSummary([], ["skeleton: none"]));
    }

    // ---------- the capture's read ----------

    [Fact]
    public void TheReadGatesThenReadsTheActiveProfileOnceAndStoresOnlyItsCanonicalText()
    {
        var reads = new FakeProfileReads();

        var (read, refusal) = HumanCatalogueProfileReader.Read(reads, 0, ProfileSample.Skeleton, ProfileSample.Summary);

        Assert.Null(refusal);
        Assert.Equal(Canonical(ProfileSample.Json), read!.Profile);
        Assert.Equal(HumanCatalogueEntry.HashOf(read.Profile), read.ProfileHash);
        Assert.Equal(ProfileSample.Counts, read.Counts);
        Assert.Equal(FakeProfileReads.Tag, read.IdTag);
        Assert.Equal(new[] { "version", "api", "valid", "owner", "active 0", "owner" }, reads.Calls);
        Assert.DoesNotContain(FakeProfileReads.Tag, read.Profile, StringComparison.Ordinal);
        Assert.DoesNotContain("j_kosi", read.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not loaded", "Customize+ profile: Customize+ is not loaded")]
    [InlineData("version", "Customize+ profile: Customize+ 2.2.1.4 is not the checked 2.2.1.3")]
    [InlineData("api", "Customize+ profile: the IPC API is not 6.4")]
    [InlineData("no api", "Customize+ profile: the IPC API is not 6.4")]
    [InlineData("invalid", "Customize+ profile: Customize+ reports it is not valid")]
    [InlineData("list throws", "Customize+ profile: a read failed (InvalidOperationException)")]
    public void TheGateRefusesBeforeAnyProfileRead(string change, string why)
    {
        var reads = new FakeProfileReads();
        switch (change)
        {
            case "not loaded": reads.Version = null; break;
            case "version": reads.Version = "2.2.1.4"; break;
            case "api": reads.Api = (6, 3); break;
            case "no api": reads.Api = null; break;
            case "invalid": reads.Valid = false; break;
            case "list throws": reads.ListThrows = true; break;
        }

        var (read, refusal) = HumanCatalogueProfileReader.Read(reads, 0, ProfileSample.Skeleton, ProfileSample.Summary);

        Assert.Null(read);
        Assert.Equal(why, refusal);
        Assert.DoesNotContain(reads.Calls, call => call.StartsWith("active", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3, null, null, null, "Customize+ profile: no normal profile is active on the character (temporary profiles, the editor and the global default profile cannot be read)")]
    [InlineData(0, null, null, null, "Customize+ profile: no normal profile is active on the character (temporary profiles, the editor and the global default profile cannot be read)")]
    [InlineData(1, null, null, null, "Customize+ profile: Customize+ does not accept the character (InvalidCharacter)")]
    [InlineData(255, null, null, null, "Customize+ profile: the active-profile read answered 255")]
    [InlineData(0, FakeProfileReads.Tag, 3, null, "Customize+ profile: the active profile could not be read (ProfileNotFound)")]
    [InlineData(0, FakeProfileReads.Tag, 255, null, "Customize+ profile: the content read answered 255")]
    [InlineData(0, FakeProfileReads.Tag, 1, null, "Customize+ profile: the content read answered 1")]
    [InlineData(0, FakeProfileReads.Tag, 0, null, "Customize+ profile: the active profile's content is missing")]
    [InlineData(0, FakeProfileReads.Tag, null, null, "Customize+ profile: the active profile's content was not read")]
    [InlineData(0, FakeProfileReads.Tag, 0, "{\"Bones\":{}}", "Customize+ profile: the profile has no bone (every template may be disabled)")]
    [InlineData(0, FakeProfileReads.Tag, 0, "{\"Bones\":{\"secret_bone_key\":{\"Scaling\":{\"X\":\"big\"}}}}",
        "Customize+ profile: a number is not finite, within float range, and at most 32 characters")]
    public void EveryAnswerOtherThanOneActiveReadableProfileIsRefused(int active, string? tag, int? content, string? json, string why)
    {
        var reads = new FakeProfileReads { Answer = new HumanCatalogueProfileAnswer(active, tag, content, json) };

        var (read, refusal) = HumanCatalogueProfileReader.Read(reads, 0, ProfileSample.Skeleton, ProfileSample.Summary);

        Assert.Null(read);
        Assert.Equal(why, refusal);
    }

    [Fact]
    public void AnActiveReadThatThrowsIsRefusedWithItsTypeOnly()
    {
        var reads = new FakeProfileReads { ActiveThrows = true };
        Assert.Equal("Customize+ profile: a read failed (InvalidOperationException)",
            HumanCatalogueProfileReader.Read(reads, 0, ProfileSample.Skeleton, ProfileSample.Summary).Refusal);
    }

    [Fact]
    public void AChangeOfTheOwnersProfilesDuringTheReadIsRefused()
    {
        var reads = new FakeProfileReads();
        reads.DuringActive = () => reads.Fingerprint = "active 0:other; list 2:def";
        Assert.Equal("Customize+ profile: the owner's profiles changed during the read",
            HumanCatalogueProfileReader.Read(reads, 0, ProfileSample.Skeleton, ProfileSample.Summary).Refusal);
    }

    [Fact]
    public void AProfileWithNoBoneOnTheCapturedSkeletonIsRefused()
    {
        var reads = new FakeProfileReads { Answer = FakeProfileReads.Readable("{\"Bones\":{\"secret_absent_a\":{},\"secret_absent_b\":{}}}") };
        var refusal = HumanCatalogueProfileReader.Read(reads, 0, ProfileSample.Skeleton, ProfileSample.Summary).Refusal;
        Assert.Equal("Customize+ profile: none of its 2 bone(s) is on the captured skeleton", refusal);
        Assert.DoesNotContain("secret", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void BoneNamesThatCannotBeReadOrDoNotMatchTheSummaryAreRefused()
    {
        const string why = "Customize+ profile: the captured skeleton's bone names could not be read";
        Assert.Equal(why, HumanCatalogueProfileReader.Read(new FakeProfileReads(), 0, null, ProfileSample.Summary).Refusal);
        Assert.Equal(why, HumanCatalogueProfileReader.Read(new FakeProfileReads(), 0, [["n_root"], ["j_kami_a"]], ProfileSample.Summary).Refusal);
    }

    [Fact]
    public void NoProfileReadMemberTakesOrReturnsAnId()
    {
        // D-M3-2: the content read is reached only through the active-profile read; nothing in this surface can name an id.
        foreach (var type in new[] { typeof(IHumanCatalogueProfileReads), typeof(HumanCatalogueProfileAnswer), typeof(HumanCatalogueProfileRead) })
        {
            foreach (var method in type.GetMethods())
            {
                Assert.NotEqual(typeof(Guid), Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType);
                Assert.DoesNotContain(method.GetParameters(), parameter => (Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType) == typeof(Guid));
            }
            Assert.DoesNotContain(type.GetProperties(), property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Guid));
        }
    }

    [Fact]
    public void TheObjectIndexIsPassedThroughAndNoOtherIdIsEverRead()
    {
        var reads = new FakeProfileReads();
        HumanCatalogueProfileReader.Read(reads, 7, ProfileSample.Skeleton, ProfileSample.Summary);
        Assert.Single(reads.Calls, call => call.StartsWith("active", StringComparison.Ordinal));
        Assert.Contains("active 7", reads.Calls);
    }
}

/// <summary>A synthetic profile, skeleton and summary for the M3 tests: three bones, two on the base partial and one absent.</summary>
internal static class ProfileSample
{
    public const string Json =
        "{\"Bones\":{\"j_kosi\":{\"Scaling\":{\"X\":1.05,\"Y\":1.05,\"Z\":1.05}}," +
        "\"iv_ko_c_l\":{\"Translation\":{\"Y\":0.02},\"Scaling\":{\"X\":1.1,\"Y\":1.1,\"Z\":1.1},\"PropagateScale\":true}," +
        "\"j_absent_x\":{\"Rotation\":{\"Z\":5.5}}}}";

    public static readonly IReadOnlyList<IReadOnlyList<string>?> Skeleton = [["n_root", "j_kosi", "iv_ko_c_l"]];

    public static IReadOnlyList<string> Summary => HumanCatalogueFixture.Summary;

    public static readonly HumanCatalogueProfileCounts Counts = new(3, 2, 0, 1);

    /// <summary>The profile as the capture reads it, through the real reader and a fake Customize+.</summary>
    public static HumanCatalogueProfileRead Read(string json = Json)
    {
        var (read, refusal) = HumanCatalogueProfileReader.Read(new FakeProfileReads { Answer = FakeProfileReads.Readable(json) }, 0, Skeleton, Summary);
        return read ?? throw new InvalidOperationException("the synthetic profile was refused: " + refusal);
    }
}

/// <summary>A synthetic Customize+ for the capture's profile read; every call is logged.</summary>
internal sealed class FakeProfileReads : IHumanCatalogueProfileReads
{
    /// <summary>A synthetic id hash, as the adapter would give it (never an id).</summary>
    public const string Tag = "5a17c0de";

    public static HumanCatalogueProfileAnswer Readable(string json) => new(HumanActorCustomizePlusStage.Success, Tag, HumanActorCustomizePlusStage.Success, json);

    public List<string> Calls { get; } = [];
    public string? Version { get; set; } = "2.2.1.3";
    public (int Major, int Minor)? Api { get; set; } = (6, 4);
    public bool Valid { get; set; } = true;
    public bool ListThrows { get; set; }
    public bool ActiveThrows { get; set; }
    public string Fingerprint { get; set; } = "active 0:abc; list 1:abc";
    public Action? DuringActive { get; set; }
    public HumanCatalogueProfileAnswer Answer { get; set; } = Readable(ProfileSample.Json);

    public string? PluginVersion
    {
        get
        {
            Calls.Add("version");
            return ListThrows ? throw new InvalidOperationException() : Version;
        }
    }

    public (int Major, int Minor)? ApiVersion
    {
        get
        {
            Calls.Add("api");
            return Api;
        }
    }

    public bool IsValid
    {
        get
        {
            Calls.Add("valid");
            return Valid;
        }
    }

    public HumanCatalogueProfileAnswer ActiveProfile(ushort objectIndex)
    {
        Calls.Add($"active {objectIndex}");
        if (ActiveThrows)
            throw new InvalidOperationException();
        DuringActive?.Invoke();
        return Answer;
    }

    public string OwnerProfiles()
    {
        Calls.Add("owner");
        return Fingerprint;
    }
}
#endif
