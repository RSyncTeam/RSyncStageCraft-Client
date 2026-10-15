using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Connection;

public enum ServerCommandStatus
{
    Completed,
    Rejected,
    /// <summary>Not signed in, or the connection ended before a reply.</summary>
    NotConnected,
    /// <summary>Too many commands are already waiting.</summary>
    Busy,
    Invalid,
    Cancelled,
}

/// <summary>Result of one create, update or delete sent to the server.</summary>
public sealed record ServerCommandResult(
    ServerCommandStatus Status,
    ObjectMutationCompletion? Completion = null,
    ProtocolRejectReason? RejectReason = null,
    string? Detail = null)
{
    public bool Completed => Status == ServerCommandStatus.Completed && Completion is not null;

    /// <summary>Refused for an unknown reason, and the fallback for any undefined status.</summary>
    public const string RefusedByServer = "Refused by the server.";

    /// <summary>The command task itself failed, so nothing is known about the change.</summary>
    public const string NotSentText = "The change could not be sent; check the connection and try again.";

    /// <summary>
    /// What the player sees. Every
    /// rejection reason has its own text; the five texts that existed before
    /// are kept word for word. No enum name is ever shown.
    /// </summary>
    public string Describe() => Status switch
    {
        ServerCommandStatus.Completed => "Done.",
        ServerCommandStatus.Rejected => DescribeRejection(RejectReason),
        ServerCommandStatus.NotConnected => "Not connected to the server.",
        ServerCommandStatus.Busy => "Still sending earlier changes; try again.",
        // The editor's own reason when it gave one (FurnitureEditor details).
        ServerCommandStatus.Invalid => string.IsNullOrWhiteSpace(Detail) ? "The change is not valid." : Detail,
        ServerCommandStatus.Cancelled => "Cancelled.",
        _ => RefusedByServer,
    };

    public static string DescribeRejection(ProtocolRejectReason? reason) => reason switch
    {
        ProtocolRejectReason.LocationMismatch => "Refused: this scene belongs to another place.",
        ProtocolRejectReason.LocationRequired => "Refused: the scene has no location.",
        ProtocolRejectReason.InvalidModelIdentity => "Refused: not a valid furniture item or light.",
        ProtocolRejectReason.NotOwner => "Refused: only its owner can change it.",
        ProtocolRejectReason.CapacityExceeded => "Refused: the scene is full (actor or object limit reached).",
        ProtocolRejectReason.StaleRevision or ProtocolRejectReason.RequestConflict =>
            "Refused: someone changed it first. The current state is shown; try again.",
        ProtocolRejectReason.NotReady or ProtocolRejectReason.SyncNotReady =>
            "Refused: the scene is still loading. Try again in a moment.",
        ProtocolRejectReason.InvalidSession or ProtocolRejectReason.NotAuthenticated =>
            "Refused: you are not signed in. Press Reconnect.",
        ProtocolRejectReason.UnknownObject or ProtocolRejectReason.Deleted => "Refused: it no longer exists.",
        ProtocolRejectReason.WrongObjectKind => "Refused: that does not apply to this kind of object.",
        ProtocolRejectReason.MessageTooLarge => "Refused: the change is too large.",
        ProtocolRejectReason.InvalidPatch => "Refused: the change is not valid.",
        ProtocolRejectReason.NotAuthorized => "Refused: the server does not allow that scene action.",
        ProtocolRejectReason.UnsupportedFeature =>
            "Refused: this server does not support that. Ask the host to update the server.",
        ProtocolRejectReason.IncompatibleProtocol or ProtocolRejectReason.ProtocolMismatch =>
            "Refused: the plugin and server versions do not match. Update the plugin, or ask the host to update the server.",
        _ => RefusedByServer,
    };
}

/// <summary>A queued command and the caller waiting for it.</summary>
internal sealed class PendingServerCommand(object request, Guid requestId)
{
    internal object Request { get; } = request;
    internal Guid RequestId { get; } = requestId;
    internal TaskCompletionSource<ServerCommandResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public static class ServerCommandRequests
{
    /// <summary>The request ID the server echoes in its reply, or empty for an unsupported or invalid request.</summary>
    public static Guid RequestIdOf(object request) => request switch
    {
        CreateObjectRequest create when create.IsValid => create.ClientRequestId,
        UpdateObjectRequest update when update.IsValid => update.RequestId,
        DeleteObjectRequest delete when delete.IsValid => delete.RequestId,
        _ => Guid.Empty,
    };
}
