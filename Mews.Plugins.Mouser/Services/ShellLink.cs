using System.Text;

namespace Mews.Plugins.Mouser.Services;

/// <summary>
/// Just enough of the Windows shortcut format to answer one question: what does this point at?
///
/// Read from the bytes rather than through the shell, because asking the shell to resolve a
/// shortcut makes it go looking: it will hunt for a moved target, hit the network, and can sit
/// there for seconds on a dead drive. A scan that touches thousands of shortcuts cannot afford
/// that, and a shortcut that needs hunting for is precisely the one being asked about.
/// </summary>
public static class ShellLink
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 1 << 0;
    private const uint HasLinkInfo = 1 << 1;
    private const uint VolumeIdAndLocalBasePath = 1 << 0;

    /// <summary>
    /// The path a shortcut points at, or null when the file is not a shortcut, is damaged, or
    /// only records a target this does not understand.
    /// </summary>
    public static string? TargetOf(string path)
    {
        try
        {
            return TargetIn(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? TargetIn(byte[] bytes)
    {
        if (bytes.Length < HeaderSize || BitConverter.ToInt32(bytes, 0) != HeaderSize)
            return null;

        var flags = BitConverter.ToUInt32(bytes, 20);
        var at = HeaderSize;

        // The target id list is a shell namespace thing rather than a path, so it is stepped
        // over. Its own size prefix is how far to step.
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (at + 2 > bytes.Length)
                return null;

            at += 2 + BitConverter.ToUInt16(bytes, at);
        }

        // 0x1C is the smallest LinkInfo header, and every offset read below lives inside it.
        // A shortcut cut off partway through is a real thing to meet on a dying disk, so the
        // length is checked against what is about to be read rather than assumed.
        if ((flags & HasLinkInfo) == 0 || at + 0x1C > bytes.Length)
            return null;

        var info = at;
        var infoHeaderSize = BitConverter.ToInt32(bytes, info + 4);
        var infoFlags = BitConverter.ToUInt32(bytes, info + 8);

        // Without a local path this is a network shortcut, which is not what is being looked for.
        if ((infoFlags & VolumeIdAndLocalBasePath) == 0)
            return null;

        var basePathOffset = BitConverter.ToInt32(bytes, info + 16);
        var suffixOffset = BitConverter.ToInt32(bytes, info + 24);

        // A larger header means the same two strings appear again as UTF-16 further along.
        var unicode = infoHeaderSize >= 0x24 && info + 0x24 <= bytes.Length;
        if (unicode)
        {
            var basePathUnicode = BitConverter.ToInt32(bytes, info + 28);
            var suffixUnicode = BitConverter.ToInt32(bytes, info + 32);

            var wide = ReadWide(bytes, info + basePathUnicode) + ReadWide(bytes, info + suffixUnicode);
            if (wide.Length > 0)
                return Trustworthy(wide);
        }

        var narrow = ReadNarrow(bytes, info + basePathOffset) + ReadNarrow(bytes, info + suffixOffset);
        return Trustworthy(narrow);
    }

    /// <summary>
    /// Refuses a path that did not decode cleanly. Saying "I cannot tell" is always better here
    /// than saying "this is dead", because only one of those gets something deleted.
    /// </summary>
    private static string? Trustworthy(string path) =>
        path.Length == 0 || path.Contains('�') ? null : path;

    private static string ReadNarrow(byte[] bytes, int at)
    {
        if (at < 0 || at >= bytes.Length)
            return "";

        var end = at;
        while (end < bytes.Length && bytes[end] != 0)
            end++;

        // Latin1 rather than Encoding.Default, which is UTF-8 on .NET and turns any accented
        // character in an old style path into a replacement character. A mangled path does not
        // exist on disk, and this tool reporting a working shortcut as dead is the one mistake
        // it must not make.
        return Encoding.Latin1.GetString(bytes, at, end - at);
    }

    private static string ReadWide(byte[] bytes, int at)
    {
        if (at < 0 || at + 1 >= bytes.Length)
            return "";

        var end = at;
        while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0))
            end += 2;

        return Encoding.Unicode.GetString(bytes, at, end - at);
    }
}
