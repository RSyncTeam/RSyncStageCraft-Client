using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Receiver;

/// <summary>
/// Bounded, non-authoritative diagnostics for receiver adapter failures.
/// Reading failure text must never escape a lifecycle or ownership boundary.
/// </summary>
internal static class ReceiverFailureDiagnostic
{
    internal const string Fallback = "Receiver operation failed.";

    internal static string From(Exception exception)
    {
        string? message;
        try
        {
            message = exception.Message;
        }
        catch
        {
            return Fallback;
        }

        if (string.IsNullOrEmpty(message))
            return Fallback;

        return message.Length <= ProtocolLimits.MaximumIdentifierLength
            ? message
            : message[..ProtocolLimits.MaximumIdentifierLength];
    }
}
