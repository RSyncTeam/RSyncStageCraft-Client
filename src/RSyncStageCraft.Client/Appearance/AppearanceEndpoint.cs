using System.Net;

namespace RSyncStageCraft.Core.Appearance;

/// <summary>
/// The appearance routes' base address is
/// derived structurally from the configured control endpoint; there is no
/// separate setting. When it cannot be derived, appearance transfer is
/// disabled and no request (and so no key) is sent.
/// <list type="bullet">
/// <item><c>wss</c> maps to <c>https</c>; <c>ws</c> maps to <c>http</c> only
/// for an IP loopback address or <c>localhost</c>.</item>
/// <item>The path must be exactly <c>/rsc/v1/control</c>, with at most one
/// trailing slash; only that suffix is replaced by <c>/rsc/v1/appearance</c>.</item>
/// <item>User information, a query and a fragment are refused (also empty ones).</item>
/// </list>
/// </summary>
public static class AppearanceEndpoint
{
    public const string ControlPath = "/rsc/v1/control";
    public const string AppearancePath = "/rsc/v1/appearance";

    /// <summary>
    /// The appearance base address (ending in <c>/rsc/v1/appearance/</c>, so
    /// route paths resolve relative to it), or null when the control endpoint
    /// does not have the canonical structure.
    /// </summary>
    public static Uri? TryDerive(string? controlEndpoint)
    {
        if (string.IsNullOrEmpty(controlEndpoint))
            return null;
        var text = controlEndpoint.Trim();
        // Characters that would make the address mean something other than
        // what it reads as: user information, a query, a fragment, escapes,
        // backslashes, whitespace or control characters.
        foreach (var character in text)
        {
            if (character is '@' or '?' or '#' or '%' or '\\' || char.IsWhiteSpace(character) || char.IsControl(character))
                return null;
        }
        // No dot segments anywhere (the path is compared as written below).
        if (text.Contains("/.", StringComparison.Ordinal))
            return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrEmpty(uri.Host))
            return null;

        string scheme;
        if (string.Equals(uri.Scheme, "wss", StringComparison.Ordinal))
            scheme = Uri.UriSchemeHttps;
        else if (string.Equals(uri.Scheme, "ws", StringComparison.Ordinal) && IsLoopbackHost(uri))
            scheme = Uri.UriSchemeHttp;
        else
            return null;

        // The parsed path must be the canonical one and must be exactly how the
        // address was written (no dot segments or other normalization).
        var path = uri.AbsolutePath;
        if (path is not (ControlPath or ControlPath + "/") || !text.EndsWith(path, StringComparison.Ordinal))
            return null;

        return Uri.TryCreate($"{scheme}://{uri.Authority}{AppearancePath}/", UriKind.Absolute, out var derived)
            ? derived
            : null;
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        if (uri.HostNameType == UriHostNameType.Dns)
            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        return uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 &&
            IPAddress.TryParse(uri.DnsSafeHost, out var address) && IPAddress.IsLoopback(address);
    }
}
