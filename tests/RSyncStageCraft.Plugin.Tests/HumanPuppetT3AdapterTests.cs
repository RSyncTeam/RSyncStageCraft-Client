#if RSC_DEVELOPMENT
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Penumbra.Api.Enums;
using RSyncStageCraft.Adapters;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Tests;

/// <summary>
/// T3-1's adapter choices that can be checked without the game: R-3's copy
/// flags and the Penumbra collection-type mapping (H1-d: an unmapped role is
/// a failed read). Nothing here calls the game or IPC.
/// </summary>
public sealed class HumanPuppetT3AdapterTests
{
    private static readonly string[] Clans =
    [
        "Midlander", "Highlander", "Wildwood", "Duskwight", "Plainsfolk", "Dunesfolk", "SeekerOfTheSun", "KeeperOfTheMoon",
        "Seawolf", "Hellsguard", "Raen", "Xaela", "Helion", "Lost", "Rava", "Veena",
    ];

    [Fact]
    public void TheCopyFlagsArePositionThenNone()
    {
        // R-3: no other flag (never Name, Target, Mount, Companion or Ornament).
        Assert.Equal(CharacterSetupContainer.CopyFlags.Position, HumanPuppetNativeBackend.FlagsFor(selfCopy: false));
        Assert.Equal(CharacterSetupContainer.CopyFlags.None, HumanPuppetNativeBackend.FlagsFor(selfCopy: true));
    }

    [Fact]
    public void EveryClanAndGenderMapsToPenumbrasOwnType()
    {
        var seen = new HashSet<ApiCollectionType>();
        for (byte clan = 1; clan <= 16; clan++)
        {
            foreach (var (gender, word) in new[] { ((byte)1, "Male"), ((byte)2, "Female") })
            {
                foreach (var npc in new[] { false, true })
                {
                    var type = HumanPenumbraReadIpc.TypeFor(new(HumanCollectionRoleKind.ClanGender, clan, gender, npc));
                    Assert.Equal(Enum.Parse<ApiCollectionType>(word + Clans[clan - 1] + (npc ? "Npc" : "")), type);
                    Assert.True(seen.Add(type!.Value));
                }
            }
        }
        Assert.Equal(64, seen.Count);
        Assert.Equal(ApiCollectionType.MalePlayerCharacter, HumanPenumbraReadIpc.TypeFor(new(HumanCollectionRoleKind.Gender, 0, 1, false)));
        Assert.Equal(ApiCollectionType.FemaleNonPlayerCharacter, HumanPenumbraReadIpc.TypeFor(new(HumanCollectionRoleKind.Gender, 0, 2, true)));
        Assert.Equal(ApiCollectionType.NonPlayerChild, HumanPenumbraReadIpc.TypeFor(new(HumanCollectionRoleKind.NonPlayerChild)));
        Assert.Equal(ApiCollectionType.NonPlayerElderly, HumanPenumbraReadIpc.TypeFor(new(HumanCollectionRoleKind.NonPlayerElderly)));
        Assert.Equal(ApiCollectionType.Default, HumanPenumbraReadIpc.TypeFor(new(HumanCollectionRoleKind.Default)));
    }

    [Theory]
    [InlineData(HumanCollectionRoleKind.ClanGender, 0, 1)]
    [InlineData(HumanCollectionRoleKind.ClanGender, 17, 1)]
    [InlineData(HumanCollectionRoleKind.ClanGender, 1, 0)]
    [InlineData(HumanCollectionRoleKind.ClanGender, 1, 3)]
    [InlineData(HumanCollectionRoleKind.Gender, 0, 0)]
    [InlineData((HumanCollectionRoleKind)99, 1, 1)]
    public void AnUnmappedRoleHasNoTypeSoItsReadFails(HumanCollectionRoleKind kind, int clan, int gender) =>
        Assert.Null(HumanPenumbraReadIpc.TypeFor(new(kind, (byte)clan, (byte)gender, true)));

    [Fact]
    public void TheYourselfTypeIsNeverAsked()
    {
        // P16 step 1: the Yourself path must not apply, so no role maps to it.
        foreach (HumanCollectionRoleKind kind in Enum.GetValues<HumanCollectionRoleKind>())
            for (byte clan = 0; clan <= 17; clan++)
                for (byte gender = 0; gender <= 3; gender++)
                    foreach (var npc in new[] { false, true })
                        Assert.NotEqual(ApiCollectionType.Yourself, HumanPenumbraReadIpc.TypeFor(new(kind, clan, gender, npc)));
    }

    [Fact]
    public void TheRunnerCommandAndStepsAreFixed()
    {
        Assert.Equal("/rsct31", HumanPuppetT3Run.Command);
        Assert.Equal(7, HumanPuppetT3Sequence.Steps.Count);
    }
}
#endif
