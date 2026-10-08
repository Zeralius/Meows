using System.Diagnostics;
using System.Security.Cryptography;

namespace Meows.Disk;

/// <summary>Why a folder will not be carried, found before anything is written.</summary>
public enum CarryRefusal
{
    None,
    NotThere,

    /// <summary>It is already a junction or link, most likely one Carry left.</summary>
    IsLink,

    /// <summary>A drive's root, Windows, Program Files, ProgramData, Users or a profile itself.</summary>
    Protected,

    /// <summary>The destination is on the same drive, which would move nothing.</summary>
    SameDrive,

    /// <summary>A removable, network or optical drive, which will not be there every boot.</summary>
    NotFixed,

    /// <summary>Something is already where the copy would go.</summary>
    TargetThere,

    NoRoom,

    /// <summary>OneDrive's cloud-only files, which a copy would have to download and a junction would break.</summary>
    CloudOnly,

    /// <summary>A junction or link inside it, which a copy cannot carry faithfully.</summary>
    HoldsLinks,

    /// <summary>The destination is inside the folder, or the folder inside the destination.</summary>
    Overlaps,

    Unreadable,
}

/// <summary>A drive as far as Carry cares: which volume, what kind, how much room.</summary>
public sealed record CarryDrive(string Volume, DriveType Type, long Free);

/// <summary>What a carry would do and cost, or why it will not.</summary>
public sealed record CarryPlan(string Source, string Target, long Bytes, int Files, long? Free, CarryRefusal Refusal)
{
    public bool CanCarry => Refusal == CarryRefusal.None;
}

public sealed record CarryProgress(long Bytes, long Of, string Current);

/// <summary>How a carry or a bring-back went. <see cref="Left"/> is set when all went well but an old copy could not be fully removed.</summary>
public sealed record CarryOutcome(bool Done, string? Failure, long Bytes, int Files)
{
    public string? Left { get; init; }
}

/// <summary>
/// Carry: a folder moved to a drive with room, every file checked, and a junction left where it
/// was so every path that pointed at it still works. The most dangerous thing in Meows, because it
/// moves data rather than binning it, so every step can be taken back until the last, and the
/// last, removing the original, only happens once the junction is in place and resolves:
///
/// 1. Copy into a folder with a temporary name, hashing each file on the way and reading the copy
///    back. One mismatch and the copy is deleted.
/// 2. Measure the original again; if it changed while being copied, stop.
/// 3. Give the copy its real name, then rename the original aside. A rename fails when something
///    has a file open, which is the question "is it in use" answered without guessing.
/// 4. Put the junction where the original was, and check it leads to the copy.
/// 5. Only then delete the original.
/// </summary>
public static class FolderMove
{
    public const string PartSuffix = ".meows-carrying";
    public const string AsideSuffix = ".meows-carried";
    public const string ReturningSuffix = ".meows-returning";

    /// <summary>Room to spare on the destination beyond the folder itself.</summary>
    public const double Headroom = 1.05;

    /// <summary>The name of the folder copies go into on the destination drive.</summary>
    public const string Home = "Carried";

    /// <summary>The real answer for a path: its volume root, kind and free space.</summary>
    public static CarryDrive? DriveOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return null;
            var drive = new DriveInfo(root);
            return new CarryDrive(root, drive.DriveType, drive.IsReady ? drive.AvailableFreeSpace : 0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Where a folder goes on a destination drive: under Carried, with the source's own path
    /// below it, C:\Users\me\Videos becoming D:\Carried\C\Users\me\Videos. Nothing clashes, and
    /// the destination says where each thing came from.
    /// </summary>
    public static string TargetFor(string source, string destinationRoot)
    {
        var full = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full) ?? "";
        var letter = root.TrimEnd('\\', '/', ':');
        var rest = full[root.Length..].TrimStart('\\', '/');
        var under = Path.Combine(destinationRoot, Home);
        return letter.Length == 0 ? Path.Combine(under, rest) : Path.Combine(under, letter, rest);
    }

    /// <summary>
    /// Folders that are never carried: moving them under a junction is how a machine stops
    /// booting or an update stops installing. A folder inside one of them is fine; the folder
    /// itself is not.
    /// </summary>
    public static bool IsProtected(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full + Path.DirectorySeparatorChar, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase) ||
            full.Length <= (Path.GetPathRoot(full)?.Length ?? 0))
            return true;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string?[] guarded =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            profile,
            string.IsNullOrEmpty(profile) ? null : Path.GetDirectoryName(profile),
        ];
        return guarded.Any(g => !string.IsNullOrEmpty(g) &&
            string.Equals(g.TrimEnd(Path.DirectorySeparatorChar), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where a junction or link leads, or null when the path is an ordinary folder.</summary>
    public static string? LinkTarget(string path)
    {
        try
        {
            // Not Exists: a junction whose drive is gone does not exist as a folder, and is
            // exactly the one worth knowing about.
            var info = new DirectoryInfo(path);
            var target = info.LinkTarget;
            if (target is null)
                return null;
            if (target.StartsWith(@"\??\", StringComparison.Ordinal))
                target = target[4..];
            return Path.GetFullPath(target, Path.GetDirectoryName(info.FullName) ?? "");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>What carrying this folder to that destination would take, measured before anything is written.</summary>
    public static CarryPlan Plan(string source, string destinationRoot, Func<string, CarryDrive?>? drives = null, CancellationToken token = default)
    {
        drives ??= DriveOf;
        source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = TargetFor(source, destinationRoot);

        CarryPlan No(CarryRefusal why, long bytes = 0, int files = 0, long? free = null) => new(source, target, bytes, files, free, why);

        if (!Directory.Exists(source))
            return No(CarryRefusal.NotThere);
        if (LinkTarget(source) is not null)
            return No(CarryRefusal.IsLink);
        if (IsProtected(source))
            return No(CarryRefusal.Protected);

        var fullDestination = Path.GetFullPath(destinationRoot);
        if (Inside(fullDestination, source) || Inside(source, fullDestination))
            return No(CarryRefusal.Overlaps);

        var from = drives(source);
        var to = drives(fullDestination);
        if (to is null)
            return No(CarryRefusal.NotThere);
        if (to.Type != DriveType.Fixed)
            return No(CarryRefusal.NotFixed, free: to.Free);
        if (from is not null && string.Equals(from.Volume, to.Volume, StringComparison.OrdinalIgnoreCase))
            return No(CarryRefusal.SameDrive, free: to.Free);
        if (Directory.Exists(target) || File.Exists(target) || Directory.Exists(target + PartSuffix))
            return No(CarryRefusal.TargetThere, free: to.Free);

        var measured = Measure(source, token);
        if (measured.Refusal != CarryRefusal.None)
            return No(measured.Refusal, measured.Bytes, measured.Files, to.Free);
        if (measured.Bytes * Headroom > to.Free)
            return No(CarryRefusal.NoRoom, measured.Bytes, measured.Files, to.Free);

        return new CarryPlan(source, target, measured.Bytes, measured.Files, to.Free, CarryRefusal.None);
    }

    private static bool Inside(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase);

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static (long Bytes, int Files, CarryRefusal Refusal) Measure(string source, CancellationToken token)
    {
        long bytes = 0;
        var files = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(source));
        try
        {
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var folder = pending.Pop();
                foreach (var entry in folder.EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return (bytes, files, CarryRefusal.HoldsLinks);
                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child);
                        continue;
                    }
                    if (entry.Attributes.HasFlag(FileAttributes.Offline) || entry.Attributes.HasFlag(RecallOnOpen) || entry.Attributes.HasFlag(RecallOnDataAccess))
                        return (bytes, files, CarryRefusal.CloudOnly);
                    files++;
                    bytes += ((FileInfo)entry).Length;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (bytes, files, CarryRefusal.Unreadable);
        }
        return (bytes, files, CarryRefusal.None);
    }

    /// <summary>
    /// Carries the folder the plan describes. Every step before the last can be taken back, and
    /// is, when a later one fails; the outcome says which step stopped it.
    /// </summary>
    public static CarryOutcome Carry(CarryPlan plan, IProgress<CarryProgress>? progress, CancellationToken token,
        Func<string, string, bool>? makeLink = null)
    {
        makeLink ??= Junction.Create;
        if (!plan.CanCarry)
            return new CarryOutcome(false, plan.Refusal.ToString(), 0, 0);

        var source = plan.Source;
        var target = plan.Target;
        var part = target + PartSuffix;
        var aside = source + AsideSuffix;

        // 1. The copy, under a name that says it is unfinished.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var copied = CopyVerified(source, part, plan.Bytes, progress, token);
            if (copied is not null)
            {
                TryDelete(part);
                return new CarryOutcome(false, copied, 0, 0);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(part);
            return new CarryOutcome(false, ex.Message, 0, 0);
        }

        // 2. Nothing changed underneath while it was being copied.
        var again = Measure(source, token);
        if (again.Refusal != CarryRefusal.None || again.Bytes != plan.Bytes || again.Files != plan.Files)
        {
            TryDelete(part);
            return new CarryOutcome(false, "changed", 0, 0);
        }

        // 3. The copy takes its name; the original steps aside, which fails if anything holds it open.
        try
        {
            Directory.Move(part, target);
        }
        catch (Exception ex)
        {
            TryDelete(part);
            return new CarryOutcome(false, ex.Message, 0, 0);
        }

        try
        {
            Directory.Move(source, aside);
        }
        catch (Exception)
        {
            TryDelete(target);
            return new CarryOutcome(false, "inuse", 0, 0);
        }

        // 4. The junction, and proof it leads to the copy.
        var linked = false;
        try
        {
            linked = makeLink(source, target) && SameFolder(LinkTarget(source), target) && Directory.Exists(source) &&
                     Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Count(File.Exists) == plan.Files;
        }
        catch (Exception)
        {
            linked = false;
        }

        if (!linked)
        {
            RemoveLink(source);
            try
            {
                Directory.Move(aside, source);
                TryDelete(target);
            }
            catch (Exception)
            {
                // The original could not go back under its name. Both copies are kept, and the
                // outcome names where the original is, rather than deleting either.
                return new CarryOutcome(false, "link", 0, 0) { Left = aside };
            }
            return new CarryOutcome(false, "link", 0, 0);
        }

        // 5. Only now is the original removed.
        try
        {
            Directory.Delete(aside, recursive: true);
        }
        catch (Exception)
        {
            return new CarryOutcome(true, null, plan.Bytes, plan.Files) { Left = aside };
        }

        return new CarryOutcome(true, null, plan.Bytes, plan.Files);
    }

    /// <summary>
    /// The way back: the folder copied home next to the junction, checked, the junction removed
    /// and the copy given its name, and only then the carried copy deleted.
    /// </summary>
    public static CarryOutcome BringBack(string link, IProgress<CarryProgress>? progress, CancellationToken token, Func<string, CarryDrive?>? drives = null)
    {
        drives ??= DriveOf;
        var target = LinkTarget(link);
        if (target is null || !Directory.Exists(target))
            return new CarryOutcome(false, "notlinked", 0, 0);

        var measured = Measure(target, token);
        if (measured.Refusal != CarryRefusal.None)
            return new CarryOutcome(false, measured.Refusal.ToString(), 0, 0);
        if (drives(link) is { } home && measured.Bytes * Headroom > home.Free)
            return new CarryOutcome(false, nameof(CarryRefusal.NoRoom), 0, 0);

        var returning = link + ReturningSuffix;
        try
        {
            var copied = CopyVerified(target, returning, measured.Bytes, progress, token);
            if (copied is not null)
            {
                TryDelete(returning);
                return new CarryOutcome(false, copied, 0, 0);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(returning);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(returning);
            return new CarryOutcome(false, ex.Message, 0, 0);
        }

        if (!RemoveLink(link))
        {
            TryDelete(returning);
            return new CarryOutcome(false, "link", 0, 0);
        }

        try
        {
            Directory.Move(returning, link);
        }
        catch (Exception ex)
        {
            // The junction is gone and the copy home could not take its name: put the junction
            // back so nothing that pointed here breaks, and keep both copies.
            Junction.Create(link, target);
            return new CarryOutcome(false, ex.Message, 0, 0) { Left = returning };
        }

        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch (Exception)
        {
            return new CarryOutcome(true, null, measured.Bytes, measured.Files) { Left = target };
        }
        return new CarryOutcome(true, null, measured.Bytes, measured.Files);
    }

    /// <summary>Removes a junction or link and never what it leads to.</summary>
    private static bool RemoveLink(string path)
    {
        try
        {
            if (LinkTarget(path) is null)
                return !Directory.Exists(path);
            Directory.Delete(path, recursive: false);
            return !Directory.Exists(path) && !File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SameFolder(string? a, string b) =>
        a is not null && string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && LinkTarget(folder) is null)
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Copies a tree, every file hashed as it is read and the copy read back and compared.
    /// Empty folders, dates and attributes come too. Null when every file arrived; otherwise the
    /// first thing that went wrong.
    /// </summary>
    private static string? CopyVerified(string from, string to, long of, IProgress<CarryProgress>? progress, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        long done = 0;
        var pending = new Stack<(DirectoryInfo From, string To)>();
        var folders = new List<(DirectoryInfo From, string To)>();
        pending.Push((new DirectoryInfo(from), to));

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (source, destination) = pending.Pop();
            Directory.CreateDirectory(destination);
            folders.Add((source, destination));

            foreach (var entry in source.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo child)
                {
                    pending.Push((child, Path.Combine(destination, child.Name)));
                    continue;
                }

                var file = (FileInfo)entry;
                var copy = Path.Combine(destination, file.Name);
                if (!CopyOne(file, copy, buffer, token))
                    return file.FullName;
                done += file.Length;
                progress?.Report(new CarryProgress(done, of, file.FullName));
            }
        }

        // Folder dates last, since writing a file into a folder moves the folder's own date.
        foreach (var (source, destination) in folders)
        {
            try
            {
                Directory.SetLastWriteTimeUtc(destination, source.LastWriteTimeUtc);
                Directory.SetCreationTimeUtc(destination, source.CreationTimeUtc);
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private static bool CopyOne(FileInfo file, string target, byte[] buffer, CancellationToken token)
    {
        string sourceHash;
        using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            sourceHash = Convert.ToHexString(hash.GetHashAndReset());
        }

        try
        {
            File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
            File.SetCreationTimeUtc(target, file.CreationTimeUtc);
            File.SetAttributes(target, file.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
        }
        catch (Exception)
        {
            // The bytes matter more than the stamps.
        }

        using var check = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length);
        using var again = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int n;
        while ((n = check.Read(buffer, 0, buffer.Length)) > 0)
            again.AppendData(buffer, 0, n);
        return Convert.ToHexString(again.GetHashAndReset()) == sourceHash;
    }
}

/// <summary>
/// A directory junction: what Windows' own mklink /J makes, which needs no administrator and is
/// followed by everything that opens a path. Elsewhere, where there are no junctions, a symbolic
/// link stands in, which is what lets the moves be tested off Windows.
/// </summary>
public static class Junction
{
    public static bool Create(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }

            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(link);
            start.ArgumentList.Add(target);
            using var process = Process.Start(start);
            if (process is null)
                return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
