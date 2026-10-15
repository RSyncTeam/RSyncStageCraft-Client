using System.Globalization;
using System.Text;
using System.Text.Json;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Authentication;

/// <summary>
/// Separate first-message authentication format. Credentials never enter the
/// object-control codec or a serializable request DTO. Callers own and must
/// clear secret-bearing transport buffers after the synchronous exchange.
/// This codec provides no encryption, endpoint, or remote-server verification.
/// </summary>
public static class AuthenticationMessageCodec
{
    public const int Version = 1;
    public const int MaximumRequestBytes = 1024;
    /// <summary>
    /// The whole reply's bound (protocol 6). It carries the stable
    /// <c>userId</c> (any valid <see cref="RscUserId"/>: at most
    /// <see cref="ProtocolLimits.MaximumIdentifierLength"/> UTF-16 units) and an
    /// optional <c>displayName</c> (at most <see cref="RscDisplayName.MaximumUtf8Bytes"/>
    /// UTF-8 bytes, so at most that many UTF-16 units). Worst case: 167 bytes of
    /// fixed text with every number at long.MaxValue and both strings empty,
    /// plus at most 6 escaped bytes per UTF-16 unit of either string
    /// (6 × 256 + 6 × 64 = 1920), so 2087; rounded up to 2304.
    /// </summary>
    public const int MaximumResponseBytes = 2304;

    public static byte[] EncodeRequest(ReadOnlySpan<char> credential, HelloMessage hello, ReadinessMessage readiness)
    {
        if (!IsCanonicalCredential(credential))
            throw new ArgumentException("A canonical access key is required.", nameof(credential));
        // Only the fixed-format secret is copied directly into the owned result;
        // no intermediate credential string or JSON writer buffer is created.
        ReadOnlySpan<byte> prefix = "{\"version\":1,\"credential\":\""u8;
        var suffix = Encoding.UTF8.GetBytes("\",\"protocolVersion\":" + hello.ProtocolVersion.ToString(CultureInfo.InvariantCulture) +
            ",\"penumbraReady\":" + BooleanText(readiness.PenumbraReady) + ",\"glamourerReady\":" + BooleanText(readiness.GlamourerReady) +
            ",\"ipcReady\":" + BooleanText(readiness.IpcReady) + "}");
        var bytes = new byte[prefix.Length + credential.Length + suffix.Length];
        prefix.CopyTo(bytes);
        for (var index = 0; index < credential.Length; index++)
            bytes[prefix.Length + index] = (byte)credential[index];
        suffix.CopyTo(bytes.AsSpan(prefix.Length + credential.Length));
        return bytes;
    }

    public static bool TryReadRequest(ReadOnlySpan<byte> bytes, Span<char> credential,
        out HelloMessage hello, out ReadinessMessage readiness)
    {
        hello = default;
        readiness = default;
        if (bytes.IsEmpty || bytes.Length > MaximumRequestBytes || credential.Length != AccessKeyCredentialFormat.CredentialLength)
            return false;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 2 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            var seen = 0;
            var protocol = 0;
            var penumbra = false;
            var glamourer = false;
            var ipc = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = reader.ValueTextEquals("version"u8) ? 1 : reader.ValueTextEquals("credential"u8) ? 2 :
                    reader.ValueTextEquals("protocolVersion"u8) ? 4 : reader.ValueTextEquals("penumbraReady"u8) ? 8 :
                    reader.ValueTextEquals("glamourerReady"u8) ? 16 : reader.ValueTextEquals("ipcReady"u8) ? 32 : 0;
                if (field == 0 || (seen & field) != 0 || !reader.Read())
                    return false;
                seen |= field;
                switch (field)
                {
                    case 1:
                        if (reader.GetInt32() != Version) return false;
                        break;
                    case 2:
                        if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped || reader.ValueSpan.Length != credential.Length)
                            return false;
                        for (var index = 0; index < credential.Length; index++)
                            credential[index] = (char)reader.ValueSpan[index];
                        if (!IsCanonicalCredential(credential)) return false;
                        break;
                    case 4: protocol = reader.GetInt32(); break;
                    case 8: penumbra = reader.GetBoolean(); break;
                    case 16: glamourer = reader.GetBoolean(); break;
                    case 32: ipc = reader.GetBoolean(); break;
                }
            }
            if (seen != 63 || reader.TokenType != JsonTokenType.EndObject || reader.Read())
                return false;
            hello = new(protocol);
            readiness = new(penumbra, glamourer, ipc);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static byte[] EncodeAuthorization(AuthorizeMessage authorization)
    {
        if (!IsValid(authorization))
            throw new ArgumentException("A valid connection authorization is required.", nameof(authorization));
        var user = authorization.UserId is { } id ? ",\"userId\":" + JsonSerializer.Serialize(id) : string.Empty;
        var name = authorization.DisplayName is { } display ? ",\"displayName\":" + JsonSerializer.Serialize(display) : string.Empty;
        return Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{{\"version\":1,\"status\":\"authorized\",\"sessionId\":{authorization.SessionId},\"generation\":{authorization.Generation},\"connectionEpoch\":{authorization.ConnectionEpoch}{user}{name}}}"));
    }

    public static bool TryReadAuthorization(ReadOnlySpan<byte> bytes, out AuthorizeMessage authorization)
    {
        authorization = default;
        if (bytes.IsEmpty || bytes.Length > MaximumResponseBytes)
            return false;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 2 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            var seen = 0;
            long session = 0, generation = 0, epoch = 0;
            string? user = null;
            string? displayName = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = reader.ValueTextEquals("version"u8) ? 1 : reader.ValueTextEquals("status"u8) ? 2 :
                    reader.ValueTextEquals("sessionId"u8) ? 4 : reader.ValueTextEquals("generation"u8) ? 8 :
                    reader.ValueTextEquals("connectionEpoch"u8) ? 16 :
                    reader.ValueTextEquals("userId"u8) ? 32 :
                    reader.ValueTextEquals("displayName"u8) ? 64 : 0;
                if (field == 0 || (seen & field) != 0 || !reader.Read()) return false;
                seen |= field;
                switch (field)
                {
                    case 1: if (reader.GetInt32() != Version) return false; break;
                    case 2: if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("authorized"u8)) return false; break;
                    case 4: session = reader.GetInt64(); break;
                    case 8: generation = reader.GetInt64(); break;
                    case 16: epoch = reader.GetInt64(); break;
                    case 32:
                        if (reader.TokenType != JsonTokenType.String) return false;
                        user = reader.GetString();
                        break;
                    case 64:
                        if (reader.TokenType != JsonTokenType.String) return false;
                        displayName = reader.GetString();
                        break;
                }
            }
            var result = new AuthorizeMessage(session, generation, epoch, user, displayName);
            // userId and displayName (protocol 6) are optional on the wire (the
            // server always sends userId); every other field is required.
            if ((seen & 31) != 31 || reader.TokenType != JsonTokenType.EndObject || reader.Read() || !IsValid(result))
                return false;
            authorization = result;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static bool IsValid(AuthorizeMessage value) => value.SessionId > 0 && value.Generation > 0 && value.ConnectionEpoch > 0 &&
        (value.UserId is null || new RscUserId(value.UserId).IsValid) &&
        // A display name only ever accompanies the stable id it names.
        (value.DisplayName is null || (value.UserId is not null && RscDisplayName.IsValid(value.DisplayName)));
    private static string BooleanText(bool value) => value ? "true" : "false";
    public static bool IsCanonicalCredential(ReadOnlySpan<char> value) => AccessKeyCredentialFormat.IsCanonical(value);
}
