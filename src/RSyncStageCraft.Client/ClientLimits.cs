namespace RSyncStageCraft.Core;

/// <summary>Client-side safety limits; the wire limits are in <see cref="Protocol.ProtocolLimits"/>.</summary>
public static class ClientLimits
{
    public const int DefaultDependencyAdapters = 8;
    public const int MaximumDependencyAdapters = 16;
    public const int MaximumDependencyFailureReasonLength = 512;
    public const int MaximumDesiredStateFingerprintLength = 128;
    public const int DefaultReceiverLifecycleOperations = 16;
    public const int MaximumReceiverLifecycleOperations = 64;
}
