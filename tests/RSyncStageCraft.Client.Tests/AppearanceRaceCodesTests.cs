using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The capture contract's target race (CC-1): the explicit customize table
/// and the check against the base skeleton Penumbra resolved.
/// </summary>
public sealed class AppearanceRaceCodesTests
{
    private const string BaseViera = "chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb";
    private const string BaseMidlander = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
    private const string HairViera = "chara/human/c1801/skeleton/hair/h0003/skl_c1801h0003.sklb";
    private static readonly AppearanceCharacterRace VieraRava = new(8, 15, 1);

    // Race byte, clan byte, gender byte (0 male, 1 female), and the code written out.
    [Theory]
    [InlineData(1, 1, 0, 101)]
    [InlineData(1, 1, 1, 201)]
    [InlineData(1, 2, 0, 301)]
    [InlineData(1, 2, 1, 401)]
    [InlineData(2, 3, 0, 501)]
    [InlineData(2, 3, 1, 601)]
    [InlineData(2, 4, 0, 501)]
    [InlineData(2, 4, 1, 601)]
    [InlineData(3, 5, 0, 1101)]
    [InlineData(3, 5, 1, 1201)]
    [InlineData(3, 6, 0, 1101)]
    [InlineData(3, 6, 1, 1201)]
    [InlineData(4, 7, 0, 701)]
    [InlineData(4, 7, 1, 801)]
    [InlineData(4, 8, 0, 701)]
    [InlineData(4, 8, 1, 801)]
    [InlineData(5, 9, 0, 901)]
    [InlineData(5, 9, 1, 1001)]
    [InlineData(5, 10, 0, 901)]
    [InlineData(5, 10, 1, 1001)]
    [InlineData(6, 11, 0, 1301)]
    [InlineData(6, 11, 1, 1401)]
    [InlineData(6, 12, 0, 1301)]
    [InlineData(6, 12, 1, 1401)]
    [InlineData(7, 13, 0, 1501)]
    [InlineData(7, 13, 1, 1601)]
    [InlineData(7, 14, 0, 1501)]
    [InlineData(7, 14, 1, 1601)]
    [InlineData(8, 15, 0, 1701)]
    [InlineData(8, 15, 1, 1801)]
    [InlineData(8, 16, 0, 1701)]
    [InlineData(8, 16, 1, 1801)]
    public void EveryPlayableRaceClanAndGenderMapsToItsRaceCode(int race, int tribe, int sex, int expected)
    {
        Assert.True(AppearanceRaceCodes.TryFromCustomize((byte)race, (byte)tribe, (byte)sex, out var code));
        Assert.Equal((ushort)expected, code);
        Assert.True(SkeletonResourcePaths.IsPlayerRaceCode(code));
    }

    [Fact]
    public void EveryPlayerRaceCodeIsReachableAndNothingElse()
    {
        var reached = new HashSet<ushort>();
        for (var race = 0; race <= byte.MaxValue; race++)
        {
            for (var tribe = 0; tribe <= byte.MaxValue; tribe++)
            {
                for (var sex = 0; sex <= 3; sex++)
                {
                    if (AppearanceRaceCodes.TryFromCustomize((byte)race, (byte)tribe, (byte)sex, out var code))
                        reached.Add(code);
                }
            }
        }

        Assert.Equal(SkeletonResourcePaths.PlayerRaceCodes.Order(), reached.Order());
    }

    [Theory]
    [InlineData(1, 3, 0)]   // Hyur with an Elezen clan
    [InlineData(8, 11, 1)]  // Viera with an Au Ra clan
    [InlineData(3, 7, 0)]   // Lalafell with a Miqo'te clan
    [InlineData(0, 1, 0)]   // no race
    [InlineData(9, 1, 0)]   // unknown race
    [InlineData(1, 0, 0)]   // no clan
    [InlineData(8, 17, 1)]  // unknown clan
    [InlineData(8, 15, 2)]  // not a playable gender
    [InlineData(8, 15, 255)]
    public void AnyOtherCombinationIsRefused(int race, int tribe, int sex)
    {
        Assert.False(AppearanceRaceCodes.TryFromCustomize((byte)race, (byte)tribe, (byte)sex, out var code));
        Assert.Equal(0, code);
    }

    [Fact]
    public void TheRaceIsConfirmedOnlyByExactlyOneMatchingBaseSkeleton()
    {
        Assert.Equal(AppearanceRaceCheck.Confirmed, AppearanceRaceCodes.Confirm(VieraRava, [BaseViera, HairViera, "chara/x/a.tex"], out var code));
        Assert.Equal(1801, code);

        Assert.Equal(AppearanceRaceCheck.Mismatch, AppearanceRaceCodes.Confirm(VieraRava, [BaseMidlander], out code));
        Assert.Equal(0, code);
        // An NPC base skeleton is classifiable, and is never the character's playable race.
        Assert.Equal(AppearanceRaceCheck.Mismatch, AppearanceRaceCodes.Confirm(
            new AppearanceCharacterRace(1, 1, 0), ["chara/human/c0104/skeleton/base/b0001/skl_c0104b0001.sklb"], out _));
        // A hair skeleton alone does not confirm the race.
        Assert.Equal(AppearanceRaceCheck.Unconfirmed, AppearanceRaceCodes.Confirm(VieraRava, [HairViera], out _));
        Assert.Equal(AppearanceRaceCheck.Unconfirmed, AppearanceRaceCodes.Confirm(VieraRava, [], out _));
        Assert.Equal(AppearanceRaceCheck.Unconfirmed, AppearanceRaceCodes.Confirm(VieraRava, [BaseViera, BaseMidlander], out _));
        Assert.Equal(AppearanceRaceCheck.Unknown, AppearanceRaceCodes.Confirm(null, [BaseViera], out _));
        Assert.Equal(AppearanceRaceCheck.Unknown, AppearanceRaceCodes.Confirm(new AppearanceCharacterRace(8, 11, 1), [BaseViera], out _));
    }
}
