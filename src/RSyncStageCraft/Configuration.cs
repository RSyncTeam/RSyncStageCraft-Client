using Dalamud.Configuration;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft;

/// <summary>
/// Plugin settings saved by Dalamud as plain JSON. The access key is NOT
/// stored here; it lives in a DPAPI-protected file (ProtectedAccessKeyStore).
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int ProtocolVersion { get; set; } = ProtocolConstants.CurrentVersion;

    /// <summary>The RSC server's WebSocket address (wss://...).</summary>
    public string ServerEndpoint { get; set; } = ServerConnectionSettings.DefaultEndpoint;

    /// <summary>Connect automatically when the plugin loads.</summary>
    public bool ConnectEnabled { get; set; }
}
