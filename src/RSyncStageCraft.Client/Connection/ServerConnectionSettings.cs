using RSyncStageCraft.Core.Authentication;

namespace RSyncStageCraft.Core.Connection;

/// <summary>
/// What the connection service needs to sign in. The access key is a secret:
/// <see cref="ToString"/> never includes it, and nothing here logs it.
/// </summary>
public sealed class ServerConnectionSettings
{
    /// <summary>A new install has no server address; the player enters the one they use.</summary>
    public const string DefaultEndpoint = "";

    public ServerConnectionSettings(string? endpoint, string? accessKey, bool enabled)
    {
        Endpoint = endpoint?.Trim() ?? string.Empty;
        AccessKey = accessKey?.Trim() ?? string.Empty;
        Enabled = enabled;
    }

    public string Endpoint { get; }
    public string AccessKey { get; }

    /// <summary>The user asked to be connected.</summary>
    public bool Enabled { get; }

    public bool HasAccessKey => AccessKey.Length != 0;

    /// <summary>
    /// The endpoint must be wss://. Plain ws:// is accepted only for a
    /// loopback address (local testing), so a key never crosses a network
    /// unencrypted.
    /// </summary>
    public static bool TryParseEndpoint(string? endpoint, out Uri? uri, out string problem)
    {
        uri = null;
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var parsed))
        {
            problem = "Enter the server address (wss://...).";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            problem = "The server address must not contain a user name or fragment.";
            return false;
        }
        if (parsed.Scheme == "wss" || (parsed.Scheme == "ws" && parsed.IsLoopback))
        {
            uri = parsed;
            return true;
        }
        problem = "The server address must start with wss://.";
        return false;
    }

    /// <summary>Format check only; the server decides whether the key is valid.</summary>
    public bool HasWellFormedAccessKey =>
        HasAccessKey && AuthenticationMessageCodec.IsCanonicalCredential(AccessKey.AsSpan());

    public override string ToString() =>
        $"Endpoint={Endpoint}, AccessKey={(HasAccessKey ? "(set)" : "(not set)")}, Enabled={Enabled}";
}
