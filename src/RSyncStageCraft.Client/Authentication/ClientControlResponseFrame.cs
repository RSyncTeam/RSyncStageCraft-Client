using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Authentication;

public enum ClientControlResponseStatus
{
    InactiveConnection, InactiveRequest, InvalidMessage, EnvelopeMismatch,
    WrongResponse, SceneMismatch, EventSequenceMismatch, EventLimitExceeded,
    BatchMismatch, EventObserved, Completed,
    ObjectMismatch, ObjectKindMismatch, RevisionMismatch,
}

/// <summary>
/// One caller-owned candidate command result, never a queue entry or applied
/// watermark. Observation carries no retained event payload. Completion may
/// return canonical scene read data but never imports or applies it.
/// </summary>
public readonly record struct ClientControlResponseResult(ClientControlResponseStatus Status,
    ClientControlRequestReceipt? Receipt = null, object? CommandResult = null)
{
    public bool Completed => Status == ClientControlResponseStatus.Completed;
}

/// <summary>
/// Structurally validated candidate frame. Private construction prevents an
/// unchecked typed message from bypassing preparation at the metadata owner.
/// Normalization must run outside authority locks before a fresh commit check.
/// </summary>
internal sealed class ClientControlResponseFrame
{
    private ClientControlResponseFrame(SessionMessageEnvelope envelope, object payload, ServerSequence? sequence = null)
    {
        Envelope = envelope;
        Payload = payload;
        Sequence = sequence;
    }

    internal SessionMessageEnvelope Envelope { get; }
    internal object Payload { get; }
    internal ServerSequence? Sequence { get; }

    internal static bool TryPrepare(ObjectControlMessage? message, out ClientControlResponseFrame? frame)
    {
        frame = null;
        if (message is null || !message.Envelope.IsValidForVersion(ProtocolConstants.CurrentVersion)) return false;
        var envelope = message.Envelope;
        switch (message.Payload)
        {
            case ObjectMutationCompletion mutation when envelope.MessageType == ObjectProtocolMessageType.ObjectMutationCompleted &&
                mutation.IsValid && mutation.RequestId == envelope.MessageId:
                frame = new(envelope, mutation);
                return true;
            case ObjectSnapshotCompletion snapshot when envelope.MessageType == ObjectProtocolMessageType.ObjectSnapshotCompleted &&
                snapshot.RequestId == envelope.MessageId:
                if (!snapshot.TryNormalize(out var normalizedSnapshot)) return false;
                frame = new(envelope, normalizedSnapshot);
                return true;
            case SceneSaveCompletion save when envelope.MessageType == ObjectProtocolMessageType.SceneSaveCompleted &&
                save.IsValid && save.RequestId == envelope.MessageId:
                frame = new(envelope, save);
                return true;
            case SceneReadCompletion read when envelope.MessageType == ObjectProtocolMessageType.SceneReadCompleted &&
                read.RequestId == envelope.MessageId:
                if (!read.TryNormalize(out var normalized)) return false;
                frame = new(envelope, normalized);
                return true;
            case ObjectEventBatchCompletion completed when envelope.MessageType == ObjectProtocolMessageType.ObjectEventBatchCompleted &&
                completed.IsValid && completed.RequestId == envelope.MessageId:
                frame = new(envelope, completed);
                return true;
            case ObjectRequestRejected rejected when envelope.MessageType == ObjectProtocolMessageType.Rejected &&
                rejected.IsValid && rejected.RequestId == envelope.MessageId:
                frame = new(envelope, rejected);
                return true;
            case ObjectCreatedMessage created when envelope.MessageType == ObjectProtocolMessageType.ObjectCreated &&
                IsClosedState(created.State) && created.IsValid:
                frame = new(envelope, created, created.Sequence);
                return true;
            case ObjectUpdatedMessage updated when envelope.MessageType == ObjectProtocolMessageType.ObjectUpdated &&
                IsClosedState(updated.State) && updated.IsValid:
                frame = new(envelope, updated, updated.Sequence);
                return true;
            case ObjectDeletedMessage deleted when envelope.MessageType == ObjectProtocolMessageType.ObjectDeleted && deleted.IsValid:
                frame = new(envelope, deleted, deleted.Sequence);
                return true;
            default:
                return false;
        }
    }

    private static bool IsClosedState(AuthoritativeObjectState? state) => state is ActorState or FurnitureState or LightState or HumanActorState;
}
