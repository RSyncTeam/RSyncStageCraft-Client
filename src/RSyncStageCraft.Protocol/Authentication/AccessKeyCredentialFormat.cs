namespace RSyncStageCraft.Core.Authentication;

/// <summary>
/// The access-key credential layout shared by client and server:
/// <c>rsc1.&lt;32 lowercase hex key id&gt;.&lt;64 lowercase hex secret&gt;</c>.
/// Issuing and checking keys stays with the server; this only describes the format.
/// </summary>
public static class AccessKeyCredentialFormat
{
    public const string Prefix = "rsc1.";
    public const int SecretBytes = 32;
    public const int KeyIdStart = 5;
    public const int KeyIdLength = 32;
    public const int SecretSeparatorIndex = KeyIdStart + KeyIdLength;
    public const int SecretStart = SecretSeparatorIndex + 1;
    public const int CredentialLength = SecretStart + (2 * SecretBytes);

    /// <summary>True for the exact canonical form: the prefix, a nonzero key id and the secret, all lowercase hex.</summary>
    public static bool IsCanonical(ReadOnlySpan<char> value)
    {
        if (value.Length != CredentialLength || !value.StartsWith(Prefix, StringComparison.Ordinal) || value[SecretSeparatorIndex] != '.')
            return false;
        if (!Guid.TryParseExact(value.Slice(KeyIdStart, KeyIdLength), "N", out var keyId) || keyId == Guid.Empty)
            return false;
        for (var index = KeyIdStart; index < value.Length; index++)
        {
            if (index != SecretSeparatorIndex && value[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}
