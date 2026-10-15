using System.Globalization;

namespace RSyncStageCraft.Core.Actors;

/// <summary>
/// The pinned BNpcName-to-BNpcBase pairs (LuminaSupplemental <c>BNpcLink</c>,
/// GPL-3.0; source commit, attribution and license beside the data file). It only says which name rows belong to which
/// BNpcBase row; the names themselves come from the player's own BNpcName
/// sheet, in the client's language. Local display only: nothing here is sent.
/// </summary>
public sealed class ActorNameLinks
{
    private readonly Dictionary<uint, uint[]> namesByBase;

    private ActorNameLinks(Dictionary<uint, uint[]> namesByBase) => this.namesByBase = namesByBase;

    public static ActorNameLinks Empty { get; } = new(new());

    public int BaseCount => namesByBase.Count;

    /// <summary>The BNpcName rows of a BNpcBase row (none, one or several), in ascending order.</summary>
    public IReadOnlyList<uint> NameIds(uint bnpcBaseId) =>
        namesByBase.TryGetValue(bnpcBaseId, out var ids) ? ids : [];

    /// <summary>
    /// Parses the pinned CSV: lines starting with '#' are comments, the header
    /// is <c>BNpcNameId,BNpcBaseId</c>, every other line one pair. A malformed
    /// line is skipped (it only costs a name, never an identity).
    /// </summary>
    public static ActorNameLinks Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var pairs = new Dictionary<uint, SortedSet<uint>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("BNpcNameId", StringComparison.Ordinal))
                continue;
            var comma = line.IndexOf(',');
            if (comma <= 0 ||
                !uint.TryParse(line.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out var nameId) ||
                !uint.TryParse(line.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var baseId) ||
                nameId == 0 || baseId == 0)
                continue;
            if (!pairs.TryGetValue(baseId, out var names))
                pairs[baseId] = names = new SortedSet<uint>();
            names.Add(nameId);
        }
        return new(pairs.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
    }
}

/// <summary>One creature the player can place: a supported BNpcBase row (the one wire identity).</summary>
public sealed record ActorCatalogEntry(
    uint BNpcBaseId,
    ActorModelKind Kind,
    string ModelCode,
    IReadOnlyList<string> Names)
{
    /// <summary>"Monster · m0024 b1 v1 · #1234".</summary>
    public string Label => $"{(Kind == ActorModelKind.Monster ? "Monster" : "Demihuman")} · {ModelCode} · #{BNpcBaseId}";

    /// <summary>The first name, or the label when the creature has none.</summary>
    public string DisplayName => Names.Count > 0 ? Names[0] : Label;

    internal string SearchText { get; init; } = string.Empty;
}

/// <summary>
/// The actor catalogue: BNpcBase rows the resolver can draw (ModelChara
/// type 2 or 3), de-duplicated by visual key (the ModelChara row, plus the
/// NpcEquip row for demihumans; the lowest BNpcBase row represents the key and
/// the names of every row with that key are kept). Searchable by name, row and
/// model code.
/// </summary>
public sealed class ActorCatalog
{
    private readonly ActorCatalogEntry[] entries;
    private readonly Dictionary<uint, ActorCatalogEntry> byBase;

    public ActorCatalog(IEnumerable<ActorCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        this.entries = entries.OrderBy(entry => entry.BNpcBaseId).ToArray();
        byBase = this.entries.ToDictionary(entry => entry.BNpcBaseId);
    }

    public static ActorCatalog Empty { get; } = new([]);

    public int Count => entries.Length;

    public IReadOnlyList<ActorCatalogEntry> Entries => entries;

    public bool TryGet(uint bnpcBaseId, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ActorCatalogEntry entry) =>
        byBase.TryGetValue(bnpcBaseId, out entry);

    /// <summary>Entries whose name, model code or row contains every word of <paramref name="query"/>.</summary>
    public IEnumerable<ActorCatalogEntry> Search(string? query)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return entries;
        return entries.Where(entry => words.All(word =>
            entry.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    /// <param name="nameOf">The local BNpcName sheet's name for a row, or null.</param>
    public static ActorCatalog Build(
        IActorGameData data,
        ActorAppearanceResolver resolver,
        ActorNameLinks links,
        Func<uint, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(nameOf);

        var groups = new Dictionary<(uint ModelChara, uint NpcEquip), (ActorDrawPlan First, List<uint> Bases)>();
        foreach (var baseId in data.BNpcBaseIds().Where(id => id != 0).Distinct().OrderBy(id => id))
        {
            if (resolver.Resolve(baseId).Plan is not { } plan)
                continue;
            var key = (plan.ModelCharaId, plan.Kind == ActorModelKind.Demihuman ? plan.NpcEquipId : 0u);
            if (groups.TryGetValue(key, out var group))
                group.Bases.Add(baseId);
            else
                groups[key] = (plan, [baseId]);
        }

        var built = new List<ActorCatalogEntry>(groups.Count);
        foreach (var (first, bases) in groups.Values)
        {
            var names = new List<string>();
            foreach (var baseId in bases)
            {
                foreach (var nameId in links.NameIds(baseId))
                {
                    string? name;
                    try
                    {
                        name = nameOf(nameId);
                    }
                    catch (Exception)
                    {
                        name = null;
                    }
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name.Trim());
                }
            }
            var entry = new ActorCatalogEntry(first.BNpcBaseId, first.Kind, first.ModelCode, names);
            built.Add(entry with
            {
                SearchText = string.Join(' ', names) + " " + entry.Label + " " +
                    string.Join(' ', bases.Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))),
            });
        }
        return new ActorCatalog(built);
    }
}

/// <summary>
/// "Use my target". Read-only: the caller passes what
/// the current target is; nothing here sets, clears or changes it. Only a
/// battle NPC whose nonzero BNpcBase row resolves locally to a supported
/// monster or demihuman is accepted.
/// </summary>
public static class ActorTargetSelection
{
    public const string NotSupported = "Not a supported monster or demihuman";
    public const string NoTarget = "No target";

    public static ActorDrawPlanResult FromTarget(bool hasTarget, bool isBattleNpc, uint baseId, ActorAppearanceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (!hasTarget)
            return ActorDrawPlanResult.NotDrawn(NoTarget);
        if (!isBattleNpc || baseId == 0)
            return ActorDrawPlanResult.NotDrawn(NotSupported);
        var resolved = resolver.Resolve(baseId);
        return resolved.Drawable ? resolved : ActorDrawPlanResult.NotDrawn(NotSupported);
    }
}
