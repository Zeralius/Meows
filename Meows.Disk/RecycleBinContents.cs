using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Meows.Plugins.Abstractions;

namespace Meows.Disk;

/// <summary>
/// One thing sitting in a Recycle Bin: where it came from, how big it was, when it went, and
/// the two files the bin keeps it as.
/// </summary>
/// <param name="DataPath">The <c>$R</c> file or folder: the thing itself, under its bin name.</param>
/// <param name="MetadataPath">The <c>$I</c> file beside it, which is the only record of where it came from.</param>
/// <param name="OriginalPath">Where it was when it was deleted, which is where a restore puts it back.</param>
/// <param name="Size">What the bin recorded, which for a folder is everything inside it.</param>
/// <param name="DeletedAt">Local time, converted from the FILETIME in the metadata.</param>
public sealed record BinItem(
    string DataPath,
    string MetadataPath,
    string OriginalPath,
    long Size,
    DateTime DeletedAt,
    bool IsFolder)
{
    public string Name => Path.GetFileName(OriginalPath) is { Length: > 0 } name ? name : OriginalPath;

    /// <summary>The folder it came out of, which is what says whether two rows are the same pile.</summary>
    public string OriginalFolder => Path.GetDirectoryName(OriginalPath) ?? "";

    /// <summary>Whether a restore would land on top of something that is there now.</summary>
    public bool IsBlocked => File.Exists(OriginalPath) || Directory.Exists(OriginalPath);

    public int Days(DateTime now) => Math.Max(0, (int)(now - DeletedAt).TotalDays);
}

/// <summary>What one drive's bin holds, and why it could not be read if it could not.</summary>
public sealed record BinDrive(string Root, IReadOnlyList<BinItem> Items, string? Unreadable = null)
{
    public long Bytes => Items.Sum(i => i.Size);

    public int Count => Items.Count;

    public DateTime? Oldest => Items.Count == 0 ? null : Items.Min(i => i.DeletedAt);
}

/// <summary>
/// Reads the Recycle Bin rather than deleting into it, which is what <see cref="RecycleBin"/>
/// does and the only thing anything here has done until now.
///
/// Every drive keeps its own bin at <c>&lt;drive&gt;\$Recycle.Bin\&lt;SID&gt;</c>, one folder per user,
/// and inside it each deleted thing is two entries sharing a name: <c>$I</c> holds the size, the
/// time and the path it came from, and <c>$R</c> is the thing itself. Nothing but those <c>$I</c>
/// files knows where anything came from, so a bin with its metadata lost is a folder of files
/// with no history, which is worth saying plainly rather than showing as empty.
///
/// Only the current user's folder is read. The others are there, they belong to other accounts,
/// and reading them needs rights this application has no business asking for.
/// </summary>
public static class RecycleBinContents
{
    /// <summary>The header both formats start with: version, size, then the FILETIME.</summary>
    private const int FixedHeader = 24;

    /// <summary>Windows 10 and later write version 2, which counts the path rather than padding it.</summary>
    private const long Version2 = 2;

    /// <summary>Before that the path was a fixed 260 characters, padded with nulls.</summary>
    private const int LegacyPathChars = 260;

    /// <summary>
    /// Reads one <c>$I</c> file's bytes. Separate from the disk so the format itself can be
    /// tested, since it is the only part here that can be subtly wrong: a misread length gives a
    /// plausible-looking path that restores to the wrong place.
    /// </summary>
    public static (string Path, long Size, DateTime DeletedAt)? ParseMetadata(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < FixedHeader)
            return null;

        var version = BitConverter.ToInt64(bytes[..8]);
        var size = BitConverter.ToInt64(bytes[8..16]);
        var stamp = BitConverter.ToInt64(bytes[16..24]);

        if (version is < 1 or > Version2 || size < 0)
            return null;

        DateTime deletedAt;
        try
        {
            deletedAt = DateTime.FromFileTime(stamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A stamp that is not a FILETIME at all. The item is still real and still takes up
            // room, so it is kept with a time nobody will mistake for a reading.
            deletedAt = DateTime.MinValue;
        }

        var rest = bytes[FixedHeader..];
        string path;

        if (version == Version2)
        {
            if (rest.Length < 4)
                return null;
            var chars = BitConverter.ToInt32(rest[..4]);
            // The count includes the terminator and is in characters, not bytes.
            if (chars <= 0 || chars > 1 + short.MaxValue || rest.Length < 4 + chars * 2)
                return null;
            path = Encoding.Unicode.GetString(rest.Slice(4, (chars - 1) * 2));
        }
        else
        {
            if (rest.Length < LegacyPathChars * 2)
                return null;
            path = Encoding.Unicode.GetString(rest[..(LegacyPathChars * 2)]);
            var end = path.IndexOf('\0');
            if (end >= 0)
                path = path[..end];
        }

        return path.Length == 0 ? null : (path, size, deletedAt);
    }

    /// <summary>
    /// Everything in one bin folder, which is a per-user folder rather than a drive. Anything
    /// unreadable is skipped rather than failing the lot: one corrupt <c>$I</c> should cost its
    /// own row and nothing else.
    /// </summary>
    public static IReadOnlyList<BinItem> Read(string binFolder)
    {
        var items = new List<BinItem>();

        DirectoryInfo folder;
        try
        {
            folder = new DirectoryInfo(binFolder);
            if (!folder.Exists)
                return items;
        }
        catch (Exception)
        {
            return items;
        }

        FileInfo[] metadata;
        try
        {
            metadata = folder.GetFiles("$I*");
        }
        catch (Exception)
        {
            return items;
        }

        foreach (var file in metadata)
        {
            (string Path, long Size, DateTime DeletedAt)? parsed;
            try
            {
                parsed = ParseMetadata(File.ReadAllBytes(file.FullName));
            }
            catch (Exception)
            {
                continue;
            }

            if (parsed is not { } entry)
                continue;

            // $I and $R differ by one character and nothing else, which is the whole of the
            // pairing rule.
            var dataPath = Path.Combine(folder.FullName, "$R" + file.Name[2..]);
            var isFolder = Directory.Exists(dataPath);
            if (!isFolder && !File.Exists(dataPath))
                continue; // Metadata for something already gone; not worth a row.

            items.Add(new BinItem(dataPath, file.FullName, entry.Path, entry.Size, entry.DeletedAt, isFolder));
        }

        return items;
    }

    /// <summary>
    /// Every fixed drive's bin for whoever is running, read. A drive whose bin cannot be opened
    /// is still listed, with the reason, because "C could not be read" and "C is empty" are
    /// different answers and only one of them is good news.
    /// </summary>
    public static IReadOnlyList<BinDrive> ReadAll(string? sid = null)
    {
        var drives = new List<BinDrive>();
        sid ??= CurrentUserSid();

        DriveInfo[] all;
        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            return drives;
        }

        foreach (var drive in all)
        {
            bool usable;
            try
            {
                usable = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch (Exception)
            {
                continue;
            }

            if (!usable)
                continue;

            if (sid is null)
            {
                drives.Add(new BinDrive(drive.Name, [], MeowsText.Current["disk.bin.nosid"]));
                continue;
            }

            var bin = Path.Combine(drive.Name, "$Recycle.Bin", sid);

            try
            {
                if (!Directory.Exists(bin))
                {
                    // No folder is the normal state for a drive nothing has been deleted from.
                    drives.Add(new BinDrive(drive.Name, []));
                    continue;
                }

                drives.Add(new BinDrive(drive.Name, Read(bin)));
            }
            catch (UnauthorizedAccessException)
            {
                drives.Add(new BinDrive(drive.Name, [], MeowsText.Current["disk.bin.denied"]));
            }
            catch (Exception ex)
            {
                drives.Add(new BinDrive(drive.Name, [], ex.Message));
            }
        }

        return drives;
    }

    /// <summary>Whose bin to read. Null off Windows, or when the identity cannot be had.</summary>
    public static string? CurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts one item back where it came from, by moving the <c>$R</c> entry to the path the
    /// <c>$I</c> file recorded and then dropping the metadata.
    ///
    /// Deliberately not the shell's own undo. The shell restores the last operation, which is
    /// not the same as restoring a row somebody picked out of a list of six months of deletions,
    /// and it would silently do something else when the two disagree.
    /// </summary>
    public static string? Restore(BinItem item)
    {
        var text = MeowsText.Current;

        if (item.IsBlocked)
            return text.Format("disk.bin.inthewaynow", item.OriginalPath);

        try
        {
            var parent = Path.GetDirectoryName(item.OriginalPath);
            if (parent is { Length: > 0 } && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            if (item.IsFolder)
                Directory.Move(item.DataPath, item.OriginalPath);
            else
                File.Move(item.DataPath, item.OriginalPath);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        try
        {
            File.Delete(item.MetadataPath);
        }
        catch (Exception)
        {
            // The thing is back, which is what was asked for. A metadata file with no $R beside
            // it is skipped by Read, so the row goes on the next refresh either way.
        }

        return null;
    }

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(nint hwnd, string? rootPath, uint flags);

    /// <summary>
    /// Empties one drive's bin, through the shell rather than by deleting the folder, because
    /// the bin is the shell's own bookkeeping and a folder deleted underneath it leaves the
    /// desktop icon claiming things are still in there.
    ///
    /// Asking has already happened by the time this is called. Nothing here confirms anything.
    /// </summary>
    public static string? Empty(string driveRoot)
    {
        if (!OperatingSystem.IsWindows())
            return MeowsText.Current["disk.delete.windowsonly"];

        int result;
        try
        {
            result = SHEmptyRecycleBinW(nint.Zero, driveRoot, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        // S_OK, and the code for a bin that was already empty, which is not a failure.
        const int S_OK = 0;
        const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
        return result is S_OK or E_UNEXPECTED
            ? null
            : MeowsText.Current.Format("disk.delete.shellfailed", result.ToString("X"));
    }

    public static string Humanise(long bytes) => FolderSize.Humanise(bytes);
}
