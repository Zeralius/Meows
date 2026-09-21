using Meows.Disk;

namespace Meows.Plugins.Rehome.Services;

/// <summary>Where a folder sits, which is most of what it is.</summary>
public enum FolderKind { Library, Cloud, Profile, Roaming, Local, LocalLow, ProgramData, Root, Browser, Saves, Cache }

/// <summary>One folder the wipe would take, before it is measured. Survives says it sits on a drive not being wiped and is listed only so it is not missed.</summary>
public sealed record WipeCandidate(string Path, FolderKind Kind, string? Note, bool Survives = false)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
}

/// <summary>
/// The same folder, measured: what is in it and when it was last written. Bytes is what is on
/// the disk; CloudBytes is what the Skipped cloud-only files would add if pulled down.
/// </summary>
public sealed record MeasuredFolder(WipeCandidate Candidate, long Bytes, int Files, DateTime? Newest, int Unreadable, int Skipped, long CloudBytes = 0);

/// <summary>
/// The folders on the drives being wiped that are not a program and are on no other drive:
/// the profile, the three AppData trees, ProgramData, and whatever was made at the root because
/// it was there. Program Files and Windows are not on the list and cannot be added: a copied
/// install is a folder that does not run.
/// </summary>
public static class WipeList
{
    private static readonly string[] RootSkipped =
    [
        "Windows", "Program Files", "Program Files (x86)", "Users", "ProgramData", "$Recycle.Bin",
        "System Volume Information", "Recovery", "PerfLogs", "$WinREAgent", "$SysReset", "$Windows.~BT",
        "$Windows.~WS", "Documents and Settings", "Config.Msi", "OneDriveTemp", "MSOCache", "Boot", "EFI",
    ];

    private static readonly string[] ProgramDataSkipped =
    [
        "Microsoft", "Package Cache", "USOShared", "SoftwareDistribution", "Packages", "WindowsHolographicDevices",
        "regid.1991-06.com.microsoft", "ssh", "Microsoft OneDrive", "Application Data", "Desktop", "Documents",
        "Start Menu", "Templates", "Intel", "AMD", "NVIDIA Corporation", "NVIDIA", "Comms",
    ];

    private static readonly string[] LocalSkipped =
    [
        "Temp", "Microsoft", "Packages", "ConnectedDevicesPlatform", "Comms", "D3DSCache", "CrashDumps",
        "PeerDistRepub", "PlaceholderTileLogoFolder", "Publishers", "VirtualStore", "IconCache.db",
        "NVIDIA", "NVIDIA Corporation", "AMD", "Intel", "pip", "npm-cache", "Yarn", "NuGet", "Temporary Internet Files",
        "History", "Application Data", "ElevatedDiagnostics", "OneDrive", "SquirrelTemp",
    ];

    private static readonly string[] ProfileSkipped =
    [
        "AppData", "Application Data", "Cookies", "Local Settings", "NetHood", "PrintHood", "Recent", "SendTo",
        "Start Menu", "Templates", "My Documents", "ntuser.dat", "IntelGraphicsProfiles", "MicrosoftEdgeBackups",
    ];

    /// <summary>Folder names under the profile that rebuild themselves and are not worth carrying.</summary>
    private static readonly string[] ProfileCaches = [".nuget", ".gradle", ".m2", ".cache", ".cargo", ".rustup", ".npm", ".dotnet", ".android", ".templateengine", ".vs"];

    /// <summary>(relative to the profile, what browser) for the folders that do not come back whole.</summary>
    private static readonly (string Path, string Browser, bool KeepsPasswords)[] Browsers =
    [
        (@"AppData\Local\Google\Chrome\User Data", "Chrome", false),
        (@"AppData\Local\Microsoft\Edge\User Data", "Edge", false),
        (@"AppData\Local\BraveSoftware\Brave-Browser\User Data", "Brave", false),
        (@"AppData\Local\Vivaldi\User Data", "Vivaldi", false),
        (@"AppData\Roaming\Opera Software", "Opera", false),
        (@"AppData\Roaming\Mozilla\Firefox", "Firefox", true),
        (@"AppData\Roaming\Thunderbird", "Thunderbird", true),
    ];

    public static IReadOnlyList<WipeCandidate> Gather(IReadOnlyList<string> wipedDrives, Func<string, string> text)
    {
        var found = new List<WipeCandidate>();
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        // The person's own folders first, wherever Windows has them: a Documents moved to another
        // drive is still Documents, listed as surviving rather than left off.
        var library = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, name) in LibraryFolders())
        {
            if (!Directory.Exists(path) || !library.Add(path.TrimEnd('\\')))
                continue;
            var root = Path.GetPathRoot(path)?.TrimEnd('\\') ?? "";
            var survives = !wipedDrives.Any(d => string.Equals(d.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase));
            found.Add(new WipeCandidate(path, FolderKind.Library, survives ? string.Format(text("rehome.note.survives"), root) : null, survives));
        }

        foreach (var drive in wipedDrives)
        {
            var root = drive.TrimEnd('\\') + "\\";
            var isSystem = string.Equals(root, system, StringComparison.OrdinalIgnoreCase);
            foreach (var child in Children(root))
            {
                if (child.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || child.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (isSystem && RootSkipped.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                    continue;
                found.Add(new WipeCandidate(child.FullName, FolderKind.Root, isSystem ? text("rehome.note.root") : text("rehome.note.otherdrive")));
            }
        }

        if (!wipedDrives.Any(d => string.Equals(d.TrimEnd('\\') + "\\", system, StringComparison.OrdinalIgnoreCase)))
            return found;

        // OneDrive, in the group with the person's own folders: it is in the cloud too, but the
        // option to carry it whole is the person's, not the plugin's.
        foreach (var cloud in CloudRoots(profile))
        {
            if (!library.Add(cloud.TrimEnd('\\')))
                continue;
            var root = Path.GetPathRoot(cloud)?.TrimEnd('\\') ?? "";
            var survives = !wipedDrives.Any(d => string.Equals(d.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase));
            found.Add(new WipeCandidate(cloud, FolderKind.Cloud, survives ? string.Format(text("rehome.note.survives"), root) : text("rehome.note.onedrive"), survives));
        }

        // The profile: the folders a person actually put things in.
        foreach (var child in Children(profile))
        {
            if (ProfileSkipped.Contains(child.Name, StringComparer.OrdinalIgnoreCase) || library.Contains(child.FullName.TrimEnd('\\')))
                continue;
            var kind = FolderKind.Profile;
            string? note = null;
            if (ProfileCaches.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
            {
                kind = FolderKind.Cache;
                note = text("rehome.note.cache");
            }
            else if (child.Name.Equals("Saved Games", StringComparison.OrdinalIgnoreCase))
            {
                kind = FolderKind.Saves;
                note = text("rehome.note.saves");
            }
            else if (child.Name.Equals(".ssh", StringComparison.OrdinalIgnoreCase))
                note = text("rehome.note.ssh");
            found.Add(new WipeCandidate(child.FullName, kind, note));
        }

        // My Games is inside Documents, which is in the group above; a row of its own would carry it twice.
        var myGames = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
        if (Directory.Exists(myGames) && !library.Any(l => myGames.StartsWith(l + "\\", StringComparison.OrdinalIgnoreCase)))
            found.Add(new WipeCandidate(myGames, FolderKind.Saves, text("rehome.note.saves")));

        // The browsers, named as what they are rather than left as one more AppData row.
        var browserPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, browser, keepsPasswords) in Browsers)
        {
            var path = Path.Combine(profile, relative);
            if (!Directory.Exists(path))
                continue;
            browserPaths.Add(path);
            found.Add(new WipeCandidate(path, FolderKind.Browser, string.Format(text(keepsPasswords ? "rehome.note.browser.keeps" : "rehome.note.browser.loses"), browser)));
        }

        // The three AppData trees, one row per program.
        AddTree(Path.Combine(profile, "AppData", "Roaming"), FolderKind.Roaming, ["Microsoft", "Application Data", "Adobe\\Common"], browserPaths, found, text);
        AddTree(Path.Combine(profile, "AppData", "Local"), FolderKind.Local, LocalSkipped, browserPaths, found, text);
        AddTree(Path.Combine(profile, "AppData", "LocalLow"), FolderKind.LocalLow, ["Microsoft"], browserPaths, found, text);

        // ProgramData: shared configs, minus Windows' own.
        foreach (var child in Children(programData))
        {
            if (ProgramDataSkipped.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            found.Add(new WipeCandidate(child.FullName, FolderKind.ProgramData, null));
        }

        return found;
    }

    /// <summary>Desktop, Documents, Downloads, Pictures, Videos, Music: where Windows says they are, not where they usually are.</summary>
    public static IReadOnlyList<(string Path, string Name)> LibraryFolders()
    {
        var list = new List<(string, string)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Desktop"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
            (DownloadsFolder(), "Downloads"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videos"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Music"),
        };
        return list.Where(l => !string.IsNullOrEmpty(l.Item1)).ToList();
    }

    /// <summary>Every OneDrive root: the ones the environment names, and any "OneDrive - Company" folder under the profile.</summary>
    public static IReadOnlyList<string> CloudRoots(string profile)
    {
        var roots = new List<string>();
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value) && !roots.Contains(value.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase))
                roots.Add(value.TrimEnd('\\'));
        }
        foreach (var child in Children(profile))
        {
            if ((child.Name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) || child.Name.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase))
                && !roots.Contains(child.FullName.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase))
                roots.Add(child.FullName.TrimEnd('\\'));
        }
        return roots;
    }

    /// <summary>.NET has no special folder for Downloads; Windows keeps it under the known-folder GUID, or it is the default.</summary>
    private static string DownloadsFolder()
    {
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!OperatingSystem.IsWindows())
            return fallback;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string raw && raw.Length > 0)
                return Environment.ExpandEnvironmentVariables(raw);
        }
        catch (Exception)
        {
            // The default it is.
        }
        return fallback;
    }

    private static void AddTree(string tree, FolderKind kind, string[] skipped, HashSet<string> browserPaths, List<WipeCandidate> into, Func<string, string> text)
    {
        foreach (var child in Children(tree))
        {
            if (skipped.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            // A browser's parent folder (Google, Mozilla) would carry the profile twice.
            if (browserPaths.Any(b => b.StartsWith(child.FullName + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var sibling in Children(child.FullName))
                {
                    if (!browserPaths.Any(b => b.StartsWith(sibling.FullName, StringComparison.OrdinalIgnoreCase)))
                        into.Add(new WipeCandidate(sibling.FullName, kind, null));
                }
                continue;
            }
            var note = kind == FolderKind.LocalLow ? text("rehome.note.lowlow") : null;
            into.Add(new WipeCandidate(child.FullName, kind, note));
        }
    }

    private static IEnumerable<DirectoryInfo> Children(string path)
    {
        DirectoryInfo[] children;
        try
        {
            children = new DirectoryInfo(path).GetDirectories();
        }
        catch (Exception)
        {
            yield break;
        }
        foreach (var child in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            FileAttributes attributes;
            try
            {
                attributes = child.Attributes;
            }
            catch (Exception)
            {
                continue;
            }
            // A junction is a path to somewhere else, and copying it would copy that somewhere twice.
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            yield return child;
        }
    }

    /// <summary>
    /// Sizes one candidate the way the copy will see it: every file under it, junctions and
    /// OneDrive placeholders skipped and counted, the newest write remembered.
    /// </summary>
    public static MeasuredFolder Measure(WipeCandidate candidate, CancellationToken token)
    {
        long bytes = 0;
        long cloudBytes = 0;
        var files = 0;
        var unreadable = 0;
        var skipped = 0;
        DateTime? newest = null;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(candidate.Path));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (!FolderWalk.CanRead(current))
            {
                unreadable++;
                continue;
            }
            foreach (var file in FolderWalk.Files(current))
            {
                try
                {
                    if (Placeholder(file))
                    {
                        // A placeholder knows its full size without holding a byte of it.
                        skipped++;
                        cloudBytes += file.Length;
                        continue;
                    }
                    bytes += file.Length;
                    files++;
                    if (newest is null || file.LastWriteTime > newest)
                        newest = file.LastWriteTime;
                }
                catch (Exception)
                {
                    unreadable++;
                }
            }
            foreach (var child in FolderWalk.Into(current, skipSystemFolders: false))
                pending.Push(child);
        }

        return new MeasuredFolder(candidate, bytes, files, newest, unreadable, skipped, cloudBytes);
    }

    /// <summary>
    /// A OneDrive file that is only in the cloud: reading it would pull it down to put it on a
    /// disk. Two attributes say so; a reparse point on a file is the third way it shows.
    /// </summary>
    public static bool Placeholder(FileInfo file)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        var a = file.Attributes;
        return a.HasFlag(FileAttributes.Offline) || a.HasFlag(recallOnOpen) || a.HasFlag(recallOnDataAccess) || a.HasFlag(FileAttributes.ReparsePoint);
    }
}
