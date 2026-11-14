#if RSC_DEVELOPMENT
using System.Text;
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-6: the shared human package (format 3), its facts, the received
/// in-memory entry and the steps. Synthetic data only: no game, no IPC, no
/// owner data.
/// </summary>
public sealed class HumanSharedPackageTests : IDisposable
{
    private static readonly RscUserId PlayerA = new("player-a");
    private readonly List<string> folders = [];

    public void Dispose()
    {
        foreach (var folder in folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private HumanCatalogueEntry Entry(bool swap = false)
    {
        var fixture = HumanCatalogueFixture.Create(swap: swap);
        folders.Add(fixture.Folder);
        return new HumanCatalogueStore(fixture.Root).FindEntry(HumanCatalogueFixture.Label, out var refusal) ?? throw new InvalidOperationException(refusal);
    }

    [Fact]
    public void TheFactsAreReadOnlyInTheirCanonicalForm()
    {
        var facts = new HumanSharedFacts(1, 2, 1, 1, HumanCatalogueFixture.Versions, HumanCatalogueFixture.Summary, new HumanCatalogueProfileCounts(3, 2, 1, 0));
        var bytes = facts.ToCanonicalBytes();
        var back = HumanSharedFacts.TryParse(bytes, out var problem);
        Assert.True(back is not null, problem);
        Assert.Equal(bytes, back!.ToCanonicalBytes());
        Assert.Equal((facts.Race, facts.Tribe, facts.Sex, facts.BodyType, facts.Versions), (back.Race, back.Tribe, back.Sex, back.BodyType, back.Versions));
        Assert.Equal(facts.SkeletonSummary, back.SkeletonSummary);
        Assert.Equal(facts.ProfileCounts, back.ProfileCounts);

        Assert.Null(HumanSharedFacts.TryParse(Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(bytes)), out problem));
        Assert.NotNull(problem);
        Assert.Null(HumanSharedFacts.TryParse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("rsc-human-facts-1", "rsc-human-facts-2")), out _));
        Assert.Null(HumanSharedFacts.TryParse([], out _));
        // No label, name, path or key member exists to carry.
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var member in new[] { "label", "name", "path", "collection", "receipt", "key" })
            Assert.DoesNotContain($"\"{member}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalEntryBecomesAFormatThreeBundleAndTheReceiverRebuildsTheSameEntry()
    {
        var entry = Entry();
        var package = HumanSharedPackageBuilder.Build(entry, PlayerA, withProfile: false, HumanCatalogueFixture.Versions, out var refusal);
        Assert.True(package is not null, refusal);
        var manifest = package!.Manifest;
        Assert.Equal(AppearanceLimits.HumanPackageFormatVersion, manifest.FormatVersion);
        Assert.NotNull(manifest.Human);
        Assert.Equal(PlayerA, manifest.OwnerId);
        Assert.False(package.ProfileIncluded);
        // The canonical bytes read back as the same bundle, and only with human packages allowed.
        Assert.Equal(manifest.BundleId, AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, AppearanceManifestReadOptions.AllowHumanPackages, out _)!.BundleId);
        Assert.Null(AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, AppearanceManifestReadOptions.AllowSkeletonGroups, out _));
        Assert.Null(AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, out _));
        // The same entry builds the same bundle id (place-shared and unshare derive it; it is never typed).
        Assert.Equal(manifest.BundleId, HumanSharedPackageBuilder.Build(entry, PlayerA, false, HumanCatalogueFixture.Versions, out _)!.Manifest.BundleId);

        var received = HumanSharedEntry.Build(manifest, hash => package.MemberBytes.TryGetValue(hash, out var bytes) ? bytes : null, out refusal);
        Assert.True(received is not null, refusal);
        Assert.Equal(HumanCatalogueEntry.SharedFormat, received!.Format);
        Assert.Equal(HumanSharedEntry.LabelOf(manifest.BundleId), received.Label);
        Assert.StartsWith("shared ", received.Label, StringComparison.Ordinal);
        Assert.DoesNotContain(entry.Label, received.Label, StringComparison.Ordinal);
        Assert.Equal((entry.Race, entry.Tribe, entry.Sex, entry.BodyType), (received.Race, received.Tribe, received.Sex, received.BodyType));
        Assert.Equal(entry.GlamourerState, received.GlamourerState);
        Assert.Equal(entry.GlamourerReference, received.GlamourerReference);
        Assert.Equal(entry.Manipulations, received.Manipulations);
        Assert.Equal(entry.SkeletonSummary, received.SkeletonSummary);
        Assert.Equal(entry.Versions, received.Versions);
        Assert.Equal(entry.Paths.Select(path => (path.GamePath, path.Blob)).Order(), received.Paths.Select(path => (path.GamePath, path.Blob)).Order());
        Assert.Equal(entry.Skeleton?.RaceCode, received.Skeleton?.RaceCode);
        Assert.Equal(entry.Skeleton?.Deformer, received.Skeleton?.Deformer);
        Assert.Null(received.Profile);
        Assert.Null(HumanCatalogueEntryCodec.Problem(received));
        // Phase 1 needs the same members on either side.
        Assert.Equal(HumanCatalogueSpawnPreparation.PhaseOneMembers(entry).Order(), HumanCatalogueSpawnPreparation.PhaseOneMembers(received).Order());
    }

    [Fact]
    public void TheReceiverRefusesMissingOrAlteredMembers()
    {
        var entry = Entry();
        var package = HumanSharedPackageBuilder.Build(entry, PlayerA, false, HumanCatalogueFixture.Versions, out _)!;
        var manifest = package.Manifest;
        Assert.Null(HumanSharedEntry.Build(manifest, _ => null, out var refusal));
        Assert.NotNull(refusal);
        // Bytes of the wrong length for the declared size are not the member.
        Assert.Null(HumanSharedEntry.Build(manifest, hash => package.MemberBytes.TryGetValue(hash, out var bytes) ? bytes.Append((byte)' ').ToArray() : null, out refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void ADifferentRecordedVersionOrAMissingProfileFlagRefusesTheShare()
    {
        var entry = Entry();
        Assert.Null(HumanSharedPackageBuilder.Build(entry, PlayerA, false,
            HumanCatalogueFixture.Versions with { Penumbra = "0.0.0.1" }, out var refusal));
        Assert.Contains("Penumbra", refusal, StringComparison.Ordinal);
        Assert.Null(HumanSharedPackageBuilder.Build(entry, PlayerA, withProfile: true, HumanCatalogueFixture.Versions, out refusal));
        Assert.Contains("no Customize+ profile", refusal, StringComparison.Ordinal);
        // A captured path swap is never shared in this candidate.
        Assert.Null(HumanSharedPackageBuilder.Build(Entry(swap: true), PlayerA, false, HumanCatalogueFixture.Versions, out refusal));
        Assert.Contains("1 swap(s)", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("share Rsc One", HumanSharedStep.Share, "Rsc One", false)]
    [InlineData("share Rsc One with-customize+", HumanSharedStep.Share, "Rsc One", true)]
    [InlineData("unshare Rsc One", HumanSharedStep.Unshare, "Rsc One", false)]
    [InlineData("place-shared Rsc One with-customize+", HumanSharedStep.Place, "Rsc One", true)]
    [InlineData("move-shared", HumanSharedStep.Move, "", false)]
    [InlineData("remove-shared", HumanSharedStep.Remove, "", false)]
    [InlineData("shared-status", HumanSharedStep.Status, "", false)]
    [InlineData("share ", HumanSharedStep.None, "", false)]
    [InlineData("share  with-customize+", HumanSharedStep.None, "", false)]
    [InlineData("place-shared", HumanSharedStep.None, "", false)]
    [InlineData("move-shared now", HumanSharedStep.None, "", false)]
    [InlineData("spawn Rsc One", HumanSharedStep.None, "", false)]
    [InlineData("remove", HumanSharedStep.None, "", false)]
    public void TheStepsParseExactly(string text, HumanSharedStep step, string label, bool withProfile)
    {
        Assert.Equal(step, HumanSharedCommand.TryParse(text, out var parsedLabel, out var parsedProfile));
        Assert.Equal(label, parsedLabel);
        Assert.Equal(withProfile, parsedProfile);
    }

    [Fact]
    public void TheReceivedEntryIdAndLabelComeOnlyFromTheBundleId()
    {
        var id = ContentHash.Of("synthetic"u8);
        Assert.Equal("shared " + id.Value[..8], HumanSharedEntry.LabelOf(id));
        Assert.Equal(HumanSharedEntry.EntryIdOf(id), HumanSharedEntry.EntryIdOf(id));
        Assert.NotEqual(HumanSharedEntry.EntryIdOf(id), HumanSharedEntry.EntryIdOf(ContentHash.Of("other"u8)));
    }
}
#endif
