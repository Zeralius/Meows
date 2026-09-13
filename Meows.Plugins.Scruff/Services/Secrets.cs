using System.Runtime.InteropServices;
using System.Text;

namespace Meows.Plugins.Scruff.Services;

/// <summary>
/// Where an app password or a token lives.
///
/// This is the first plugin that has to hold a real credential itself, with no separate program
/// to keep it in, and the rules were settled before it was written: never in settings.json,
/// never in clear text, and never an account password when the service offers something
/// revocable instead. Each one is a file of its own in the plugin's data folder, sealed with
/// Windows' data protection for the current user, so it opens on this machine for this account
/// and is noise anywhere else.
///
/// The call is made directly rather than through the ProtectedData package, because that
/// package is named System.Security.Cryptography.ProtectedData and the shell shares everything
/// beginning with System. with itself. A copy in the plugin folder would be ignored and the
/// shell does not carry one.
/// </summary>
public sealed class Secrets
{
    private readonly string _directory;

    public Secrets(string directory) => _directory = directory;

    private string PathFor(string name) => Path.Combine(_directory, name + ".secret");

    public bool Has(string name) => File.Exists(PathFor(name));

    public void Save(string name, string value)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(PathFor(name), Protect(Encoding.UTF8.GetBytes(value)));
    }

    public string? Load(string name)
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
            // answer; it is not the plugin's to recover.
            return null;
        }
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

    public static byte[] Unprotect(byte[] sealed_) => Call(sealed_, protect: false);

    private static byte[] Call(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Secrets are sealed with Windows data protection.");

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Length = bytes.Length, Data = handle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, "Meows Scruff", nint.Zero, nint.Zero, nint.Zero, UiForbidden, out var output)
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
