using System.Text.Json.Serialization;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Scenes;

public static class SceneFormat
{
    /// <summary>
    /// Scene format 2 (protocol 7) adds the <c>human</c> record. A server
    /// always writes the current format; it still reads format 1, which can
    /// hold no human record. Older servers refuse a format-2 file whole.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>The oldest format still read.</summary>
    public const int FirstReadableVersion = 1;

    /// <summary>The first format that may hold a <c>human</c> record.</summary>
    public const int HumanRecordVersion = 2;

    public static bool IsReadable(int version) =>
        version is >= FirstReadableVersion and <= CurrentVersion;
}

/// <summary>
/// Explicit JSON union for an authoritative scene object. Keeping the union
/// closed prevents an untrusted discriminator from selecting arbitrary CLR
/// types during scene loading.
/// </summary>
public sealed record SceneObjectRecord(
    RscObjectKind Kind,
    ActorState? Actor = null,
    FurnitureState? Furniture = null,
    LightState? Light = null,
    HumanActorState? Human = null)
{
    [JsonIgnore]
    public AuthoritativeObjectState? State => Kind switch
    {
        RscObjectKind.Actor => Actor,
        RscObjectKind.Furniture => Furniture,
        RscObjectKind.Light => Light,
        RscObjectKind.Human => Human,
        _ => null,
    };

    [JsonIgnore]
    public bool IsValid
    {
        get
        {
            if (!Enum.IsDefined(Kind))
                return false;

            var populated =
                (Actor is null ? 0 : 1) +
                (Furniture is null ? 0 : 1) +
                (Light is null ? 0 : 1) +
                (Human is null ? 0 : 1);
            return populated == 1 &&
                State is { IsValid: true } state &&
                state.Kind == Kind;
        }
    }

    public static SceneObjectRecord FromState(AuthoritativeObjectState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state switch
        {
            ActorState actor => new(RscObjectKind.Actor, Actor: actor),
            FurnitureState furniture => new(RscObjectKind.Furniture, Furniture: furniture),
            LightState light => new(RscObjectKind.Light, Light: light),
            HumanActorState human => new(RscObjectKind.Human, Human: human),
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state.GetType(),
                "Unsupported authoritative object state."),
        };
    }
}

/// <summary>
/// Versioned authoritative JSON scene. <see cref="Location"/> records where
/// the scene lives (territory plus housing address); it is optional until the
/// client always captures it. Tombstones remain in the same scene
/// generation as live objects so a stale document cannot silently resurrect a
/// deleted stable object identity.
/// </summary>
public sealed record SceneDocument(
    int FormatVersion,
    RscSceneId SceneId,
    long SceneGeneration,
    DateTimeOffset SavedAtUtc,
    IReadOnlyList<SceneObjectRecord> Objects,
    IReadOnlyList<ObjectTombstone> Tombstones,
    SceneLocation? Location = null)
{
    [JsonIgnore]
    public bool IsValid => TryGetValidatedCollections(out _, out _);

    public SceneDocument Canonicalize()
    {
        if (!TryCanonicalize(out var canonical))
            throw new InvalidOperationException("Cannot canonicalize an invalid scene document.");

        return canonical;
    }

    public bool HasSameAuthoritativeContent(SceneDocument other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!TryCanonicalize(out var left) ||
            !other.TryCanonicalize(out var right))
        {
            return false;
        }

        return left.FormatVersion == right.FormatVersion &&
            left.SceneId == right.SceneId &&
            left.SceneGeneration == right.SceneGeneration &&
            left.Location == right.Location &&
            left.Objects.SequenceEqual(right.Objects) &&
            left.Tombstones.SequenceEqual(right.Tombstones);
    }

    public bool TryCanonicalize(out SceneDocument canonical)
    {
        if (!TryGetValidatedCollections(out var objects, out var tombstones))
        {
            canonical = null!;
            return false;
        }

        canonical = this with
        {
            Objects = objects
                .OrderBy(record => record.State!.ObjectId.Value)
                .ToArray(),
            Tombstones = tombstones
                .OrderBy(tombstone => tombstone.ObjectId.Value)
                .ToArray(),
        };
        return true;
    }

    private bool TryGetValidatedCollections(
        out SceneObjectRecord[] objects,
        out ObjectTombstone[] tombstones)
    {
        if (!SceneFormat.IsReadable(FormatVersion) ||
            !SceneId.IsValid ||
            SceneGeneration <= 0 ||
            SavedAtUtc == default ||
            SavedAtUtc.Offset != TimeSpan.Zero ||
            Location is { IsValid: false } ||
            !ProtocolLimits.TryCopyBoundedPair(
                Objects,
                Tombstones,
                ProtocolLimits.MaximumObjectsPerSnapshot,
                out objects,
                out tombstones) ||
            objects.Any(record =>
                record is not { IsValid: true } ||
                record.State!.SceneId != SceneId ||
                // The location is stored once, on the scene.
                record.State.Location is not null ||
                // Format 1 cannot hold a human record.
                (record.Kind == RscObjectKind.Human && FormatVersion < SceneFormat.HumanRecordVersion)) ||
            tombstones.Any(tombstone => !tombstone.IsValid))
        {
            objects = [];
            tombstones = [];
            return false;
        }

        var identities = new HashSet<RscObjectId>();
        return objects.All(record => identities.Add(record.State!.ObjectId)) &&
            tombstones.All(tombstone => identities.Add(tombstone.ObjectId));
    }
}
