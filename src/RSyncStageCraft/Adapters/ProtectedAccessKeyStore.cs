using System.Runtime.InteropServices;
using System.Text;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Keeps the user's access key in a file encrypted with Windows DPAPI for the
/// current Windows user, separate from the plugin's plain JSON settings. The
/// key never appears in logs or in the settings file.
/// </summary>
internal sealed class ProtectedAccessKeyStore
{
    internal const string FileName = "access-key.dpapi";
    private const int MaximumFileBytes = 16 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RSyncStageCraft.AccessKey.v1");
    private readonly string path;

    internal ProtectedAccessKeyStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        path = Path.Combine(configDirectory, FileName);
    }

    internal bool Exists => File.Exists(path);

    /// <summary>Returns the stored key, or null if none is stored or it cannot be decrypted.</summary>
    internal string? Load()
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var info = new FileInfo(path);
            if (info.Length is 0 or > MaximumFileBytes)
                return null;
            var plain = Unprotect(File.ReadAllBytes(path));
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Stores the key, or deletes the file when the key is empty.</summary>
    internal bool Save(string? key)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var plain = Encoding.UTF8.GetBytes(key.Trim());
            byte[] protectedBytes;
            try
            {
                protectedBytes = Protect(plain);
            }
            finally
            {
                Array.Clear(plain);
            }
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] Protect(byte[] plain) => Transform(plain, protect: true);

    private static byte[] Unprotect(byte[] encrypted) => Transform(encrypted, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = default(DataBlob);
        try
        {
            var inBlob = new DataBlob(input.Length, inputHandle.AddrOfPinnedObject());
            var entropyBlob = new DataBlob(Entropy.Length, entropyHandle.AddrOfPinnedObject());
            var succeeded = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            if (!succeeded)
                throw new IOException("The access key could not be protected for this Windows user.");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // Wipe the decrypted copy before handing the buffer back.
                for (var index = 0; index < output.Size; index++)
                    Marshal.WriteByte(output.Data, index, 0);
                LocalFree(output.Data);
            }
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob(int size, IntPtr data)
    {
        public int Size = size;
        public IntPtr Data = data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
