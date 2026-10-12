using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace Meows.Disk;

/// <summary>Why a path cannot go to the Recycle Bin, if it cannot.</summary>
public enum BinRefusal
{
    None,

    /// <summary>A network share, or a mapped drive pointing at one. Windows deletes those for good.</summary>
    Network,

    /// <summary>A USB stick, card reader or other removable drive, which has no bin.</summary>
    Removable,

    /// <summary>The drive's bin is set to "Don't move files to the Recycle Bin".</summary>
    Switched,

    /// <summary>Bigger than the drive's bin will hold, so Windows would delete it for good.</summary>
    TooLarge,

    /// <summary>Longer than the shell's delete can address; it would stop the rest of the batch.</summary>
    TooLong,
}

/// <summary>
/// Works out, before anything is handed to the shell, whether a path would really land in the
/// Recycle Bin. SHFileOperation with FOF_NOCONFIRMATION deletes for good, without a word, when
/// the bin cannot take something; the check afterwards only sees that it is gone. So every case
/// where Windows would do that is refused here instead, with the reason.
/// </summary>
public static class BinReach
{
    /// <summary>What SHFileOperation's pFrom can hold per path.</summary>
    private const int MaxPath = 260;

    private const string BitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePath, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int bufferLength);

    /// <summary>Whether this full path can go to the bin, and why not if it cannot.</summary>
    public static BinRefusal Check(string fullPath)
    {
        if (fullPath.Length >= MaxPath)
            return BinRefusal.TooLong;

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return BinRefusal.Network;

        if (!OperatingSystem.IsWindows())
            return BinRefusal.None;

        var root = VolumeRoot(fullPath);
        if (root is null)
            return BinRefusal.None;

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
            switch (drive.DriveType)
            {
                case DriveType.Network:
                    return BinRefusal.Network;
                case DriveType.Removable:
                case DriveType.CDRom:
                    return BinRefusal.Removable;
            }
        }
        catch (Exception)
        {
            return BinRefusal.None;
        }

        var (switchedOff, capacity) = Settings(root, drive);
        if (switchedOff)
            return BinRefusal.Switched;

        if (capacity is { } limit && SizeExceeds(fullPath, limit))
            return BinRefusal.TooLarge;

        return BinRefusal.None;
    }

    /// <summary>The root of the volume the path is on, which for a folder mounted into another drive is that folder.</summary>
    private static string? VolumeRoot(string fullPath)
    {
        var buffer = new StringBuilder(MaxPath + 1);
        if (OperatingSystem.IsWindows() && GetVolumePathNameW(fullPath, buffer, buffer.Capacity))
            return buffer.ToString();

        return Path.GetPathRoot(fullPath);
    }

    /// <summary>
    /// The bin's own settings for this volume: switched off, and how many bytes it holds. Windows
    /// keeps them per volume GUID; a volume nobody has touched has no key, and then the global
    /// NukeOnDelete applies and the size is Windows' default, about a twentieth of the drive.
    /// </summary>
    private static (bool SwitchedOff, long? Capacity) Settings(string root, DriveInfo drive)
    {
        if (!OperatingSystem.IsWindows())
            return (false, null);

        long? fallback = null;
        try
        {
            fallback = drive.TotalSize / 20;
        }
        catch (Exception)
        {
            // Not ready, or not ours to ask. Leave the size alone rather than refuse everything.
        }

        try
        {
            using var global = Registry.CurrentUser.OpenSubKey(BitBucket);
            var nukeAll = global?.GetValue("NukeOnDelete") is int all && all != 0;

            var name = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPointW(root, name, name.Capacity))
                return (nukeAll, fallback);

            var guid = ExtractGuid(name.ToString());
            if (guid is null)
                return (nukeAll, fallback);

            using var volume = Registry.CurrentUser.OpenSubKey($@"{BitBucket}\Volume\{guid}");
            if (volume is null)
                return (nukeAll, fallback);

            var nuke = volume.GetValue("NukeOnDelete") is int n ? n != 0 : nukeAll;
            long? capacity = volume.GetValue("MaxCapacity") is int megabytes && megabytes > 0
                ? megabytes * 1024L * 1024
                : fallback;

            return (nuke, capacity);
        }
        catch (Exception)
        {
            return (false, fallback);
        }
    }

    /// <summary>"\\?\Volume{guid}\" to "{guid}".</summary>
    private static string? ExtractGuid(string volumeName)
    {
        var start = volumeName.IndexOf('{');
        var end = volumeName.IndexOf('}');
        return start >= 0 && end > start ? volumeName[start..(end + 1)] : null;
    }

    /// <summary>Whether a file or folder is bigger than the limit, stopping as soon as it is.</summary>
    private static bool SizeExceeds(string fullPath, long limit)
    {
        try
        {
            if (File.Exists(fullPath))
                return new FileInfo(fullPath).Length > limit;

            if (!Directory.Exists(fullPath))
                return false;

            var total = 0L;
            foreach (var file in new DirectoryInfo(fullPath).EnumerateFiles("*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                total += file.Length;
                if (total > limit)
                    return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
