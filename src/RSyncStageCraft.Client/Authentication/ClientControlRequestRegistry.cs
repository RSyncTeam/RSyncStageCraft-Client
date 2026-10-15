using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Core.Authentication;

public readonly record struct ClientControlRequestLimits(int MaximumPending, int MaximumRetainedIds)
{
    public const int MaximumPendingHardLimit = 256;
    public const int MaximumRetainedIdsHardLimit = 16384;
    public static ClientControlRequestLimits Default => new(64, 4096);
    public bool IsValid => MaximumPending > 0 && MaximumPending <= MaximumPendingHardLimit &&
        MaximumRetainedIds >= MaximumPending && MaximumRetainedIds <= MaximumRetainedIdsHardLimit;
}

public enum ClientControlRequestKind
{
    CreateObject, UpdateObject, DeleteObject, Snapshot, Events, SaveScene, ReadScene,
}

public enum ClientControlRequestRegistrationStatus
{
    Registered, InvalidRequest, InactiveConnection, DuplicateRequest,
    PendingCapacityExceeded, RequestBudgetExhausted, RequestModeConflict,
}

public readonly record struct ClientObjectEventRequestMetadata(ServerSequence After, int MaximumEvents)
{
    public bool IsValid => After.IsValid && MaximumEvents > 0 &&
        MaximumEvents <= ProtocolLimits.MaximumAuthoritativeObjectEvents;
}

/// <summary>Scalar request identity only; no create or patch payload is retained.</summary>
public readonly record struct ClientObjectMutationRequestMetadata(
    RscObjectKind? ObjectKind, RscSceneId? SceneId, RscObjectId? ObjectId, ObjectRevision? BaseRevision)
{
    internal bool IsValidFor(ClientControlRequestKind kind) => kind switch
    {
        ClientControlRequestKind.CreateObject => ObjectKind is { } value && Enum.IsDefined(value) &&
            (SceneId is null || SceneId.Value.IsValid) && ObjectId is null && BaseRevision is null,
        ClientControlRequestKind.UpdateObject => ObjectKind is { } value && Enum.IsDefined(value) &&
            SceneId is null && ObjectId?.IsValid == true && BaseRevision?.IsValid == true,
        ClientControlRequestKind.DeleteObject => ObjectKind is null && SceneId is null &&
            ObjectId?.IsValid == true && BaseRevision?.IsValid == true,
        _ => false,
    };

    internal static ClientObjectMutationRequestMetadata? FromRequest(object request) => request switch
    {
        CreateObjectRequest create => new(create.Payload.Kind, create.Payload.SceneId, null, null),
        UpdateObjectRequest update => new(update.Patch.Kind, null, update.ObjectId, update.BaseRevision),
        DeleteObjectRequest delete => new(null, null, delete.ObjectId, delete.BaseRevision),
        _ => null,
    };
}

/// <summary>
/// Exact local request ownership. Carries only immutable correlation metadata,
/// never a payload, socket, runtime, credential, callback, or completion task.
/// Generation is local metadata and is not added to protocol-version-1 frames.
/// </summary>
public sealed class ClientControlRequestReceipt
{
    internal ClientControlRequestReceipt(Guid requestId, ClientControlRequestKind kind,
        RscSceneId? sceneId, ReceiverControlBinding binding, long generation,
        ClientObjectEventRequestMetadata? eventRequest = null,
        ClientObjectMutationRequestMetadata? mutationRequest = null)
    {
        RequestId = requestId;
        Kind = kind;
        SceneId = sceneId;
        LocalSession = binding.LocalSession;
        Remote = binding.Remote;
        Generation = generation;
        EventRequest = eventRequest;
        MutationRequest = mutationRequest;
    }

    public Guid RequestId { get; }
    public ClientControlRequestKind Kind { get; }
    public RscSceneId? SceneId { get; }
    public ReceiverSessionKey LocalSession { get; }
    public ReceiverRemoteConnection Remote { get; }
    public long Generation { get; }
    public ClientObjectEventRequestMetadata? EventRequest { get; }
    public ClientObjectMutationRequestMetadata? MutationRequest { get; }
}

public readonly record struct ClientControlRequestSnapshot(
    int PendingRequests, int RetainedRequestIds, bool Retired);

internal readonly record struct ClientControlRequestRegistration(
    ClientControlRequestRegistrationStatus Status, ClientControlRequestReceipt? Receipt = null);

/// <summary>
/// Owned by one established authentication entry. Request IDs are single-use
/// within that exact binding, including after completion or local abandonment:
/// version 1 replies carry no local generation that could distinguish reused
/// IDs. A finite lifetime budget requires a fresh connection when exhausted.
/// All work is bounded metadata; no timer, provider, or callback runs here.
/// </summary>
internal sealed class ClientControlRequestRegistry
{
    private readonly object gate = new();
    private readonly ReceiverControlBinding binding;
    private readonly ClientControlRequestLimits limits;
    private readonly Dictionary<Guid, PendingRequest> pending = new();
    private readonly HashSet<Guid> identifiers = new();
    private long generation;
    private bool retired;

    internal ClientControlRequestRegistry(ReceiverControlBinding binding, ClientControlRequestLimits limits)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!limits.IsValid) throw new ArgumentOutOfRangeException(nameof(limits));
        this.binding = binding;
        this.limits = limits;
    }

    internal ClientControlRequestSnapshot Snapshot
    {
        get { lock (gate) return new(pending.Count, identifiers.Count, retired); }
    }

    internal ClientControlRequestRegistration Register(Guid requestId,
        ClientControlRequestKind kind, RscSceneId? sceneId,
        ClientObjectEventRequestMetadata? eventRequest = null,
        ClientObjectMutationRequestMetadata? mutationRequest = null)
    {
        lock (gate)
        {
            if (retired) return new(ClientControlRequestRegistrationStatus.InactiveConnection);
            if (requestId == Guid.Empty || !Enum.IsDefined(kind) ||
                (kind is ClientControlRequestKind.SaveScene or ClientControlRequestKind.ReadScene
                    ? sceneId?.IsValid != true : sceneId is not null) ||
                (kind == ClientControlRequestKind.Events ? eventRequest?.IsValid != true : eventRequest is not null) ||
                (kind is ClientControlRequestKind.CreateObject or ClientControlRequestKind.UpdateObject or ClientControlRequestKind.DeleteObject
                    ? mutationRequest?.IsValidFor(kind) != true : mutationRequest is not null))
                return new(ClientControlRequestRegistrationStatus.InvalidRequest);
            if (identifiers.Contains(requestId))
                return new(ClientControlRequestRegistrationStatus.DuplicateRequest);
            if (pending.Count >= limits.MaximumPending)
                return new(ClientControlRequestRegistrationStatus.PendingCapacityExceeded);
            if (identifiers.Count >= limits.MaximumRetainedIds)
                return new(ClientControlRequestRegistrationStatus.RequestBudgetExhausted);
            var receipt = new ClientControlRequestReceipt(requestId, kind, sceneId, binding, ++generation, eventRequest, mutationRequest);
            identifiers.Add(requestId);
            pending.Add(requestId, new(receipt));
            return new(ClientControlRequestRegistrationStatus.Registered, receipt);
        }
    }

    internal bool TryFind(ReceiverControlBinding source, Guid requestId,
        out ClientControlRequestReceipt? receipt)
    {
        lock (gate)
        {
            receipt = null;
            if (retired || !ReferenceEquals(binding, source) || !pending.TryGetValue(requestId, out var retained))
                return false;
            receipt = retained.Receipt;
            return true;
        }
    }

    internal bool Complete(ReceiverControlBinding source, ClientControlRequestReceipt receipt)
    {
        lock (gate)
            return !retired && ReferenceEquals(binding, source) && RemoveExact(receipt);
    }

    internal bool Abandon(ClientControlRequestReceipt receipt)
    {
        lock (gate)
            return !retired && RemoveExact(receipt);
    }

    private bool RemoveExact(ClientControlRequestReceipt receipt) =>
        pending.TryGetValue(receipt.RequestId, out var retained) &&
        ReferenceEquals(retained.Receipt, receipt) && pending.Remove(receipt.RequestId);

    internal bool Owns(ReceiverControlBinding source, ClientControlRequestReceipt receipt)
    {
        lock (gate)
            return !retired && ReferenceEquals(binding, source) &&
                pending.TryGetValue(receipt.RequestId, out var retained) && ReferenceEquals(retained.Receipt, receipt);
    }

    // Candidate-only typed boundary. Normalization is deliberately outside the
    // metadata lock: caller-supplied scene collections can synchronously reenter.
    internal ClientControlResponseResult ReceiveCandidate(ReceiverControlBinding source,
        ClientControlRequestReceipt receipt, ObjectControlMessage? message)
    {
        if (!Owns(source, receipt)) return new(ClientControlResponseStatus.InactiveRequest);
        if (!ClientControlResponseFrame.TryPrepare(message, out var frame))
            return RefuseInvalidCandidate(source, receipt, message?.Envelope);
        return ReceivePrepared(source, receipt, frame!);
    }

    internal ClientControlResponseResult RefuseInvalidCandidate(ReceiverControlBinding source,
        ClientControlRequestReceipt receipt, SessionMessageEnvelope? envelope)
    {
        lock (gate)
        {
            if (retired || !ReferenceEquals(binding, source)) return new(ClientControlResponseStatus.InactiveConnection);
            if (!pending.TryGetValue(receipt.RequestId, out var retained) || !ReferenceEquals(retained.Receipt, receipt))
                return new(ClientControlResponseStatus.InactiveRequest);
            if (receipt.Kind == ClientControlRequestKind.Events && envelope is { } header &&
                header.ProtocolVersion == ProtocolConstants.CurrentVersion && EnvelopeMatches(header, receipt))
                retained.BatchFailed = true;
            return new(ClientControlResponseStatus.InvalidMessage);
        }
    }

    private bool EnvelopeMatches(SessionMessageEnvelope envelope, ClientControlRequestReceipt receipt) =>
        envelope.SessionId == binding.Remote.SessionId && envelope.SessionGeneration == binding.Remote.SessionGeneration &&
        envelope.ConnectionEpoch == binding.Remote.ConnectionEpoch && envelope.MessageId == receipt.RequestId;

    internal ClientControlResponseResult ReceivePrepared(ReceiverControlBinding source,
        ClientControlRequestReceipt receipt, ClientControlResponseFrame frame)
    {
        lock (gate)
        {
            if (retired || !ReferenceEquals(binding, source))
                return new(ClientControlResponseStatus.InactiveConnection);
            if (!pending.TryGetValue(receipt.RequestId, out var retained) || !ReferenceEquals(retained.Receipt, receipt))
                return new(ClientControlResponseStatus.InactiveRequest);
            var envelope = frame.Envelope;
            if (!EnvelopeMatches(envelope, receipt))
                return new(ClientControlResponseStatus.EnvelopeMismatch);
            if (retained.BatchFailed) return new(ClientControlResponseStatus.BatchMismatch);
            if (receipt.Kind == ClientControlRequestKind.Events && frame.Sequence is null &&
                frame.Payload is not (ObjectEventBatchCompletion or ObjectRequestRejected))
            {
                retained.BatchFailed = true;
                return new(ClientControlResponseStatus.WrongResponse);
            }

            if (frame.Sequence is { } sequence)
            {
                if (receipt.Kind != ClientControlRequestKind.Events || receipt.EventRequest is not { } request)
                    return new(ClientControlResponseStatus.WrongResponse);
                if (retained.ObservedEvents >= request.MaximumEvents)
                {
                    retained.BatchFailed = true;
                    return new(ClientControlResponseStatus.EventLimitExceeded);
                }
                if (retained.DeliveredThrough.Value == long.MaxValue || sequence.Value != retained.DeliveredThrough.Value + 1)
                {
                    retained.BatchFailed = true;
                    return new(ClientControlResponseStatus.EventSequenceMismatch);
                }
                retained.ObservedEvents++;
                retained.DeliveredThrough = sequence;
                return new(ClientControlResponseStatus.EventObserved, receipt);
            }

            switch (frame.Payload)
            {
                case ObjectMutationCompletion mutation:
                    var match = MatchMutation(receipt, mutation);
                    if (match != ClientControlResponseStatus.Completed) return new(match);
                    break;
                case ObjectSnapshotCompletion:
                    if (receipt.Kind != ClientControlRequestKind.Snapshot) return new(ClientControlResponseStatus.WrongResponse);
                    break;
                case SceneSaveCompletion save:
                    if (receipt.Kind != ClientControlRequestKind.SaveScene) return new(ClientControlResponseStatus.WrongResponse);
                    if (receipt.SceneId != save.SceneId) return new(ClientControlResponseStatus.SceneMismatch);
                    break;
                case SceneReadCompletion read:
                    if (receipt.Kind != ClientControlRequestKind.ReadScene) return new(ClientControlResponseStatus.WrongResponse);
                    if (receipt.SceneId != read.SceneId) return new(ClientControlResponseStatus.SceneMismatch);
                    break;
                case ObjectEventBatchCompletion completed:
                    if (receipt.Kind != ClientControlRequestKind.Events || receipt.EventRequest is not { } request)
                        return new(ClientControlResponseStatus.WrongResponse);
                    if (completed.RequestedAfter != request.After || completed.EventCount > request.MaximumEvents ||
                        completed.EventCount != retained.ObservedEvents || completed.DeliveredThrough != retained.DeliveredThrough)
                    {
                        retained.BatchFailed = true;
                        return new(ClientControlResponseStatus.BatchMismatch);
                    }
                    break;
                case ObjectRequestRejected:
                    if (retained.ObservedEvents != 0)
                    {
                        retained.BatchFailed = true;
                        return new(ClientControlResponseStatus.BatchMismatch);
                    }
                    break;
                default:
                    return new(ClientControlResponseStatus.WrongResponse);
            }
            pending.Remove(receipt.RequestId);
            return new(ClientControlResponseStatus.Completed, receipt, frame.Payload);
        }
    }

    private static ClientControlResponseStatus MatchMutation(ClientControlRequestReceipt receipt,
        ObjectMutationCompletion mutation)
    {
        var kind = receipt.Kind switch
        {
            ClientControlRequestKind.CreateObject => ObjectMutationCompletionKind.Create,
            ClientControlRequestKind.UpdateObject => ObjectMutationCompletionKind.Update,
            ClientControlRequestKind.DeleteObject => ObjectMutationCompletionKind.Delete,
            _ => (ObjectMutationCompletionKind)(-1),
        };
        if (kind != mutation.Kind || receipt.MutationRequest is not { } request)
            return ClientControlResponseStatus.WrongResponse;
        if (request.ObjectKind is { } objectKind && mutation.State?.Kind != objectKind)
            return ClientControlResponseStatus.ObjectKindMismatch;
        if (kind == ObjectMutationCompletionKind.Create)
            return request.SceneId == mutation.State!.SceneId
                ? ClientControlResponseStatus.Completed : ClientControlResponseStatus.SceneMismatch;
        var objectId = mutation.State?.ObjectId ?? mutation.Tombstone!.Value.ObjectId;
        if (request.ObjectId != objectId) return ClientControlResponseStatus.ObjectMismatch;
        var revision = mutation.State?.Revision ?? mutation.Tombstone!.Value.DeletionRevision;
        return request.BaseRevision is { } basis && basis.Value != long.MaxValue && revision.Value == basis.Value + 1
            ? ClientControlResponseStatus.Completed : ClientControlResponseStatus.RevisionMismatch;
    }

    private sealed class PendingRequest(ClientControlRequestReceipt receipt)
    {
        internal ClientControlRequestReceipt Receipt { get; } = receipt;
        internal int ObservedEvents { get; set; }
        internal bool BatchFailed { get; set; }
        internal ServerSequence DeliveredThrough { get; set; } = receipt.EventRequest?.After ?? new(0);
    }

    internal void Retire()
    {
        lock (gate)
        {
            retired = true;
            pending.Clear();
            identifiers.Clear();
        }
    }
}
