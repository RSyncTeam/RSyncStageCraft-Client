using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Scenes;

namespace RSyncStageCraft.Tests;

/// <summary>
/// SD-17(a): the protocol-7 golden fixtures. A deterministic corpus (fixed
/// ids, times and values; synthetic identities only) of one valid frame per
/// <see cref="ObjectProtocolMessageType"/>, every human kind (state, create,
/// <c>humanTransform</c> patch, tombstone, snapshot, mutation and snapshot
/// completions), a scene-format-2 document carrying a human record, and a
/// format-3 human-package manifest; and malformed variants of them, each of
/// which every reader must refuse.
/// <list type="bullet">
/// <item>The bytes are those this Protocol's codec writes. They are checked in
/// under <c>Fixtures/protocol-7/</c> with <c>fixtures.sha256</c>, and emitted
/// only when <c>RSC_EMIT_GOLDEN_FIXTURES</c> names a folder (the emission
/// step; no test writes otherwise).</item>
/// <item>This Protocol must reproduce every checked-in valid fixture byte for
/// byte, decode it and re-encode it to the same bytes, and refuse every
/// malformed one. The server's equivalence test (SV-7) applies the same
/// checks with its own Core copy to the same files at the pinned commit; a
/// disagreement fails either side. Matching version numbers alone prove
/// nothing.</item>
/// </list>
/// </summary>
public sealed class Protocol7GoldenFixtures
{
    public const string EmitVariable = "RSC_EMIT_GOLDEN_FIXTURES";
    public const string HashFile = "fixtures.sha256";

    /// <summary>The corpus size: 20 frames and one manifest valid, 21 single-change variants malformed. The private Core's check (SV-7) requires the same.</summary>
    public const int ValidCount = 21;
    public const int MalformedCount = 21;

    private static readonly RscUserId PlayerA = new("player-a");
    private static readonly RscSceneId Scene = new(Fixed(1));
    private static readonly DateTimeOffset SavedAt = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly SceneLocation Location = new(128, null);

    private static Guid Fixed(int n) => new($"00000000-0000-4000-8000-{n:D12}");

    /// <summary>The synthetic bundle id (64 lowercase hex characters; not a real bundle).</summary>
    public static readonly string BundleId = Convert.ToHexString(SHA256.HashData("rsc-golden-human-bundle"u8)).ToLowerInvariant();

    private static SessionMessageEnvelope Envelope(ObjectProtocolMessageType type, Guid messageId) =>
        new(ProtocolConstants.CurrentVersion, type, messageId, 1, 2, 3);

    private static HumanActorState Human(int revision = 1, RscSceneId? scene = null) =>
        new(new RscObjectId(Fixed(10)), new ObjectRevision(revision), PlayerA, scene, BundleId, new Vector3State(1.5f, -2.25f, 3f), 0.75f);

    private static ActorState Actor(RscSceneId? scene = null) =>
        new(new RscObjectId(Fixed(11)), new ObjectRevision(1), PlayerA, scene, 1, new Vector3State(0, 0, 0), 0, 1, ActorControlState.Default);

    private static FurnitureState Furniture(RscSceneId? scene = null) =>
        new(new RscObjectId(Fixed(12)), new ObjectRevision(1), PlayerA, scene, "furniture:indoor:6000", new Vector3State(1, 2, 3), new(0, 0, 0, 1));

    private static LightState Light(RscSceneId? scene = null) =>
        new(new RscObjectId(Fixed(13)), new ObjectRevision(1), PlayerA, scene, LightIdentity.Point, new Vector3State(1, 2, 3), LightLimits.UnitScale);

    private static ObjectTombstone HumanTombstone => new(new RscObjectId(Fixed(10)), RscObjectKind.Human, new ObjectRevision(3));

    private static SceneDocument SceneTwo => new(SceneFormat.CurrentVersion, Scene, 1, SavedAt,
        [SceneObjectRecord.FromState(Human(scene: Scene)), SceneObjectRecord.FromState(Furniture(Scene))], []);

    /// <summary>The valid frames: name, message type and payload. Every message type appears at least once.</summary>
    public static IReadOnlyList<(string Name, ObjectProtocolMessageType Type, Guid MessageId, object Payload)> Frames()
    {
        var request = Fixed(100);
        var snapshot = new ObjectSnapshotMessage(new ServerSequence(4),
            new AuthoritativeObjectState[] { Human(), Actor(), Furniture(), Light() }, [HumanTombstone with { ObjectId = new(Fixed(14)) }]);
        return
        [
            ("create-object-human", ObjectProtocolMessageType.CreateObject, request,
                new CreateObjectRequest(request, new HumanActorCreatePayload(Scene, BundleId, new Vector3State(1.5f, -2.25f, 3f), 0.75f, Location))),
            ("update-object-human-transform", ObjectProtocolMessageType.UpdateObject, request,
                new UpdateObjectRequest(request, Human().ObjectId, new ObjectRevision(1), new HumanTransformPatch(new Vector3State(2, 0, -1), -1.5f))),
            ("update-object-human-yaw-only", ObjectProtocolMessageType.UpdateObject, request,
                new UpdateObjectRequest(request, Human().ObjectId, new ObjectRevision(2), new HumanTransformPatch(null, 0.25f))),
            ("delete-object-human", ObjectProtocolMessageType.DeleteObject, request,
                new DeleteObjectRequest(request, Human().ObjectId, new ObjectRevision(2))),
            ("object-snapshot-all-kinds", ObjectProtocolMessageType.ObjectSnapshot, Fixed(101), snapshot),
            ("object-created-human", ObjectProtocolMessageType.ObjectCreated, Fixed(102), new ObjectCreatedMessage(new ServerSequence(5), Human())),
            ("object-updated-human", ObjectProtocolMessageType.ObjectUpdated, Fixed(103), new ObjectUpdatedMessage(new ServerSequence(6), Human(2))),
            ("object-deleted-human", ObjectProtocolMessageType.ObjectDeleted, Fixed(104), new ObjectDeletedMessage(new ServerSequence(7), HumanTombstone)),
            ("acknowledge-objects", ObjectProtocolMessageType.AcknowledgeObjects, Fixed(105), new ObjectStreamAcknowledgement(new ServerSequence(7))),
            ("rejected-capacity", ObjectProtocolMessageType.Rejected, request, new ObjectRequestRejected(request, ProtocolRejectReason.CapacityExceeded, null)),
            ("request-object-snapshot", ObjectProtocolMessageType.RequestObjectSnapshot, request, new ObjectSnapshotRequest(request)),
            ("request-object-events", ObjectProtocolMessageType.RequestObjectEvents, request, new ObjectEventBatchRequest(request, new ServerSequence(4), 8)),
            ("save-scene", ObjectProtocolMessageType.SaveScene, request, new SaveSceneRequest(request, Scene, new ServerSequence(7))),
            ("read-scene", ObjectProtocolMessageType.ReadScene, request, new ReadSceneRequest(request, Scene)),
            ("object-event-batch-completed", ObjectProtocolMessageType.ObjectEventBatchCompleted, request,
                new ObjectEventBatchCompletion(request, ObjectEventBatchCompletionStatus.Advanced, new ServerSequence(4), new ServerSequence(7),
                    new ServerSequence(7), 3, false)),
            ("scene-save-completed", ObjectProtocolMessageType.SceneSaveCompleted, request, new SceneSaveCompletion(request, Scene, SceneSaveCompletionStatus.Saved)),
            ("scene-read-completed-format-2-human", ObjectProtocolMessageType.SceneReadCompleted, request,
                new SceneReadCompletion(request, Scene, SceneReadCompletionStatus.Loaded, SceneTwo with { Location = Location })),
            ("object-mutation-completed-human-create", ObjectProtocolMessageType.ObjectMutationCompleted, request,
                new ObjectMutationCompletion(request, ObjectMutationCompletionKind.Create, false, new ServerSequence(5), Human(), null)),
            ("object-mutation-completed-human-delete", ObjectProtocolMessageType.ObjectMutationCompleted, request,
                new ObjectMutationCompletion(request, ObjectMutationCompletionKind.Delete, false, new ServerSequence(7), null, HumanTombstone)),
            ("object-snapshot-completed-human", ObjectProtocolMessageType.ObjectSnapshotCompleted, request, new ObjectSnapshotCompletion(request, snapshot)),
        ];
    }

    /// <summary>The format-3 human-package manifest (synthetic blobs; no real appearance content).</summary>
    public static AppearanceBundleManifest HumanManifest()
    {
        static AppearanceBlobRef Blob(string text) => new(ContentHash.Of(Encoding.UTF8.GetBytes(text)), Encoding.UTF8.GetByteCount(text));
        var manifest = AppearanceBundleManifest.TryCreateHumanPackage(PlayerA, Blob("golden glamourer state"), Blob("golden manipulations"), null,
            [new AppearanceFileEntry("chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex", Blob("golden texture"))], null,
            new AppearanceHumanPackage(Blob("golden facts"), Blob("golden glamourer reference")), out var error);
        return manifest ?? throw new InvalidOperationException("The golden manifest is not valid: " + error);
    }

    /// <summary>Every valid fixture by its relative path (forward slashes).</summary>
    public static SortedDictionary<string, byte[]> Valid()
    {
        var codec = new ObjectControlMessageCodec();
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, type, messageId, payload) in Frames())
        {
            if (!codec.TryEncode(Envelope(type, messageId), payload, out var bytes, out var error))
                throw new InvalidOperationException($"The golden frame {name} does not encode ({error}).");
            files.Add($"valid/{name}.json", bytes);
        }
        files.Add("valid/manifest-format-3-human.json", HumanManifest().CanonicalBytes);
        return files;
    }

    /// <summary>Every malformed fixture by its relative path: a valid one with exactly one change, each of which must be refused.</summary>
    public static SortedDictionary<string, byte[]> Malformed()
    {
        var valid = Valid();
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        void Mutate(string name, string source, Func<JsonNode, bool> change)
        {
            var root = JsonNode.Parse(valid[$"valid/{source}.json"])!;
            if (!change(root))
                throw new InvalidOperationException($"The malformed fixture {name} changed nothing.");
            files.Add($"malformed/{name}.json", Encoding.UTF8.GetBytes(root.ToJsonString()));
        }
        Mutate("human-bundle-id-uppercase", "object-created-human", root => Set(root, "appearanceBundleId", BundleId.ToUpperInvariant()));
        Mutate("human-bundle-id-short", "object-created-human", root => Set(root, "appearanceBundleId", BundleId[..63]));
        Mutate("human-bundle-id-missing", "object-created-human", root => Remove(root, "appearanceBundleId"));
        Mutate("human-owner-missing", "object-created-human", root => Remove(root, "ownerId"));
        Mutate("human-yaw-out-of-range", "object-updated-human", root => Set(root, "yaw", 7.0));
        Mutate("human-unknown-member-scale", "object-updated-human", root => Add(root, "appearanceBundleId", "scale", new JsonObject { ["x"] = 1, ["y"] = 1, ["z"] = 1 }));
        Mutate("human-unknown-kind", "object-created-human", root => Set(root, "$kind", "humanoid"));
        Mutate("human-create-bundle-id-missing", "create-object-human", root => Remove(root, "appearanceBundleId"));
        Mutate("human-create-label-member", "create-object-human", root => Add(root, "appearanceBundleId", "label", Value("a label")));
        Mutate("human-transform-empty", "update-object-human-yaw-only", root => Set(root, "yaw", null));
        Mutate("human-transform-scale-member", "update-object-human-transform", root => Add(root, "yaw", "scale", Value(1.0)));
        Mutate("human-tombstone-unknown-kind", "object-deleted-human", root => Set(root, "kind", "Humanoid"));
        Mutate("envelope-protocol-6", "object-created-human", root => Set(root, "protocolVersion", 6));
        Mutate("envelope-protocol-8", "object-created-human", root => Set(root, "protocolVersion", 8));
        Mutate("scene-format-1-with-human", "scene-read-completed-format-2-human", root => Set(root, "formatVersion", 1));
        Mutate("scene-format-3", "scene-read-completed-format-2-human", root => Set(root, "formatVersion", 3));
        Mutate("mutation-completed-human-bundle-id-invalid", "object-mutation-completed-human-create", root => Set(root, "appearanceBundleId", "not-a-bundle"));
        var manifest = valid["valid/manifest-format-3-human.json"];
        files.Add("malformed/manifest-format-3-whitespace.json", Encoding.UTF8.GetBytes(" ").Concat(manifest).ToArray());
        void MutateManifest(string name, Func<JsonNode, bool> change)
        {
            var root = JsonNode.Parse(manifest)!;
            if (!change(root))
                throw new InvalidOperationException($"The malformed fixture {name} changed nothing.");
            files.Add($"malformed/{name}.json", Encoding.UTF8.GetBytes(root.ToJsonString()));
        }
        MutateManifest("manifest-format-3-human-missing", root => Remove(root, "human"));
        MutateManifest("manifest-format-4", root => Set(root, "formatVersion", 4));
        MutateManifest("manifest-format-3-facts-missing", root => Remove(root, "facts"));
        return files;
    }

    /// <summary><c>fixtures.sha256</c>: one line per file, <c>hash  path</c>, in ordinal path order.</summary>
    public static string Hashes(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        var builder = new StringBuilder();
        foreach (var (path, bytes) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
            builder.Append(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()).Append("  ").Append(path).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// The checked-in fixture folder, beside this source file. Found from the
    /// test output upwards (CI builds map source paths, so the caller file
    /// path is not a real path there).
    /// </summary>
    public static string Folder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RSyncStageCraft-Client.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        return Path.Combine(root, "tests", "RSyncStageCraft.Protocol.Tests", "Fixtures", "protocol-7");
    }

    private static JsonObject? Holder(JsonNode node, string member)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey(member))
                return obj;
            foreach (var (_, child) in obj)
            {
                if (child is not null && Holder(child, member) is { } found)
                    return found;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null && Holder(child, member) is { } found)
                    return found;
            }
        }
        return null;
    }

    private static bool Set(JsonNode root, string member, object? value)
    {
        if (Holder(root, member) is not { } holder)
            return false;
        holder[member] = Value(value);
        return true;
    }

    private static bool Remove(JsonNode root, string member) => Holder(root, member)?.Remove(member) == true;

    private static bool Add(JsonNode root, string beside, string member, JsonNode? value)
    {
        if (Holder(root, beside) is not { } holder || holder.ContainsKey(member))
            return false;
        holder[member] = value;
        return true;
    }

    private static JsonNode? Value(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        int number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        JsonNode node => node,
        _ => throw new ArgumentException("Unsupported fixture value.", nameof(value)),
    };
}
