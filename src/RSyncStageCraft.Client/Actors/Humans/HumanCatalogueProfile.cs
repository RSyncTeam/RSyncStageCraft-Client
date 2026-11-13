#if RSC_DEVELOPMENT
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RSyncStageCraft.Core.Actors.Humans;

// HV-5 M3 (Debug only): the owner-approved Customize+ profile of a catalogue
// entry (owner decision D2: the normal profile active on the owner's own
// character at capture, read only with the explicit `with-customize+` flag).
// Nothing here names a profile, a template, a character or a world: the
// profile's JSON has no field for any of them, and the id is shown only as a
// short hash. No refusal carries a bone name, a value or a parser message.

/// <summary>
/// The capture's Customize+ reads (IPC API 6.4, by label), read only, on the
/// game thread. The only implementation is the plugin's Debug-only adapter.
/// The profile content is read only for the id the active-profile read just
/// returned (owner decision D-M3-2); no member takes an id.
/// </summary>
public interface IHumanCatalogueProfileReads
{
    /// <summary>The installed, loaded Customize+ plugin's version (read only); null only when none is loaded; throws when the list cannot be read.</summary>
    string? PluginVersion { get; }

    /// <summary><c>CustomizePlus.General.GetApiVersion</c>, or null.</summary>
    (int Major, int Minor)? ApiVersion { get; }

    /// <summary><c>CustomizePlus.General.IsValid</c>.</summary>
    bool IsValid { get; }

    /// <summary>
    /// <c>CustomizePlus.Profile.GetActiveProfileIdOnCharacter(index)</c>, then,
    /// only when it answered Success with a non-empty id,
    /// <c>CustomizePlus.Profile.GetByUniqueId</c> for that id, in the same call.
    /// The id itself never leaves the adapter.
    /// </summary>
    HumanCatalogueProfileAnswer ActiveProfile(ushort objectIndex);

    /// <summary>The owner's profile fingerprint, as HV-4's stage takes it (short hashes only).</summary>
    string OwnerProfiles();
}

/// <summary>
/// What the two reads answered: the active-profile code, a short hash of the
/// id (8 hex characters of its SHA-256, never the id), and the content read's
/// code and JSON (null when the content was not read).
/// </summary>
public sealed record HumanCatalogueProfileAnswer(int ActiveCode, string? IdTag, int? ContentCode, string? Json)
{
    /// <summary>Never the profile.</summary>
    public override string ToString() => $"Customize+ profile answer ({ActiveCode})";
}

/// <summary>
/// The profile's bones against the captured skeleton (owner decisions D-M3-3
/// and D-M3-8): <see cref="Base"/> present in partial 0, <see cref="Other"/>
/// present only in a later partial, <see cref="Absent"/> on no partial. Absent
/// bones are kept in the stored profile and counted, never dropped.
/// </summary>
public sealed record HumanCatalogueProfileCounts(int Bones, int Base, int Other, int Absent)
{
    /// <summary>The counts as log lines show them; no bone name.</summary>
    public string Describe() => $"{Bones} bone(s) ({Base} base, {Other} other partial, {Absent} absent)";
}

/// <summary>A profile read at capture, checked and canonical, with its counts and the id's short hash (memory only until stored).</summary>
public sealed record HumanCatalogueProfileRead(string Profile, string ProfileHash, HumanCatalogueProfileCounts Counts, string IdTag)
{
    /// <summary>Never the profile.</summary>
    public override string ToString() => $"catalogue profile read ({Counts.Bones} bone(s))";
}

/// <summary>
/// The stored profile's form. <see cref="Canonicalize"/> accepts exactly the
/// JSON Customize+ 2.2.1.3 writes for <c>IPCCharacterProfile</c>
/// (Newtonsoft, <c>DefaultValueHandling.Ignore</c>) and writes it again with
/// every field explicit:
/// <list type="bullet">
/// <item>one top-level property, <c>Bones</c>: an object of 1 to
/// <see cref="MaximumBones"/> bones (D-M3-5), keys sorted ordinally;</item>
/// <item>a bone key: 1 to <see cref="MaximumKeyLength"/> printable ASCII
/// characters other than <c>"</c> and <c>\</c> (D-M3-11); a repeated key is
/// refused;</item>
/// <item>a bone: only the eight properties of <c>IPCBoneTransform</c>, exact
/// case, none repeated; a missing one is written as its default;</item>
/// <item>a vector: only <c>X</c>, <c>Y</c> and <c>Z</c>, each a number, none
/// repeated; 0 to 3 of them may be present (Customize+ omits a zero
/// component); a missing one is written as <c>0</c>, and a missing vector as
/// all zeros (the <c>IPCBoneTransform</c> default, so a missing
/// <c>Scaling</c> is zero, not one);</item>
/// <item>a number: its validated token exactly as written (at most
/// <see cref="MaximumNumberLength"/> characters, finite as a float, and an
/// integer token at most 18 digits), never re-formatted;</item>
/// <item>a flag: <c>true</c> or <c>false</c>; a missing one is
/// <c>false</c>.</item>
/// </list>
/// The text read and the canonical text are each at most
/// <see cref="MaximumTextBytes"/> UTF-8 bytes (D-M3-5). Canonicalizing the
/// canonical text returns it unchanged. Read by Customize+, the canonical text
/// gives the same bone transforms as the text it came from, exactly up to the
/// sign of a zero (Customize+ itself omits <c>-0</c>, which then reads as
/// <c>+0</c>).
/// </summary>
public static class HumanCatalogueProfile
{
    public const int MaximumBones = 1024;
    public const int MaximumTextBytes = 256 * 1024;
    public const int MaximumKeyLength = 64;
    public const int MaximumNumberLength = 32;

    private static readonly string[] Vectors = ["Translation", "Rotation", "Scaling", "ChildScaling"];
    private static readonly string[] Flags = ["PropagateTranslation", "PropagateRotation", "PropagateScale", "ChildScaleIndependent"];
    private static readonly string[] Components = ["X", "Y", "Z"];

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        MaxDepth = 8,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>
    /// The canonical text and its bone keys (ordinal order), or null with a
    /// fixed-text refusal (counts at most; never a key, a value or a parser
    /// message). Never throws for bad input.
    /// </summary>
    public static string? Canonicalize(string? json, out IReadOnlyList<string> bones, out string? refusal)
    {
        bones = [];
        refusal = null;
        if (string.IsNullOrEmpty(json))
            return Refuse(out refusal, "the profile text is empty");
        if (json.Length > MaximumTextBytes || Encoding.UTF8.GetByteCount(json) > MaximumTextBytes)
            return Refuse(out refusal, $"the profile text is larger than {MaximumTextBytes} bytes");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ParseOptions);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // The parser's message names a JSON path, so a bone key: never shown.
            return Refuse(out refusal, "the profile is not valid JSON");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Refuse(out refusal, "the profile is not a JSON object");
            JsonElement? found = null;
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "Bones", StringComparison.Ordinal) || found is not null)
                    return Refuse(out refusal, "the profile has a property other than one Bones");
                found = property.Value;
            }
            if (found is not { ValueKind: JsonValueKind.Object } map)
                return Refuse(out refusal, "the profile has no Bones object");

            var byKey = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var bone in map.EnumerateObject())
            {
                if (!IsBoneKey(bone.Name))
                    return Refuse(out refusal, $"a bone name is not 1 to {MaximumKeyLength} printable ASCII characters other than quote and backslash");
                if (!byKey.TryAdd(bone.Name, bone.Value))
                    return Refuse(out refusal, "a bone name appears twice");
                if (byKey.Count > MaximumBones)
                    return Refuse(out refusal, $"the profile has more than {MaximumBones} bones");
            }
            if (byKey.Count == 0)
                return Refuse(out refusal, "the profile has no bone (every template may be disabled)");

            var text = new StringBuilder(byKey.Count * 320);
            text.Append("{\"Bones\":{");
            var first = true;
            foreach (var (key, transform) in byKey)
            {
                if (!first)
                    text.Append(',');
                first = false;
                text.Append('"').Append(key).Append("\":");
                if (Transform(transform, text) is { } problem)
                    return Refuse(out refusal, problem);
            }
            text.Append("}}");
            var canonical = text.ToString();
            if (Encoding.UTF8.GetByteCount(canonical) > MaximumTextBytes)
                return Refuse(out refusal, $"the canonical profile text is larger than {MaximumTextBytes} bytes");
            bones = byKey.Keys.ToList();
            return canonical;
        }
    }

    /// <summary>A bone key: 1 to 64 printable ASCII characters other than <c>"</c> and <c>\</c> (D-M3-11).</summary>
    public static bool IsBoneKey(string? key) =>
        key is { Length: > 0 and <= MaximumKeyLength } && key.All(character => character is >= ' ' and <= '~' and not '"' and not '\\');

    /// <summary>
    /// The profile's bones against the captured skeleton's bone names per
    /// partial (slot order; null for a slot with no resource). Ordinal, as
    /// Customize+ binds a bone by its name.
    /// </summary>
    public static HumanCatalogueProfileCounts Classify(IReadOnlyList<string> bones, IReadOnlyList<IReadOnlyList<string>?> skeleton)
    {
        ArgumentNullException.ThrowIfNull(bones);
        ArgumentNullException.ThrowIfNull(skeleton);
        var basePartial = new HashSet<string>(StringComparer.Ordinal);
        if (skeleton.Count > 0 && skeleton[0] is { } zero)
            basePartial.UnionWith(zero);
        var later = new HashSet<string>(skeleton.Skip(1).Where(names => names is not null).SelectMany(names => names!), StringComparer.Ordinal);
        int inBase = 0, other = 0, absent = 0;
        foreach (var bone in bones)
        {
            if (basePartial.Contains(bone))
                inBase++;
            else if (later.Contains(bone))
                other++;
            else
                absent++;
        }
        return new HumanCatalogueProfileCounts(bones.Count, inBase, other, absent);
    }

    /// <summary>
    /// True when the bone names were read from the skeleton the summary
    /// describes: the same partial count, the base slot with a resource, and
    /// a resource in exactly the slots the summary shows one.
    /// </summary>
    public static bool MatchesSummary(IReadOnlyList<IReadOnlyList<string>?> skeleton, IReadOnlyList<string> summary)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(summary);
        if (!HumanCatalogueSkeletonSummary.Comparable(summary) || summary.Count != skeleton.Count + 1 || skeleton.Count == 0 || skeleton[0] is null)
            return false;
        for (var index = 0; index < skeleton.Count; index++)
        {
            var empty = summary[index + 1] == $"skeleton {index}: no resource";
            if (empty != (skeleton[index] is null))
                return false;
        }
        return true;
    }

    /// <summary>One bone's transform, written with every field explicit; null, or a fixed-text problem.</summary>
    private static string? Transform(JsonElement transform, StringBuilder text)
    {
        if (transform.ValueKind != JsonValueKind.Object)
            return "a bone is not a JSON object";
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in transform.EnumerateObject())
        {
            if ((!Vectors.Contains(property.Name, StringComparer.Ordinal) && !Flags.Contains(property.Name, StringComparer.Ordinal)) ||
                !fields.TryAdd(property.Name, property.Value))
                return "a bone has an unknown or repeated property";
        }
        text.Append('{');
        foreach (var name in Vectors)
        {
            text.Append('"').Append(name).Append("\":{");
            var components = new Dictionary<string, string>(StringComparer.Ordinal);
            if (fields.TryGetValue(name, out var vector))
            {
                if (vector.ValueKind != JsonValueKind.Object)
                    return "a vector is not a JSON object";
                foreach (var component in vector.EnumerateObject())
                {
                    if (!Components.Contains(component.Name, StringComparer.Ordinal) || components.ContainsKey(component.Name))
                        return "a vector has an unknown or repeated component";
                    if (Number(component.Value) is not { } token)
                        return $"a number is not finite, within float range, and at most {MaximumNumberLength} characters";
                    components.Add(component.Name, token);
                }
            }
            for (var index = 0; index < Components.Length; index++)
            {
                if (index > 0)
                    text.Append(',');
                text.Append('"').Append(Components[index]).Append("\":").Append(components.GetValueOrDefault(Components[index], "0"));
            }
            text.Append("},");
        }
        for (var index = 0; index < Flags.Length; index++)
        {
            var value = false;
            if (fields.TryGetValue(Flags[index], out var flag))
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return "a flag is not true or false";
                value = flag.ValueKind == JsonValueKind.True;
            }
            if (index > 0)
                text.Append(',');
            text.Append('"').Append(Flags[index]).Append("\":").Append(value ? "true" : "false");
        }
        text.Append('}');
        return null;
    }

    /// <summary>The number's token exactly as written, or null when it is not a short, finite number within float range.</summary>
    private static string? Number(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number)
            return null;
        var token = element.GetRawText();
        // An integer token (no fraction or exponent) is kept within 18 digits, which every reader takes as a 64-bit integer.
        var integer = token.AsSpan().TrimStart('-');
        if (token.Length is 0 or > MaximumNumberLength ||
            (integer.IndexOfAny('.', 'e', 'E') < 0 && integer.Length > 18) ||
            !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !float.IsFinite((float)value))
            return null;
        return token;
    }

    private static string? Refuse(out string? refusal, string reason)
    {
        refusal = reason;
        return null;
    }
}

/// <summary>
/// HV-5 M3: the capture's profile read, on the game thread, in the same
/// framework call as the rest of the capture's read.
/// <list type="number">
/// <item>HV-4's gate: Customize+ exactly 2.2.1.3, IPC API exactly (6, 4),
/// <c>IsValid</c>.</item>
/// <item>The owner fingerprint, then the active profile and its content (one
/// call; both reads are synchronous on the game thread, so the content cannot
/// change between them), then the fingerprint again; a difference refuses.</item>
/// <item>The answers: no active normal profile refuses (D-M3-4); so does
/// any other code, a missing id or a missing text.</item>
/// <item>The text is canonicalized (<see cref="HumanCatalogueProfile"/>).</item>
/// <item>The bones are classified against the captured skeleton's names,
/// read in the same call; a profile with no bone present on it is refused
/// (D-M3-10).</item>
/// </list>
/// Limitations: a temporary profile on the owner's character (L-1), an open
/// Customize+ editor (L-2) and the global default profile are not readable
/// here; in a rare priority case Customize+ shows another of the character's
/// profiles than the one read (L-4).
/// </summary>
public static class HumanCatalogueProfileReader
{
    public static (HumanCatalogueProfileRead? Read, string? Refusal) Read(
        IHumanCatalogueProfileReads reads, ushort objectIndex, IReadOnlyList<IReadOnlyList<string>?>? skeleton, IReadOnlyList<string> summary)
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(summary);
        const string lead = "Customize+ profile: ";
        HumanCatalogueProfileAnswer answer;
        try
        {
            if (reads.PluginVersion is not { } version)
                return (null, lead + "Customize+ is not loaded");
            if (version != HumanActorCustomizePlusStage.RequiredVersion)
                return (null, lead + $"Customize+ {version} is not the checked {HumanActorCustomizePlusStage.RequiredVersion}");
            if (reads.ApiVersion is not { } api || api != (HumanActorCustomizePlusStage.RequiredApiMajor, HumanActorCustomizePlusStage.RequiredApiMinor))
                return (null, lead + $"the IPC API is not {HumanActorCustomizePlusStage.RequiredApiMajor}.{HumanActorCustomizePlusStage.RequiredApiMinor}");
            if (!reads.IsValid)
                return (null, lead + "Customize+ reports it is not valid");
            var before = reads.OwnerProfiles();
            answer = reads.ActiveProfile(objectIndex);
            if (!string.Equals(before, reads.OwnerProfiles(), StringComparison.Ordinal))
                return (null, lead + "the owner's profiles changed during the read");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (null, lead + $"a read failed ({exception.GetType().Name})");
        }
        switch (answer.ActiveCode)
        {
            case HumanActorCustomizePlusStage.Success when answer.IdTag is { Length: 8 }:
                break;
            case HumanActorCustomizePlusStage.Success:
            case HumanActorCustomizePlusStage.ProfileNotFound:
                return (null, lead + "no normal profile is active on the character (temporary profiles, the editor and the global default profile cannot be read)");
            case HumanActorCustomizePlusStage.InvalidCharacter:
                return (null, lead + "Customize+ does not accept the character (InvalidCharacter)");
            default:
                return (null, lead + $"the active-profile read answered {answer.ActiveCode}");
        }
        if (answer.ContentCode != HumanActorCustomizePlusStage.Success || answer.Json is null)
        {
            return (null, lead + (answer.ContentCode switch
            {
                HumanActorCustomizePlusStage.ProfileNotFound => "the active profile could not be read (ProfileNotFound)",
                null => "the active profile's content was not read",
                HumanActorCustomizePlusStage.Success => "the active profile's content is missing",
                _ => $"the content read answered {answer.ContentCode}",
            }));
        }
        if (HumanCatalogueProfile.Canonicalize(answer.Json, out var bones, out var invalid) is not { } canonical)
            return (null, lead + invalid);
        if (skeleton is null || !HumanCatalogueProfile.MatchesSummary(skeleton, summary))
            return (null, lead + "the captured skeleton's bone names could not be read");
        var counts = HumanCatalogueProfile.Classify(bones, skeleton);
        if (counts.Base + counts.Other == 0)
            return (null, lead + $"none of its {counts.Bones} bone(s) is on the captured skeleton");
        return (new HumanCatalogueProfileRead(canonical, HumanCatalogueEntry.HashOf(canonical), counts, answer.IdTag!), null);
    }
}
#endif
