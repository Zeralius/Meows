using System.Runtime.InteropServices;

namespace Mews.Disk;

public sealed record DeleteOutcome(int Deleted, int Failed, string? FailureReason)
{
    public bool Succeeded => Failed == 0 && FailureReason is null;
}

/// <summary>
/// Recycle Bin, not File.Delete. Things are removed here in bulk on the strength of an
/// automated judgement, a hash or a size, so every removal has to stay undoable. No exceptions
/// to this, and no path through either plugin that calls anything else.
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    private struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT fileOp);

    /// <summary>Whether this path is something the shell could send to the bin.</summary>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Sends files and folders to the Recycle Bin. Folders go whole, with everything inside
    /// them, which is the entire point for a tool that finds one fat directory rather than a
    /// list of fat files.
    /// </summary>
    public static DeleteOutcome Send(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(Exists).Select(Path.GetFullPath).Distinct().ToList();
        if (existing.Count == 0)
            return new DeleteOutcome(0, 0, null);

        if (!OperatingSystem.IsWindows())
            return new DeleteOutcome(0, existing.Count, "Recycle Bin deletion is only implemented for Windows.");

        // SHFileOperation wants a double-null-terminated list. The embedded nulls survive
        // the LPWStr marshaller because it copies the whole managed string by Length.
        var buffer = string.Join('\0', existing) + "\0\0";

        var operation = new SHFILEOPSTRUCT
        {
            hwnd = nint.Zero,
            wFunc = FO_DELETE,
            pFrom = buffer,
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };

        int result;
        try
        {
            result = SHFileOperationW(ref operation);
        }
        catch (Exception ex)
        {
            return new DeleteOutcome(0, existing.Count, ex.Message);
        }

        if (result != 0)
            return new DeleteOutcome(0, existing.Count, $"Shell delete failed with code 0x{result:X}.");

        if (operation.fAnyOperationsAborted)
            return new DeleteOutcome(0, existing.Count, "The delete was aborted.");

        // Check the disk rather than trusting the return code.
        var stillThere = existing.Count(Exists);
        return new DeleteOutcome(existing.Count - stillThere, stillThere,
            stillThere == 0 ? null : $"{stillThere} item(s) could not be removed.");
    }
}
