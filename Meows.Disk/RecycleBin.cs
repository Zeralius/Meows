using Meows.Plugins.Abstractions;
using System.Runtime.InteropServices;

namespace Meows.Disk;

public sealed record DeleteOutcome(int Deleted, int Failed, string? FailureReason)
{
    public bool Succeeded => Failed == 0 && FailureReason is null;
}

/// <summary>
/// Recycle Bin, never File.Delete. Files get removed here in bulk based on an automated
/// judgement like a hash or a size, so every removal has to stay recoverable. Nothing in the
/// plugins should delete by any other route.
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

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

    /// <summary>Whether the shell could send this path to the bin.</summary>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Sends files and folders to the Recycle Bin. Folders go whole, contents included, which
    /// is what a tool that reports one large directory needs.
    /// </summary>
    public static DeleteOutcome Send(IReadOnlyList<string> paths)
    {
        // Names ending in a space or a dot do not survive normalising, and GetFullPath below
        // is what normalises them: "data." becomes "data", so a delete aimed at one hits the
        // other and reports success. There is no safe way to pass those to the shell, so refuse.
        //
        // Checked before Exists rather than after, because Exists normalises too and so answers
        // about the neighbour: false when nothing is next door (silently dropping the path), true
        // when something is, which is the dangerous case.
        var unsafePaths = paths.Where(p => !WalkRules.SurvivesNormalising(p)).ToList();
        var candidates = paths.Where(WalkRules.SurvivesNormalising)
            .Where(Exists)
            .Select(Path.GetFullPath)
            .Distinct()
            .ToList();

        // Anything the bin cannot take would be deleted for good, silently, so it is held back
        // here with the reason rather than handed to the shell.
        var noBin = candidates
            .Select(p => (Path: p, Why: BinReach.Check(p)))
            .Where(x => x.Why != BinRefusal.None)
            .ToList();
        var existing = candidates.Except(noBin.Select(x => x.Path)).ToList();

        var refusals = new List<string>();
        if (unsafePaths.Count > 0)
            refusals.Add(Refusal(unsafePaths.Count));
        foreach (var group in noBin.GroupBy(x => x.Why))
            refusals.Add(NoBin(group.Key, group.Count()));
        var refused = unsafePaths.Count + noBin.Count;

        if (existing.Count == 0)
        {
            return refused == 0
                ? new DeleteOutcome(0, 0, null)
                : new DeleteOutcome(0, refused, string.Join(" ", refusals));
        }

        if (!OperatingSystem.IsWindows())
            return new DeleteOutcome(0, existing.Count, MeowsText.Current["disk.delete.windowsonly"]);

        // SHFileOperation wants a double-null-terminated list. The embedded nulls survive the
        // LPWStr marshaller because it copies the managed string by Length.
        var buffer = string.Join('\0', existing) + "\0\0";

        var operation = new SHFILEOPSTRUCT
        {
            hwnd = nint.Zero,
            wFunc = FO_DELETE,
            pFrom = buffer,
            pTo = null,
            // FOF_WANTNUKEWARNING is the backstop for a case BinReach did not foresee: Windows
            // asks rather than deleting for good without a word.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
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
            return new DeleteOutcome(0, existing.Count, MeowsText.Current.Format("disk.delete.shellfailed", result.ToString("X")));

        if (operation.fAnyOperationsAborted)
            return new DeleteOutcome(0, existing.Count, MeowsText.Current["disk.delete.aborted"]);

        // Check the disk rather than trusting the return code.
        var leftover = existing.Count(Exists);
        var stillThere = leftover + refused;
        var deleted = existing.Count - leftover;

        if (stillThere == 0)
            return new DeleteOutcome(deleted, 0, null);

        if (leftover > 0)
            refusals.Insert(0, MeowsText.Current.Format("disk.delete.leftover", stillThere));

        return new DeleteOutcome(deleted, stillThere, string.Join(" ", refusals));
    }

    /// <summary>Explains what was held back because the bin could not take it.</summary>
    private static string NoBin(BinRefusal why, int count) =>
        MeowsText.Current.Format(why switch
        {
            BinRefusal.Network => "disk.delete.nobin.network",
            BinRefusal.Removable => "disk.delete.nobin.removable",
            BinRefusal.Switched => "disk.delete.nobin.switched",
            BinRefusal.TooLarge => "disk.delete.nobin.toolarge",
            _ => "disk.delete.nobin.toolong",
        }, count);

    /// <summary>Explains what was refused and why.</summary>
    private static string Refusal(int count) =>
        count == 1
            ? MeowsText.Current["disk.delete.refused.one"]
            : MeowsText.Current.Format("disk.delete.refused.many", count);
}
