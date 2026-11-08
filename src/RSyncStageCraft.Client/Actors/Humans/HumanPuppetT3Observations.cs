#if RSC_DEVELOPMENT
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// T3-1M's read-only puppet observations after the draw (P1-M): pure text
/// shaping for the puppet's own skeleton summary and the skeleton-resource
/// entries in Penumbra's per-object resource report. It reads nothing, writes
/// nothing and never sees a file's contents. Debug only: compiled out of Release.
/// </summary>
/// <remarks>
/// A <em>hashed path</em> is the first 16 lowercase hexadecimal characters of
/// the SHA-256 of the UTF-8 bytes of a path <em>string</em>, exactly as
/// Penumbra reported it (no case change, no separator change, no trimming).
/// Only the string is hashed: no resource file is opened, read or hashed, and
/// no file-system path appears in the log. Two equal hashes mean the two
/// reported strings were equal; they say nothing about the files' contents.
/// The prefix is unsalted, so anyone holding a log can confirm a guessed
/// path string against it: shared evidence keeps paths anonymized.
/// </remarks>
public static class HumanPuppetT3Observations
{
    /// <summary>The game-path extensions counted as skeleton resources, ordinal and case-insensitive.</summary>
    public static IReadOnlyList<string> SkeletonExtensions { get; } = [".sklb", ".skp", ".pbd", ".phyb"];

    /// <summary>The fixed provenance line logged before the observations.</summary>
    public const string Provenance =
        "puppet observations, read once after isolation held and only when the draw was observed. " +
        "Skeleton: the puppet's own draw object, as file-name hash, bone count and hashes of names, parents and reference pose. " +
        "Resources: Penumbra's per-object resource report (GetGameObjectResourcePaths) for the puppet's and the player's current draw objects, " +
        "actual path to game paths, as the loaded resources resolved when they loaded; it does not name the collection that resolved them. " +
        "Isolation (P6(a), P16) checked, at isolation time only, that the puppet's effective collection was the first set collection " +
        "in the expected order, with no individual entry and no Yourself path. The player's collection was not checked. Hashed paths are SHA-256 prefixes of the reported path strings; no file contents were read or hashed. " +
        "A human.pbd entry is resolution evidence only: it does not show which deformer the puppet loaded, that it affected output, " +
        "or that GAP-K4 is closed.";

    /// <summary>
    /// One object's skeleton-resource entries from Penumbra's per-object map
    /// (actual path to the game paths it serves). Lines, in order:
    /// <c>"&lt;prefix&gt; skeleton resources"</c> (counts, or <c>none</c> when
    /// Penumbra gave no map), then one <c>"&lt;prefix&gt; skeleton resource
    /// &lt;descriptor&gt;"</c> per resource, ordinal by descriptor. Each value is
    /// <c>"&lt;kind&gt;, actual &lt;hashed path&gt;"</c>; several actual paths for one
    /// descriptor are sorted and joined with <c>"; "</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> DescribeSkeletonResources(
        string prefix, IReadOnlyDictionary<string, HashSet<string>>? map)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var lines = new List<KeyValuePair<string, string>>();
        if (map is null)
        {
            lines.Add(new(prefix + " skeleton resources", "none"));
            return lines;
        }
        var entries = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var gamePaths = new HashSet<string>(StringComparer.Ordinal);
        var actualPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (actual, games) in map)
        {
            if (actual is null || games is null)
                continue;
            foreach (var game in games)
            {
                if (game is null || !IsSkeletonResource(game))
                    continue;
                gamePaths.Add(game);
                actualPaths.Add(actual);
                var descriptor = Descriptor(game);
                if (!entries.TryGetValue(descriptor, out var values))
                    entries[descriptor] = values = new SortedSet<string>(StringComparer.Ordinal);
                values.Add($"{Kind(actual, game)}, actual {HashPath(actual)}");
            }
        }
        lines.Add(new(prefix + " skeleton resources", $"{gamePaths.Count} game paths, {actualPaths.Count} actual paths"));
        foreach (var (descriptor, values) in entries)
            lines.Add(new($"{prefix} skeleton resource {descriptor}", string.Join("; ", values)));
        return lines;
    }

    /// <summary>True when the game path ends in a skeleton-resource extension, ignoring case.</summary>
    public static bool IsSkeletonResource(string gamePath) =>
        SkeletonExtensions.Any(extension => gamePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The resource's name in the log: the classifier's family for a recognised
    /// path (after the one reported deformer alias), else <c>other</c> with the
    /// extension and the game path's hash.
    /// </summary>
    public static string Descriptor(string gamePath)
    {
        SkeletonResourcePaths.TryCanonicalizeReported(gamePath, out var canonical);
        if (SkeletonResourcePaths.TryClassify(canonical, out var path))
            return path.Role == SkeletonResourceRole.Deformer
                ? "deformer"
                : $"{path.Role.ToString().ToLowerInvariant()} {path.Slot.ToString().ToLowerInvariant()} c{path.RaceCode:D4} {path.SetId:D4}";
        return $"other {Path.GetExtension(gamePath).ToLowerInvariant()} {HashPath(gamePath)}";
    }

    /// <summary>
    /// <c>redirected</c> when the actual path is a rooted file-system path
    /// (a file in a mod), <c>vanilla</c> when the actual string equals the game
    /// path ignoring case, else <c>swapped</c> (any other non-rooted string).
    /// Read from the strings only: <c>vanilla</c> means "actual equals game
    /// path", not that the file was proved to come from the game's archives.
    /// </summary>
    public static string Kind(string actualPath, string gamePath)
    {
        if (IsFileSystemPath(actualPath))
            return "redirected";
        return string.Equals(actualPath, gamePath, StringComparison.OrdinalIgnoreCase) ? "vanilla" : "swapped";
    }

    /// <summary>A drive-letter, UNC or rooted path. Game paths are relative and never match.</summary>
    public static bool IsFileSystemPath(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith('\\') || path.StartsWith('/');

    /// <summary>The hashed path: the first 16 hex characters of SHA-256 over the string's UTF-8 bytes.</summary>
    public static string HashPath(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16].ToLowerInvariant();

    /// <summary>
    /// The puppet's skeleton summary beside the player's, per partial index:
    /// one line with the partial counts and, for each index, <c>same</c> or the
    /// fields that differ. Inputs are the probe's <c>"&lt;who&gt; skeleton …"</c> lines.
    /// </summary>
    public static string CompareSkeletons(
        IReadOnlyList<KeyValuePair<string, string>> puppet, IReadOnlyList<KeyValuePair<string, string>> player)
    {
        var mine = Skeleton(puppet, "puppet");
        var theirs = Skeleton(player, "player");
        if (mine.Summary is not null || theirs.Summary is not null)
            return $"skeleton compared with the player: not comparable (puppet {mine.Summary ?? "read"}, player {theirs.Summary ?? "read"}).";
        var count = Math.Max(mine.Partials.Count, theirs.Partials.Count);
        var parts = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var left = i < mine.Partials.Count ? mine.Partials[i] : null;
            var right = i < theirs.Partials.Count ? theirs.Partials[i] : null;
            parts.Add($"partial {i} " + (left is null ? "player only" : right is null ? "puppet only" : CompareFields(left, right)));
        }
        return $"skeleton compared with the player: partials {mine.Partials.Count} and {theirs.Partials.Count}" +
            (parts.Count == 0 ? "." : "; " + string.Join("; ", parts) + ".");
    }

    /// <summary>
    /// The puppet's skeleton-resource lines beside the player's, by descriptor:
    /// counts of equal values, different values and descriptors on one side
    /// only, with the descriptors that are not equal. Both sides' lines are in
    /// the one list, told apart by their prefix.
    /// </summary>
    public static string CompareSkeletonResources(IReadOnlyList<KeyValuePair<string, string>> observations) =>
        CompareSkeletonResources(observations, observations);

    /// <summary>As above, with the puppet's and the player's lines from separate lists.</summary>
    public static string CompareSkeletonResources(
        IReadOnlyList<KeyValuePair<string, string>> puppet, IReadOnlyList<KeyValuePair<string, string>> player)
    {
        var mine = Resources(puppet, "puppet");
        var theirs = Resources(player, "player");
        if (mine is null || theirs is null)
            return "skeleton resources compared with the player: not comparable (" +
                (mine is null && theirs is null ? "neither report has a map" : mine is null ? "the puppet's report has no map" : "the player's report has no map") +
                " from Penumbra: none, unanswered or missing).";
        var same = mine.Keys.Where(key => theirs.TryGetValue(key, out var value) && value == mine[key]).ToArray();
        var different = mine.Keys.Where(key => theirs.TryGetValue(key, out var value) && value != mine[key]).ToArray();
        var puppetOnly = mine.Keys.Where(key => !theirs.ContainsKey(key)).ToArray();
        var playerOnly = theirs.Keys.Where(key => !mine.ContainsKey(key)).ToArray();
        var text = $"skeleton resources compared with the player: same {same.Length}, different {different.Length}, " +
            $"puppet only {puppetOnly.Length}, player only {playerOnly.Length}";
        foreach (var (label, keys) in new[] { ("different", different), ("puppet only", puppetOnly), ("player only", playerOnly) })
            if (keys.Length > 0)
                text += $"; {label}: {string.Join(", ", keys)}";
        return text + ".";
    }

    private static (string? Summary, List<string> Partials) Skeleton(IReadOnlyList<KeyValuePair<string, string>> lines, string who)
    {
        string? summary = null;
        var count = -1;
        var partials = new SortedDictionary<int, string>();
        foreach (var (key, value) in lines)
        {
            if (key == who + " skeleton")
                summary = value;
            else if (key == who + " skeleton partials")
                count = int.TryParse(value, out var parsed) ? parsed : -1;
            else if (key.StartsWith(who + " skeleton ", StringComparison.Ordinal) &&
                     int.TryParse(key.AsSpan((who + " skeleton ").Length), out var index))
                partials[index] = value;
        }
        if (summary is null && count < 0)
            summary = "missing";
        return (summary, partials.Values.ToList());
    }

    private static string CompareFields(string left, string right)
    {
        if (left == right)
            return "same";
        var a = Fields(left);
        var b = Fields(right);
        if (a is null || b is null)
            return "differs";
        var differing = a.Keys.Union(b.Keys).Where(key => !a.TryGetValue(key, out var x) || !b.TryGetValue(key, out var y) || x != y);
        return "differs in " + string.Join(", ", differing);
    }

    /// <summary>"file H, bones N, names H, …" as field to value, or null when the value is not a summary.</summary>
    private static Dictionary<string, string>? Fields(string value)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in value.Split(", "))
        {
            var space = part.LastIndexOf(' ');
            if (space <= 0)
                return null;
            fields[part[..space]] = part[(space + 1)..];
        }
        return fields;
    }

    private static Dictionary<string, string>? Resources(IReadOnlyList<KeyValuePair<string, string>> lines, string who)
    {
        var header = lines.FirstOrDefault(line => line.Key == who + " skeleton resources");
        if (header.Key is null || header.Value == "none" || header.Value.StartsWith("unanswered", StringComparison.Ordinal))
            return null;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in lines)
            if (key.StartsWith(who + " skeleton resource ", StringComparison.Ordinal))
                entries[key[(who + " skeleton resource ").Length..]] = value;
        return entries;
    }
}
#endif
