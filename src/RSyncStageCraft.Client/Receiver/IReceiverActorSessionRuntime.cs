namespace RSyncStageCraft.Core.Receiver;

/// <summary>
/// Session lifecycle surface used by the connection boundary. Implementations
/// must expose only a bounded, side-effect-free exact-session activity check
/// and return <see langword="false"/> from <see cref="TryActivate"/> only when
/// they retained no admission or ownership for that session. A throwing
/// activation is treated as potentially partial and is disconnected by the
/// lifecycle. Implementations
/// must synchronously revoke admission and cancel session work before
/// <see cref="DisconnectAsync"/> returns its task.
/// </summary>
internal interface IReceiverActorSessionRuntime
{
    bool IsActive(ReceiverSessionKey session);

    bool TryActivate(ReceiverSessionKey session);

    Task<ReceiverApplyCleanupResult> DisconnectAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken = default);

    Task<ReceiverApplyCleanupResult> RetryCleanupAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken = default);
}
