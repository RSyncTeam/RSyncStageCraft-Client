#nullable enable
using System.Text;
using RSyncStageCraft.Core.Actors;

namespace RSyncStageCraft.Tests;

/// <summary>The local resolver, catalogue, pinned name links and Use my target.</summary>
public sealed class ActorAppearanceResolverTests
{
    [Fact]
    public void MonsterNeedsOnlyItsModelChara()
    {
        var data = new FakeGameData();
        data.Monster(100, modelChara: 7, model: 24, scale: 1.5f);
        var plan = new ActorAppearanceResolver(data).Resolve(100).Plan!;
        Assert.Equal(ActorModelKind.Monster, plan.Kind);
        Assert.Equal(7u, plan.ModelCharaId);
        Assert.Equal(1.5f, plan.RowScale);
        Assert.Null(plan.Equipment);
        Assert.Equal("m0024 b1 v1", plan.ModelCode);
    }

    [Fact]
    public void DemihumanAddsItsNpcEquipWithDyes()
    {
        var data = new FakeGameData();
        data.Demihuman(200, modelChara: 8, npcEquip: 30, headModel: 0x0002_0011u);
        var plan = new ActorAppearanceResolver(data).Resolve(200).Plan!;
        Assert.Equal(ActorModelKind.Demihuman, plan.Kind);
        var equipment = plan.Equipment!;
        Assert.Equal(10, equipment.Slots.Count);
        Assert.Equal(new ActorEquipmentModel(0x11, 2, 5, 6), equipment.Slots[0]);
        Assert.Equal(new ActorWeaponModel(0x0101, 0x0002, 0x0003, 9, 0), equipment.MainHand);
        Assert.True(equipment.OffHand.IsEmpty);
    }

    [Fact]
    public void UnsupportedRowsAreNotDrawnWithTheReason()
    {
        var data = new FakeGameData();
        data.Demihuman(201, modelChara: 8, npcEquip: 0, headModel: 1);
        data.Demihuman(202, modelChara: 8, npcEquip: 31, headModel: 0);
        data.ModelCharas[9] = new ActorModelCharaRow(1, 1, 1, 1);
        data.Bases[203] = new ActorBNpcBaseRow(9, 1, 0);
        data.Bases[204] = new ActorBNpcBaseRow(0, 1, 0);
        var resolver = new ActorAppearanceResolver(data);
        Assert.Equal(ActorAppearanceResolver.DemihumanEquipmentMissing, resolver.Resolve(201).NotDrawnReason);
        Assert.Equal(ActorAppearanceResolver.DemihumanEquipmentMissing, resolver.Resolve(202).NotDrawnReason);
        Assert.Equal(ActorAppearanceResolver.NotMonsterOrDemihuman, resolver.Resolve(203).NotDrawnReason);
        Assert.Equal(ActorAppearanceResolver.UnknownCreature, resolver.Resolve(204).NotDrawnReason);
        Assert.Equal(ActorAppearanceResolver.UnknownCreature, resolver.Resolve(999).NotDrawnReason);
        Assert.Equal(ActorAppearanceResolver.UnknownCreature, resolver.Resolve(0).NotDrawnReason);
    }

    [Fact]
    public void PinnedNameLinksParseCommentsHeaderAndSeveralNames()
    {
        var links = ActorNameLinks.Parse(new StringReader(
            "# comment\nBNpcNameId,BNpcBaseId\n5,100\n4,100\nbad,line\n0,100\n6,0\n7,200\n"));
        Assert.Equal(new uint[] { 4, 5 }, links.NameIds(100));
        Assert.Equal(new uint[] { 7 }, links.NameIds(200));
        Assert.Empty(links.NameIds(300));
        Assert.Equal(2, links.BaseCount);
    }

    [Fact]
    public void TheEmbeddedDatasetIsPinnedAndLicensed()
    {
        var root = FindRepositoryRoot();
        var data = File.ReadAllText(Path.Combine(root, "src", "RSyncStageCraft", "Data", "BNpcLink.csv"), Encoding.UTF8);
        Assert.Contains("2a04329e5fb3ca6f22bc8d1915fe0d1382d59519", data, StringComparison.Ordinal);
        Assert.Contains("GNU General Public License v3.0", data, StringComparison.Ordinal);
        Assert.Contains("GNU GENERAL PUBLIC LICENSE",
            File.ReadAllText(Path.Combine(root, "src", "RSyncStageCraft", "Data", "BNpcLink.LICENSE")), StringComparison.Ordinal);
        Assert.True(ActorNameLinks.Parse(new StringReader(data)).BaseCount > 10_000);
    }

    [Fact]
    public void CatalogueDeduplicatesByVisualKeyAndUsesLocalNamesOrTheLabel()
    {
        var data = new FakeGameData();
        data.Monster(100, modelChara: 7, model: 24);
        data.Monster(101, modelChara: 7, model: 24);   // same visual: grouped with 100
        data.Monster(102, modelChara: 8, model: 25);   // no name: label
        data.Demihuman(200, modelChara: 9, npcEquip: 30, headModel: 1);
        data.Demihuman(201, modelChara: 9, npcEquip: 31, headModel: 2); // other equipment: separate
        data.ModelCharas[50] = new ActorModelCharaRow(1, 1, 1, 1);
        data.Bases[300] = new ActorBNpcBaseRow(50, 1, 0);              // human: excluded
        var links = ActorNameLinks.Parse(new StringReader("BNpcNameId,BNpcBaseId\n1,100\n2,101\n3,200\n"));
        var names = new Dictionary<uint, string> { [1] = "funguar", [2] = "Funguar", [3] = "goblin" };
        var catalog = ActorCatalog.Build(data, new ActorAppearanceResolver(data), links,
            id => names.TryGetValue(id, out var name) ? name : null);

        Assert.Equal(new uint[] { 100, 102, 200, 201 }, catalog.Entries.Select(entry => entry.BNpcBaseId));
        Assert.Equal(new[] { "funguar" }, catalog.Entries[0].Names);
        Assert.Equal("Monster · m0025 b1 v1 · #102", catalog.Entries[1].DisplayName);
        Assert.Equal("goblin", catalog.Entries[2].DisplayName);
        Assert.Single(catalog.Search("FUNGUAR"));
        Assert.Single(catalog.Search("#101"));
        Assert.Equal(2, catalog.Search("demihuman").Count());
        Assert.Single(catalog.Search("m0025"));
        Assert.False(catalog.TryGet(300, out _));
    }

    [Fact]
    public void UseMyTargetAcceptsOnlySupportedBattleNpcs()
    {
        var data = new FakeGameData();
        data.Monster(100, modelChara: 7, model: 24);
        data.ModelCharas[50] = new ActorModelCharaRow(1, 1, 1, 1);
        data.Bases[300] = new ActorBNpcBaseRow(50, 1, 0);
        var resolver = new ActorAppearanceResolver(data);

        Assert.Equal(100u, ActorTargetSelection.FromTarget(true, true, 100, resolver).Plan!.BNpcBaseId);
        Assert.Equal(ActorTargetSelection.NoTarget, ActorTargetSelection.FromTarget(false, false, 0, resolver).NotDrawnReason);
        Assert.Equal(ActorTargetSelection.NotSupported, ActorTargetSelection.FromTarget(true, false, 100, resolver).NotDrawnReason);
        Assert.Equal(ActorTargetSelection.NotSupported, ActorTargetSelection.FromTarget(true, true, 0, resolver).NotDrawnReason);
        Assert.Equal(ActorTargetSelection.NotSupported, ActorTargetSelection.FromTarget(true, true, 300, resolver).NotDrawnReason);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RSyncStageCraft-Client.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    internal sealed class FakeGameData : IActorGameData
    {
        public Dictionary<uint, ActorBNpcBaseRow> Bases { get; } = new();
        public Dictionary<uint, ActorModelCharaRow> ModelCharas { get; } = new();
        public Dictionary<uint, ActorNpcEquipRow> Equipment { get; } = new();

        public void Monster(uint baseId, uint modelChara, ushort model, float scale = 1)
        {
            ModelCharas[modelChara] = new ActorModelCharaRow(ActorAppearanceResolver.MonsterModelType, model, 1, 1);
            Bases[baseId] = new ActorBNpcBaseRow(modelChara, scale, 0);
        }

        public void Demihuman(uint baseId, uint modelChara, uint npcEquip, uint headModel)
        {
            ModelCharas[modelChara] = new ActorModelCharaRow(ActorAppearanceResolver.DemihumanModelType, 1003, 1, 2);
            Bases[baseId] = new ActorBNpcBaseRow(modelChara, 1, npcEquip);
            if (npcEquip != 0)
            {
                var slots = Enumerable.Range(0, 10)
                    .Select(index => index == 0 ? new ActorNpcEquipSlot(headModel, 5, 6) : new ActorNpcEquipSlot(0, 0, 0))
                    .ToArray();
                Equipment[npcEquip] = new ActorNpcEquipRow(
                    new ActorNpcEquipWeapon(0x0003_0002_0101UL, 9, 0),
                    new ActorNpcEquipWeapon(0, 0, 0),
                    slots);
            }
        }

        public bool TryGetBNpcBase(uint bnpcBaseId, out ActorBNpcBaseRow row) => Bases.TryGetValue(bnpcBaseId, out row);
        public bool TryGetModelChara(uint modelCharaId, out ActorModelCharaRow row) => ModelCharas.TryGetValue(modelCharaId, out row);
        public bool TryGetNpcEquip(uint npcEquipId, out ActorNpcEquipRow row) => Equipment.TryGetValue(npcEquipId, out row!);
        public IEnumerable<uint> BNpcBaseIds() => Bases.Keys;
    }
}
