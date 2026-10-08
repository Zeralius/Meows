using System.IO.Compression;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Vet.Services;

/// <summary>One drive, as the checkup saw it.</summary>
public sealed class DriveRow
{
    public string Name { get; set; } = "";

    public long TotalBytes { get; set; }

    public long FreeBytes { get; set; }

    /// <summary>Red when Windows would show red (under a tenth free) or under ten gigabytes whatever the size.</summary>
    public bool IsLow => TotalBytes > 0 && (FreeBytes < TotalBytes / 10 || FreeBytes < 10L * 1024 * 1024 * 1024);
}

/// <summary>One checkup, as kept: enough for the Home line with no fresh reading.</summary>
public sealed class CheckupSummary
{
    public DateTime AtUtc { get; set; }

    public int Drives { get; set; }

    public List<string> LowDrives { get; set; } = [];

    public bool? RebootNeeded { get; set; }

    public int? BackupDays { get; set; }

    public bool BackupStale { get; set; }
}

/// <summary>The verdict on one checkup: what wants doing, if anything.</summary>
public sealed record Verdict(bool IsTrouble, IReadOnlyList<string> Parts);

/// <summary>
/// The family PC health check: room on the drives, whether Windows wants a reboot, and how old
/// the newest file in the backup folder is. Reading only, always; the only thing Vet writes is
/// the diagnostic zip it is asked for.
/// </summary>
/// <summary>
/// Where a checkup reads the machine: the drives and the reboot key. The real one in the app; a
/// test hands in drives of its own, so what it asserts does not depend on how full this PC is.
/// </summary>
public sealed record MachineReadings(Func<List<DriveRow>> Drives, Func<bool?> RebootNeeded)
{
    public static MachineReadings Real { get; } = new(Checkup.Drives, Checkup.RebootNeeded);
}

public static class Checkup
{
    /// <summary>A backup older than this many days counts as stale.</summary>
    public const int DefaultWarnDays = 7;

    /// <summary>Every fixed drive that is ready, or an empty list where none can be asked.</summary>
    public static List<DriveRow> Drives()
    {
        var rows = new List<DriveRow>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            return rows;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;
                rows.Add(new DriveRow
                {
                    Name = drive.Name,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                });
            }
            catch (Exception)
            {
                // A drive that cannot be asked is not a row.
            }
        }
        return rows;
    }

    /// <summary>
    /// Whether Windows is waiting for a reboot to finish updates: the Update key, or the
    /// servicing key. Null anywhere but Windows, where there is no such key to read.
    /// </summary>
    public static bool? RebootNeeded()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var update = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (update is not null)
                return true;
            using var servicing = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return servicing is not null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// How old the newest file in the backup folder is, in whole days. Null when the folder is
    /// not set, is gone, or holds nothing. The folder itself is never written to.
    /// </summary>
    public static int? BackupDays(string? folder, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        DateTime newest;
        try
        {
            if (!Directory.Exists(folder))
                return null;
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f =>
                {
                    try
                    {
                        return (DateTime?)File.GetLastWriteTimeUtc(f);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                })
                .Where(t => t.HasValue)
                .ToList();
            if (files.Count == 0)
                return null;
            newest = files.Max()!.Value;
        }
        catch (Exception)
        {
            return null;
        }

        var days = (int)(nowUtc.Date - newest.Date).TotalDays;
        return Math.Max(0, days);
    }

    public static CheckupSummary Run(string? backupFolder, DateTime nowUtc) =>
        Run(backupFolder, nowUtc, Drives(), RebootNeeded());

    /// <summary>One checkup from drives and a reboot answer already read, so nothing is read twice.</summary>
    public static CheckupSummary Run(string? backupFolder, DateTime nowUtc, List<DriveRow> drives, bool? rebootNeeded)
    {
        return new CheckupSummary
        {
            AtUtc = nowUtc,
            Drives = drives.Count,
            LowDrives = drives.Where(d => d.IsLow).Select(d => d.Name).ToList(),
            RebootNeeded = rebootNeeded,
            BackupDays = BackupDays(backupFolder, nowUtc),
            BackupStale = false,
        };
    }

    /// <summary>What one checkup means, in words: healthy, or the list of what wants doing.</summary>
    public static Verdict Judge(CheckupSummary summary, bool backupSet, int warnDays, IMeowsText text)
    {
        var parts = new List<string>();
        foreach (var drive in summary.LowDrives)
            parts.Add(text.Format("vet.low", drive));
        if (summary.RebootNeeded == true)
            parts.Add(text["vet.reboot.needed"]);
        if (backupSet && summary.BackupDays is null)
            parts.Add(text["vet.backup.unknown"]);
        else if (summary.BackupDays is { } days && days > warnDays)
            parts.Add(text.Format("vet.backup.stale", days));

        return parts.Count == 0
            ? new Verdict(false, [text.Format("vet.healthy", summary.Drives)])
            : new Verdict(true, parts);
    }

    /// <summary>The line under the header from the last checkup.</summary>
    public static string SummaryOf(CheckupSummary? summary, bool backupSet, int warnDays, IMeowsText text)
    {
        if (summary is null)
            return "";
        return string.Join(" · ", Judge(summary, backupSet, warnDays, text).Parts);
    }

    /// <summary>The Home line from the last checkup. Red while anything wants doing.</summary>
    public static Glance? GlanceOf(CheckupSummary? summary, bool backupSet, int warnDays, IMeowsText text)
    {
        if (summary is null)
            return null;
        var judged = Judge(summary, backupSet, warnDays, text);
        return new Glance(string.Join(" · ", judged.Parts), judged.IsTrouble);
    }

    /// <summary>
    /// The diagnostic zip for whoever helps: what the checkup saw, in one text file. The one
    /// file Vet ever writes, only when asked, only where pointed.
    /// </summary>
    public static void WriteDiagnostics(string zipPath, CheckupSummary summary, bool backupSet, int warnDays,
        IReadOnlyList<DriveRow> drives, IMeowsText text)
    {
        var lines = new List<string>
        {
            $"Meows Vet — {summary.AtUtc:yyyy-MM-dd HH:mm} UTC",
            "",
            string.Join(" · ", Judge(summary, backupSet, warnDays, text).Parts),
            "",
        };
        foreach (var drive in drives)
            lines.Add($"{drive.Name} free {Humanise(drive.FreeBytes)} of {Humanise(drive.TotalBytes)}");
        lines.Add($"{text["vet.reboot.needed"]}: {(summary.RebootNeeded is null ? "?" : summary.RebootNeeded.ToString())}");
        lines.Add($"Backup: {(summary.BackupDays is { } days ? days.ToString() : "?")}");

        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (directory is not null)
            Directory.CreateDirectory(directory);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("checkup.txt");
        using var writer = new StreamWriter(entry.Open());
        foreach (var line in lines)
            writer.WriteLine(line);
    }

    /// <summary>Bytes as a person would say them, in big text.</summary>
    public static string Humanise(long bytes) =>
        bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        };
}
