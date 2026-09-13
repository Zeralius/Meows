using System.Runtime.InteropServices;
using System.Text;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// A plugin's credentials, each a file of its own under <c>secrets\</c> in the plugin's data
/// folder, sealed with Windows data protection for the current user.
///
/// Started life inside Scruff, the first plugin to hold a real credential, and moved here the
/// moment a second one needed it: one sealing routine, one folder layout, one place to audit.
/// The files Scruff wrote are exactly where this looks, so nothing had to move.
///
/// The call is made directly rather than through the ProtectedData package, because that
/// package is named System.Security.Cryptography.ProtectedData and the shell would have to
/// carry it for every plugin. Two P/Invokes are cheaper than a dependency.
/// </summary>
public sealed class SecretStore : IMeowsSecrets
{
    private readonly string _directory;

    public SecretStore(string pluginDataDirectory) => _directory = Path.Combine(pluginDataDirectory, "secrets");

    private string PathFor(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_directory, safe + ".secret");
    }

    public bool Has(string name) => File.Exists(PathFor(name));

    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
            return null;

        try
        {
            return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)));
        }
        catch (Exception)
        {
            // Another user's file, or a machine this was copied to. Unreadable is the right
            // answer; it is not the shell's to recover.
            return null;
        }
    }

    public void Set(string name, string value)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(PathFor(name), Protect(Encoding.UTF8.GetBytes(value)));
    }

    public void Forget(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    // ---- DPAPI --------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public nint Data;
    }

    private const uint UiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, nint entropy,
        nint reserved, nint prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, nint entropy,
        nint reserved, nint prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    public static byte[] Protect(byte[] clear) => Call(clear, protect: true);

    public static byte[] Unprotect(byte[] sealedBytes) => Call(sealedBytes, protect: false);

    private static byte[] Call(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Secrets are sealed with Windows data protection.");

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Length = bytes.Length, Data = handle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, "Meows", nint.Zero, nint.Zero, nint.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, nint.Zero, nint.Zero, nint.Zero, nint.Zero, UiForbidden, out output);

            if (!ok)
                throw new InvalidOperationException($"Data protection failed with error {Marshal.GetLastWin32Error()}.");

            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
