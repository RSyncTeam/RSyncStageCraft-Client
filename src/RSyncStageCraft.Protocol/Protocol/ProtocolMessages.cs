namespace RSyncStageCraft.Core.Protocol;

public readonly record struct HelloMessage(int ProtocolVersion);

public readonly record struct ReadinessMessage(
    bool PenumbraReady,
    bool GlamourerReady,
    bool IpcReady);

/// <param name="UserId">
/// Protocol 6: the signed-in user's stable RSC identity (<see cref="RscUserId"/>),
/// the only thing a client compares with an actor's owner to decide whether it
/// may offer editing (the server still enforces owner-only editing). The
/// server always sends it; null only where a caller has no identity to report.
/// </param>
/// <param name="DisplayName">
/// Protocol 6: that account's optional display name (<see cref="RscDisplayName"/>),
/// presentation only.
/// </param>
public readonly record struct AuthorizeMessage(
    long SessionId,
    long Generation,
    long ConnectionEpoch,
    string? UserId = null,
    string? DisplayName = null);

public enum ProtocolRejectReason
{
    None,
    IncompatibleProtocol,
    NotReady,
    InvalidSession,
    StaleRevision,
    CapacityExceeded,
    MessageTooLarge,
    NotAuthenticated,
    SyncNotReady,
    UnknownObject,
    WrongObjectKind,
    NotOwner,
    InvalidPatch,
    Deleted,
    ProtocolMismatch,
    UnsupportedFeature,
    RequestConflict,
    NotAuthorized,
    /// <summary>A create without a complete scene location, or into an existing unlocated scene.</summary>
    LocationRequired,
    /// <summary>A create whose location differs from its scene's established location.</summary>
    LocationMismatch,
    /// <summary>A model identity that is not in its canonical form (for example furniture:indoor:&lt;item&gt;).</summary>
    InvalidModelIdentity,
}
