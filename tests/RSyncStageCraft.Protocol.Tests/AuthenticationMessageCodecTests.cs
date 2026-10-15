#nullable enable

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

public sealed class AuthenticationMessageCodecTests
{
    private static string Credential => "rsc1.1234567890abcdef1234567890abcdef." + new string('a', 64);

    [Fact]
    public void RequestRoundTripsThroughStrictParserWithoutASecretBearingDto()
    {
        var bytes = AuthenticationMessageCodec.EncodeRequest(Credential, new(1), new(true, false, true));
        Span<char> credential = stackalloc char[AccessKeyCredentialFormat.CredentialLength];
        try
        {
            Assert.True(bytes.Length <= AuthenticationMessageCodec.MaximumRequestBytes);
            Assert.True(AuthenticationMessageCodec.TryReadRequest(bytes, credential, out var hello, out var readiness));
            Assert.True(credential.SequenceEqual(Credential));
            Assert.Equal(new HelloMessage(1), hello);
            Assert.Equal(new ReadinessMessage(true, false, true), readiness);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(credential));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void MalformedAmbiguousOrUnsupportedRequestsAreRejected(int variant)
    {
        var original = Encoding.UTF8.GetString(AuthenticationMessageCodec.EncodeRequest(Credential, new(1), new(true, true, true)));
        var invalid = variant switch
        {
            0 => "",
            1 => original[..^1],
            2 => original + "{}",
            3 => original.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            4 => original.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            5 => original.Replace("\"version\":1", "\"version\":1,\"ver\\u0073ion\":1", StringComparison.Ordinal),
            6 => original.Replace("\"version\":1", "\"version\":1,\"userId\":\"owner\"", StringComparison.Ordinal),
            7 => original.Replace("\"version\":1", "\"version\":1,\"sessionId\":1", StringComparison.Ordinal),
            8 => original.Replace("\"protocolVersion\":1", "\"protocolVersion\":\"1\"", StringComparison.Ordinal),
            9 => original.Replace("\"ipcReady\":true", "\"ipcReady\":1", StringComparison.Ordinal),
            10 => original.Replace(",\"ipcReady\":true", "", StringComparison.Ordinal),
            11 => original.Replace("rsc1.", "rsc2.", StringComparison.Ordinal),
            12 => original.Replace("rsc1.", "rsc1. ", StringComparison.Ordinal),
            13 => original.Replace("rsc1.", "rsc1\\u002e", StringComparison.Ordinal),
            14 => original.Replace("credential", "Credential", StringComparison.Ordinal),
            15 => original.Replace(Credential, Credential.ToUpperInvariant(), StringComparison.Ordinal),
            16 => original.Replace("\"protocolVersion\":1", "\"protocolVersion\":2147483648", StringComparison.Ordinal),
            17 => original.Replace("\"ipcReady\":true", "\"ipcReady\":{}", StringComparison.Ordinal),
            18 => original.Replace("\"version\":1", "\"version\":1/*comment*/", StringComparison.Ordinal),
            _ => new string(' ', AuthenticationMessageCodec.MaximumRequestBytes + 1),
        };
        Span<char> credential = stackalloc char[AccessKeyCredentialFormat.CredentialLength];
        Assert.False(AuthenticationMessageCodec.TryReadRequest(Encoding.UTF8.GetBytes(invalid), credential, out _, out _));
    }

    [Fact]
    public void EncoderRejectsNoncanonicalCredentialsWithoutEchoingThem()
    {
        var invalid = "must-not-appear-in-error";
        var exception = Assert.Throws<ArgumentException>(() => AuthenticationMessageCodec.EncodeRequest(invalid, new(1), new()));
        Assert.DoesNotContain(invalid, exception.Message);
    }

    [Fact]
    public void AuthorizationCarriesOnlyExactPositiveServerNumbersAndHasNoAuthorityByItself()
    {
        var authorization = new AuthorizeMessage(long.MaxValue, long.MaxValue, long.MaxValue);
        var bytes = AuthenticationMessageCodec.EncodeAuthorization(authorization);
        Assert.True(bytes.Length <= AuthenticationMessageCodec.MaximumResponseBytes);
        Assert.True(AuthenticationMessageCodec.TryReadAuthorization(bytes, out var result));
        Assert.Equal(authorization, result);
        Assert.Throws<ArgumentException>(() => AuthenticationMessageCodec.EncodeAuthorization(default));
    }

    [Fact]
    public void TheReplyCarriesTheStableIdAndABoundedOptionalDisplayName()
    {
        // Worst case: every number at its maximum, the longest stable id and the
        // longest display name, each made of characters that escape to six bytes.
        var longestId = new string('\u0001', ProtocolLimits.MaximumIdentifierLength);
        var longestName = new string('<', RscDisplayName.MaximumUtf8Bytes);
        var worst = new AuthorizeMessage(long.MaxValue, long.MaxValue, long.MaxValue, longestId, longestName);
        var bytes = AuthenticationMessageCodec.EncodeAuthorization(worst);
        Assert.InRange(bytes.Length, 1, AuthenticationMessageCodec.MaximumResponseBytes);
        Assert.True(AuthenticationMessageCodec.TryReadAuthorization(bytes, out var read));
        Assert.Equal(worst, read);

        // The stable id is never dropped for its length; the display name is optional.
        var withoutName = new AuthorizeMessage(1, 2, 3, longestId);
        Assert.True(AuthenticationMessageCodec.TryReadAuthorization(
            AuthenticationMessageCodec.EncodeAuthorization(withoutName), out var anonymous));
        Assert.Equal(withoutName, anonymous);
        Assert.Null(anonymous.DisplayName);

        // 64 UTF-8 bytes, measured in bytes: 32 two-byte characters fit, 33 do not.
        var fits = new AuthorizeMessage(1, 2, 3, "alice", new string('é', 32));
        Assert.True(AuthenticationMessageCodec.TryReadAuthorization(AuthenticationMessageCodec.EncodeAuthorization(fits), out read));
        Assert.Equal(fits, read);
        Assert.Throws<ArgumentException>(() => AuthenticationMessageCodec.EncodeAuthorization(fits with { DisplayName = new string('é', 33) }));
        // A name never comes without the stable id it belongs to.
        Assert.Throws<ArgumentException>(() => AuthenticationMessageCodec.EncodeAuthorization(new(1, 2, 3, null, "Ada")));

        // A reply carrying an invalid name is refused, even within the byte bound.
        var original = Encoding.UTF8.GetString(AuthenticationMessageCodec.EncodeAuthorization(new(1, 2, 3, "alice")));
        foreach (var name in new[] { new string('x', 65), " Ada", "Ad\\u0007a", "" })
        {
            var forged = original.Replace("}", ",\"displayName\":\"" + name + "\"}", StringComparison.Ordinal);
            Assert.False(AuthenticationMessageCodec.TryReadAuthorization(Encoding.UTF8.GetBytes(forged), out var refused), name);
            Assert.Equal(default, refused);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void InvalidOrAmbiguousAuthorizationCannotActivateAReceiver(int variant)
    {
        var original = Encoding.UTF8.GetString(AuthenticationMessageCodec.EncodeAuthorization(new(1, 2, 3)));
        var invalid = variant switch
        {
            0 => "",
            1 => original + "{}",
            2 => original.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            3 => original.Replace("\"sessionId\":1", "\"sessionId\":0", StringComparison.Ordinal),
            4 => original.Replace("\"generation\":2", "\"generation\":-1", StringComparison.Ordinal),
            5 => original.Replace("\"connectionEpoch\":3", "\"connectionEpoch\":3,\"connectionEpoch\":4", StringComparison.Ordinal),
            6 => original.Replace("\"sessionId\":1", "\"sessionId\":1,\"credential\":\"unexpected\"", StringComparison.Ordinal),
            7 => original.Replace("authorized", "rejected", StringComparison.Ordinal),
            8 => original.Replace("\"connectionEpoch\":3", "\"connectionEpoch\":9223372036854775808", StringComparison.Ordinal),
            9 => original.Replace(",\"generation\":2", "", StringComparison.Ordinal),
            _ => new string(' ', AuthenticationMessageCodec.MaximumResponseBytes + 1),
        };
        Assert.False(AuthenticationMessageCodec.TryReadAuthorization(Encoding.UTF8.GetBytes(invalid), out var result));
        Assert.Equal(default, result);
    }
}
