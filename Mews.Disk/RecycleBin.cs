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
        // A name ending in a space or a dot does not survive being normalised, and GetFullPath
        // below is exactly what normalises it. "data." becomes "data", so a delete aimed at one
        // lands on the other and cheerfully reports success for having destroyed the wrong
        // folder. Refuse those outright: there is no way to name them safely here, and quietly
        // deleting something the user did not pick is the worst thing this code could do.
        // Checked before Exists, not after. Exists normalises too, so it answers about the
        // neighbour rather than about the thing asked for: it says false when nothing sits next
        // door, which would drop the path silently, and true when something does, which is
        // precisely the case that must not go through.
        var unsafePaths = paths.Where(p => !WalkRules.SurvivesNormalising(p)).ToList();
        var existing = paths.Where(WalkRules.SurvivesNormalising)
            .Where(Exists)
            .Select(Path.GetFullPath)
            .Distinct()
            .ToList();

        if (existing.Count == 0)
        {
            return unsafePaths.Count == 0
                ? new DeleteOutcome(0, 0, null)
                : new DeleteOutcome(0, unsafePaths.Count, Refusal(unsafePaths.Count));
        }

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
        var stillThere = existing.Count(Exists) + unsafePaths.Count;
        var deleted = existing.Count - existing.Count(Exists);

        if (stillThere == 0)
            return new DeleteOutcome(deleted, 0, null);

        var reason = unsafePaths.Count > 0 && existing.Count(Exists) == 0
            ? Refusal(unsafePaths.Count)
            : $"{stillThere} item(s) could not be removed." +
              (unsafePaths.Count > 0 ? " " + Refusal(unsafePaths.Count) : "");

        return new DeleteOutcome(deleted, stillThere, reason);
    }

    /// <summary>Says what was refused and why, in terms of the thing on disk.</summary>
    private static string Refusal(int count) =>
        count == 1
            ? "One of these has a name ending in a space or a dot, which Windows cannot address " +
              "safely. Removing it would delete whatever sits next to it instead, so it was left alone. " +
              "Rename it first."
            : $"{count} of these have names ending in a space or a dot, which Windows cannot " +
              "address safely. Removing them would delete whatever sits next to them instead, so " +
              "they were left alone. Rename them first.";
}
