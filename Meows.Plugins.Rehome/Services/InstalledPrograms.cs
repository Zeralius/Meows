using System.Text.RegularExpressions;
using Meows.Disk;
using Microsoft.Win32;

namespace Meows.Plugins.Rehome.Services;

/// <summary>One row of the uninstall registry, as the Windows "Apps" list would show it.</summary>
public sealed record InstalledProgram(
    string Name,
    string? Publisher,
    string? Version,
    DateTime? InstalledOn,
    string? Location,
    string? Url,
    bool PerUser,
    string KeyName = "",
    string? UninstallString = null)
{
    /// <summary>"C:" for a program whose install folder is known, else null: most per-user installs say nothing and sit on the Windows drive.</summary>
    public string? Drive
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Location))
                return null;
            try
            {
                var root = System.IO.Path.GetPathRoot(Location);
                return string.IsNullOrEmpty(root) || root.StartsWith(@"\\") ? null : root.TrimEnd('\\');
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Whether the wipe takes it: its files are on a drive being formatted, or it did not say
    /// where it lives, which means the Windows drive. A program elsewhere keeps its files and
    /// loses only its registry entry.
    /// </summary>
    public bool OnWipedDrive(IReadOnlyList<string> wipedDrives) =>
        Drive is not { } drive || wipedDrives.Any(d => string.Equals(d.TrimEnd('\\'), drive, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A game a launcher installed, which the launcher puts back after a sign-in: Steam names
    /// its keys "Steam App 12345", the others say so in the uninstall command.
    /// </summary>
    public string? Launcher
    {
        get
        {
            var uninstall = UninstallString ?? "";
            if (KeyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) || uninstall.Contains("steam://", StringComparison.OrdinalIgnoreCase))
                return "Steam";
            if (uninstall.Contains("GOG Galaxy", StringComparison.OrdinalIgnoreCase) || (KeyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) && (Publisher ?? "").Contains("GOG", StringComparison.OrdinalIgnoreCase)))
                return "GOG";
            if (uninstall.Contains("Uplay", StringComparison.OrdinalIgnoreCase) || uninstall.Contains("Ubisoft Game Launcher", StringComparison.OrdinalIgnoreCase) || KeyName.StartsWith("Uplay Install", StringComparison.OrdinalIgnoreCase))
                return "Ubisoft Connect";
            if (uninstall.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase) || uninstall.Contains(@"Origin\", StringComparison.OrdinalIgnoreCase) || uninstall.Contains("EA Desktop", StringComparison.OrdinalIgnoreCase))
                return "EA";
            if (uninstall.Contains("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase) || uninstall.Contains("Epic Games", StringComparison.OrdinalIgnoreCase))
                return "Epic Games";
            if (uninstall.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) || uninstall.Contains("Blizzard", StringComparison.OrdinalIgnoreCase))
                return "Battle.net";
            if ((Location ?? "").Contains(@"\XboxGames\", StringComparison.OrdinalIgnoreCase))
                return "Xbox";
            return null;
        }
    }

    /// <summary>The name with the version and the "(x64)" the installer pinned on, for matching against a package manager.</summary>
    public string BareName => Normalise(Name);

    public static string Normalise(string name)
    {
        // "7-Zip 24.08 (x64 edition)", "Python 3.12.4 (64-bit)", "Notepad++ (64-bit x64)"
        var s = Bracketed.Replace(name, " ");
        s = Bitness.Replace(s, " ");
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !VersionLike.IsMatch(w))
            .Select(w => w.Trim('(', ')', ','))
            .Where(w => w.Length > 0);
        return string.Join(' ', words).Trim();
    }

    /// <summary>A bracket that says which build it is, not which program.</summary>
    private static readonly Regex Bracketed = new(@"\((?=[^)]*(?:x64|x86|bit|edition|64|32))[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Bitness = new(@"(?<![\w-])(x64|x86|64-bit|32-bit|64bit|32bit|amd64|arm64)(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"24.08", "3.12.4", "v2", "2024": a token that is a version, not a name.</summary>
    private static readonly Regex VersionLike = new(@"^v?\d+([.,]\d+)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

/// <summary>
/// The uninstall registry, both hives and both views, which is the list Windows itself shows
/// under Apps. Read rather than trusted: the install date and location are often missing and
/// the size is regularly wrong, so the size is not read at all.
/// </summary>
public static class InstalledPrograms
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledProgram> Read()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var found = new List<InstalledProgram>();
        ReadHive(Registry.LocalMachine, UninstallPath, perUser: false, found);
        ReadHive(Registry.LocalMachine, Uninstall32Path, perUser: false, found);
        ReadHive(Registry.CurrentUser, UninstallPath, perUser: true, found);
        ReadHive(Registry.CurrentUser, Uninstall32Path, perUser: true, found);

        // The same program can sit in both views; one row is enough.
        return found
            .GroupBy(p => (p.Name.ToLowerInvariant(), p.Version ?? ""))
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadHive(RegistryKey hive, string path, bool perUser, List<InstalledProgram> into)
    {
        if (!OperatingSystem.IsWindows())
            return;
        RegistryKey? root;
        try
        {
            root = hive.OpenSubKey(path);
        }
        catch (Exception)
        {
            return;
        }
        if (root is null)
            return;

        using (root)
        {
            foreach (var name in SafeSubKeys(root))
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key is null)
                        continue;
                    var program = ReadOne(key, name, perUser);
                    if (program is not null)
                        into.Add(program);
                }
                catch (Exception)
                {
                    // One unreadable key is not a reason to lose the list.
                }
            }
        }
    }

    private static string[] SafeSubKeys(RegistryKey key)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// The rules Windows uses for its own list: a display name, not a system component, not a
    /// patch, and not a row whose parent already stands for it.
    /// </summary>
    private static InstalledProgram? ReadOne(RegistryKey key, string keyName, bool perUser)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        if (key.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display))
            return null;
        if (key.GetValue("SystemComponent") is int component && component == 1)
            return null;
        if (key.GetValue("ParentKeyName") is string parent && parent.Length > 0)
            return null;
        if (key.GetValue("ReleaseType") is string release && release.Contains("Update", StringComparison.OrdinalIgnoreCase))
            return null;
        if (display.StartsWith("KB", StringComparison.OrdinalIgnoreCase) && display.Length > 2 && char.IsDigit(display[2]))
            return null;

        return new InstalledProgram(
            display.Trim(),
            key.GetValue("Publisher") as string,
            key.GetValue("DisplayVersion") as string,
            ParseDate(key.GetValue("InstallDate") as string),
            LocationOf(key, keyName),
            key.GetValue("URLInfoAbout") as string ?? key.GetValue("HelpLink") as string,
            perUser,
            keyName,
            key.GetValue("UninstallString") as string);
    }

    /// <summary>
    /// Where it is installed: what the key says, else Steam's own record for a Steam game (the
    /// registry often leaves those blank), else the folder of the program's icon when the icon
    /// is its exe, which is how most installers set it.
    /// </summary>
    private static string? LocationOf(RegistryKey key, string keyName)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var location = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(location))
            return location;

        if (keyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) && SteamLibrary.InstallFolderOf(keyName["Steam App ".Length..].Trim()) is { } steam)
            return steam;

        var icon = (key.GetValue("DisplayIcon") as string)?.Trim().Trim('"');
        if (icon is not null && icon.IndexOf(',') is var comma && comma > 0)
            icon = icon[..comma];
        if (icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return FolderOf(icon);

        // Last: the uninstaller's own folder, when it is the program's and not Windows' or msiexec.
        var uninstall = (key.GetValue("UninstallString") as string)?.Trim();
        if (uninstall is { Length: > 0 })
        {
            var exe = uninstall.StartsWith('"') && uninstall.IndexOf('"', 1) is var close && close > 1
                ? uninstall[1..close]
                : uninstall.Split(' ', 2)[0];
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(exe) && !exe.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                return FolderOf(exe);
        }
        return null;
    }

    private static string? FolderOf(string file)
    {
        try
        {
            return Path.GetDirectoryName(file);
        }
        catch (Exception)
        {
            // A path that is not a path.
            return null;
        }
    }

    /// <summary>"20240811", which is what installers write, or nothing.</summary>
    private static DateTime? ParseDate(string? raw)
    {
        if (raw is null || raw.Length != 8)
            return null;
        return DateTime.TryParseExact(raw, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }
}
