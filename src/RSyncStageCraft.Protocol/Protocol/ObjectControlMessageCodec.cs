using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RSyncStageCraft.Core.Scenes;

namespace RSyncStageCraft.Core.Protocol;

public enum ObjectControlCodecError
{
    None,
    MessageTooLarge,
    InvalidJson,
    InvalidEnvelope,
    InvalidPayload,
}

public sealed record ObjectControlMessage(SessionMessageEnvelope Envelope, object Payload);

/// <summary>
/// Closed, bounded UTF-8 JSON framing for existing RSC object contracts.
/// Decoding validates structure, not credentials, connection authority, or
/// readiness. Hosts must still route through the exact-session gateways.
/// No stream, queue, connection, or decoded message is retained by this codec.
/// There is one protocol version (<see cref="ProtocolConstants.CurrentVersion"/>);
/// correlated responses must carry their request ID as the envelope message ID.
/// </summary>
public sealed class ObjectControlMessageCodec
{
    private const int MaximumDepth = 32;
    private const int MaximumPropertiesPerObject = 32;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly int maximumBytes;
    private readonly int protocolVersion;

    public ObjectControlMessageCodec(int maximumBytes = ProtocolLimits.MaximumControlMessageBytes,
        int protocolVersion = ProtocolConstants.CurrentVersion)
    {
        if (maximumBytes <= 0 || maximumBytes > ProtocolLimits.MaximumControlMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (protocolVersion != ProtocolConstants.CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        this.maximumBytes = maximumBytes;
        this.protocolVersion = protocolVersion;
    }

    public bool TryEncode(SessionMessageEnvelope envelope, object payload,
        out byte[] utf8Json, out ObjectControlCodecError error)
    {
        utf8Json = Array.Empty<byte>();
        error = ObjectControlCodecError.InvalidEnvelope;
        if (!envelope.IsValidForVersion(protocolVersion))
            return false;
        error = ObjectControlCodecError.InvalidPayload;
        if (PayloadType(envelope.MessageType) is not { } payloadType)
            return false;
        if (payload is null ||
            payloadType != payload.GetType() ||
            !TryNormalizePayload(payload, out var normalizedPayload) ||
            !CorrelationMatches(envelope, normalizedPayload))
        {
            return false;
        }

        try
        {
            using var stream = new BoundedWriteStream(maximumBytes);
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = MaximumDepth }))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("envelope");
                JsonSerializer.Serialize(writer, envelope, JsonOptions);
                writer.WritePropertyName("payload");
                JsonSerializer.Serialize(writer, normalizedPayload, payloadType, JsonOptions);
                writer.WriteEndObject();
                writer.Flush();
            }
            utf8Json = stream.ToArray();
            error = ObjectControlCodecError.None;
            return true;
        }
        catch (MessageLimitException)
        {
            error = ObjectControlCodecError.MessageTooLarge;
            return false;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    public bool TryDecode(ReadOnlyMemory<byte> utf8Json,
        out ObjectControlMessage? message, out ObjectControlCodecError error)
    {
        message = null;
        error = ObjectControlCodecError.MessageTooLarge;
        if (utf8Json.Length > maximumBytes)
            return false;

        error = ObjectControlCodecError.InvalidJson;
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                MaxDepth = MaximumDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = document.RootElement;
            if (!ValidateShape(root) || root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("envelope", out var envelopeJson) ||
                !root.TryGetProperty("payload", out var payloadJson))
            {
                return false;
            }

            error = ObjectControlCodecError.InvalidEnvelope;
            var envelope = envelopeJson.Deserialize<SessionMessageEnvelope>(JsonOptions);
            if (!envelope.IsValidForVersion(protocolVersion))
                return false;

            error = ObjectControlCodecError.InvalidPayload;
            if (PayloadType(envelope.MessageType) is not { } payloadType)
                return false;
            var payload = payloadJson.Deserialize(payloadType, JsonOptions);
            if (payload is null || !TryNormalizePayload(payload, out var normalizedPayload) ||
                !CorrelationMatches(envelope, normalizedPayload))
                return false;

            message = new(envelope, normalizedPayload);
            error = ObjectControlCodecError.None;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ValidateShape(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                string name;
                try
                {
                    name = property.Name;
                }
                catch (InvalidOperationException)
                {
                    // JsonDocument defers name transcoding. Malformed UTF-8
                    // or an unpaired escaped surrogate is invalid input, not
                    // a caller failure; typed deserialization has not run yet.
                    return false;
                }
                if (names.Count >= MaximumPropertiesPerObject ||
                    name.Length > 64 || !names.Add(name) ||
                    !ValidateShape(property.Value))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > ProtocolLimits.MaximumObjectsPerSnapshot)
                return false;
            foreach (var item in element.EnumerateArray())
                if (!ValidateShape(item))
                    return false;
        }
        return true;
    }

    private static bool CorrelationMatches(SessionMessageEnvelope envelope, object payload) =>
        payload switch
        {
            ObjectEventBatchCompletion value => value.RequestId == envelope.MessageId,
            SceneSaveCompletion value => value.RequestId == envelope.MessageId,
            SceneReadCompletion value => value.RequestId == envelope.MessageId,
            ObjectMutationCompletion value => value.RequestId == envelope.MessageId,
            ObjectSnapshotCompletion value => value.RequestId == envelope.MessageId,
            ObjectRequestRejected value => value.RequestId == envelope.MessageId,
            _ => true,
        };

    /// <summary>The payload type of a message type with a protocol 7 wire route, or null (retired or unknown: refused).</summary>
    private static Type? PayloadType(ObjectProtocolMessageType type) => type switch
    {
        ObjectProtocolMessageType.CreateObject => typeof(CreateObjectRequest),
        ObjectProtocolMessageType.UpdateObject => typeof(UpdateObjectRequest),
        ObjectProtocolMessageType.DeleteObject => typeof(DeleteObjectRequest),
        ObjectProtocolMessageType.ObjectSnapshot => typeof(ObjectSnapshotMessage),
        ObjectProtocolMessageType.ObjectCreated => typeof(ObjectCreatedMessage),
        ObjectProtocolMessageType.ObjectUpdated => typeof(ObjectUpdatedMessage),
        ObjectProtocolMessageType.ObjectDeleted => typeof(ObjectDeletedMessage),
        ObjectProtocolMessageType.AcknowledgeObjects => typeof(ObjectStreamAcknowledgement),
        ObjectProtocolMessageType.Rejected => typeof(ObjectRequestRejected),
        ObjectProtocolMessageType.RequestObjectSnapshot => typeof(ObjectSnapshotRequest),
        ObjectProtocolMessageType.RequestObjectEvents => typeof(ObjectEventBatchRequest),
        ObjectProtocolMessageType.SaveScene => typeof(SaveSceneRequest),
        ObjectProtocolMessageType.ReadScene => typeof(ReadSceneRequest),
        ObjectProtocolMessageType.ObjectEventBatchCompleted => typeof(ObjectEventBatchCompletion),
        ObjectProtocolMessageType.SceneSaveCompleted => typeof(SceneSaveCompletion),
        ObjectProtocolMessageType.SceneReadCompleted => typeof(SceneReadCompletion),
        ObjectProtocolMessageType.ObjectMutationCompleted => typeof(ObjectMutationCompletion),
        ObjectProtocolMessageType.ObjectSnapshotCompleted => typeof(ObjectSnapshotCompletion),
        _ => null,
    };

    private static bool PayloadIsValid(object payload) => payload switch
    {
        CreateObjectRequest value => value.IsValid,
        UpdateObjectRequest value => value.IsValid,
        DeleteObjectRequest value => value.IsValid,
        ObjectSnapshotMessage value => value.IsValid,
        ObjectCreatedMessage value => value.IsValid,
        ObjectUpdatedMessage value => value.IsValid,
        ObjectDeletedMessage value => value.IsValid,
        ObjectStreamAcknowledgement value => value.IsValid,
        ObjectRequestRejected value => value.IsValid,
        ObjectSnapshotRequest value => value.IsValid,
        ObjectEventBatchRequest value => value.IsValid,
        SaveSceneRequest value => value.IsValid,
        ReadSceneRequest value => value.IsValid,
        ObjectEventBatchCompletion value => value.IsValid,
        SceneSaveCompletion value => value.IsValid,
        ObjectMutationCompletion value => value.IsValid,
        _ => false,
    };

    private static bool TryNormalizePayload(object payload, out object normalized)
    {
        if (payload is ObjectSnapshotCompletion completed)
        {
            var accepted = completed.TryNormalize(out var normalizedCompletion);
            normalized = normalizedCompletion;
            return accepted;
        }
        if (payload is SceneReadCompletion scene)
        {
            var accepted = scene.TryNormalize(out var normalizedScene);
            normalized = normalizedScene;
            return accepted;
        }
        if (payload is ObjectSnapshotMessage snapshot)
        {
            var accepted = snapshot.TryNormalize(out var normalizedSnapshot);
            normalized = normalizedSnapshot;
            return accepted;
        }

        normalized = payload;
        return PayloadIsValid(payload);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object ||
                (info.Type.Namespace != typeof(SessionMessageEnvelope).Namespace &&
                    info.Type.Namespace != typeof(SceneDocument).Namespace))
                return;
            for (var index = info.Properties.Count - 1; index >= 0; index--)
            {
                var property = info.Properties[index];
                if (property.Set is null)
                    info.Properties.RemoveAt(index); // Never transmit computed validation/projection properties.
                else
                    property.IsRequired = true; // Missing false/zero/null fields are not implicit wire defaults.
            }

            if (info.Type == typeof(AuthoritativeObjectState))
                info.PolymorphismOptions = Union(
                    new(typeof(ActorState), "actor"), new(typeof(FurnitureState), "furniture"), new(typeof(LightState), "light"),
                    new(typeof(HumanActorState), "human"));
            else if (info.Type == typeof(ObjectCreatePayload))
                info.PolymorphismOptions = Union(
                    new(typeof(ActorCreatePayload), "actor"), new(typeof(FurnitureCreatePayload), "furniture"), new(typeof(LightCreatePayload), "light"),
                    new(typeof(HumanActorCreatePayload), "human"));
            else if (info.Type == typeof(ObjectPatch))
                info.PolymorphismOptions = Union(
                    new(typeof(ActorTransformPatch), "actorTransform"),
                    new(typeof(ActorControlPatch), "actorControl"), new(typeof(FurnitureTransformPatch), "furnitureTransform"),
                    new(typeof(FurnitureStainPatch), "furnitureStain"),
                    new(typeof(LightPointPatch), "lightPoint"),
                    new(typeof(HumanTransformPatch), "humanTransform"));
        });
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            MaxDepth = MaximumDepth,
            TypeInfoResolver = resolver,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static JsonPolymorphismOptions Union(params JsonDerivedType[] types)
    {
        var union = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "$kind",
            IgnoreUnrecognizedTypeDiscriminators = false,
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
        };
        foreach (var type in types)
            union.DerivedTypes.Add(type);
        return union;
    }

    private sealed class MessageLimitException : IOException;

    private sealed class BoundedWriteStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLimit(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLimit(buffer.Length);
            base.Write(buffer);
        }

        private void CheckLimit(int count)
        {
            if (Length + count > maximumBytes)
                throw new MessageLimitException();
        }
    }
}
