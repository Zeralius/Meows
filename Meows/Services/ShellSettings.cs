using System.Text.Json;

namespace Meows.Services;

/// <summary>
/// Everything we persist, under %APPDATA%\Meows, or beside the exe when a file called
/// <c>portable</c> sits there. Nothing is written into the repo.
/// </summary>
public sealed class ShellSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// A file called <c>portable</c> (or <c>portable.txt</c>, which is what Explorer makes) next
    /// to Meows.exe says the settings go in <c>data\</c> next to it too, so the unzipped folder
    /// is the whole installation and can be carried on a stick or kept out of a roaming
    /// profile. Read once; making the file takes effect at the next start.
    /// </summary>
    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    /// <summary>
    /// Set by the shell at startup to its settings root, for libraries the plugins carry that
    /// keep something beside the settings and cannot be handed a host.
    /// </summary>
    public const string RootVariable = "MEOWS_SETTINGS_ROOT";

    /// <summary>Where a real run keeps everything: the roaming profile, or beside the exe when portable.</summary>
    public static string DefaultRoot => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Meows");

    /// <summary>
    /// The root is injectable so tests can use a throwaway directory. The app always uses the
    /// default.
    /// </summary>
    public ShellSettings(string? root = null, string? previousRoot = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Root = root ?? DefaultRoot;

        // Only look at the real old folder on a real run, and only a roaming one: a portable
        // folder is deliberately its own world. A test with its own Root has to pass its own
        // previous folder too, or it would read the actual settings on this machine.
        var previous = previousRoot ?? (root is null && !IsPortable ? Path.Combine(appData, "Mews") : null);

        // Whether there are settings here, not whether the folder exists. CrashLog runs first
        // and creates the folder to write into, so an empty folder is normal on a first run.
        // Treating that as "already set up" skipped the migration entirely.
        if (previous is not null && !AlreadySettled(Root))
            CarryOverFromMews(previous, Root);

        Directory.CreateDirectory(Root);
        foreach (var (from, to) in RenamedPlugins)
            FollowRename(from, to);
    }

    public string Root { get; }

    /// <summary>
    /// Plugins that changed their id. An id is the settings folder and the activation record,
    /// so a rename without this would open the plugin switched off and on defaults.
    /// </summary>
    public static readonly IReadOnlyList<(string From, string To)> RenamedPlugins =
    [
        ("meows.kit", "meows.familiar"),
    ];

    /// <summary>Moves the old id's folder to the new name, once, and rewrites the activation list.</summary>
    private void FollowRename(string from, string to)
    {
        try
        {
            var old = Path.Combine(Root, "plugins", from);
            var current = Path.Combine(Root, "plugins", to);
            if (Directory.Exists(old) && !Directory.Exists(current))
                Directory.Move(old, current);

            if (File.Exists(ActivationFile))
            {
                var text = File.ReadAllText(ActivationFile);
                var renamed = text.Replace($"\"{from}\"", $"\"{to}\"");
                if (renamed != text)
                    File.WriteAllText(ActivationFile, renamed);
            }
        }
        catch (Exception)
        {
            // Worst case the plugin starts fresh and has to be ticked again.
        }
    }

    /// <summary>
    /// The activation list is stored as plugin ids, and the ids contain the app name. Copying
    /// the file across unchanged would leave every plugin switched off.
    /// </summary>
    private static void RenameIdsIn(string file)
    {
        try
        {
            if (!File.Exists(file))
                return;

            var text = File.ReadAllText(file);
            var renamed = text.Replace("\"mews.", "\"meows.");

            if (renamed != text)
                File.WriteAllText(file, renamed);
        }
        catch (Exception)
        {
            // Worst case they re-tick their plugins. Not worth failing the migration over.
        }
    }

    /// <summary>Whether this folder holds settings, as opposed to just existing.</summary>
    private static bool AlreadySettled(string root) =>
        File.Exists(Path.Combine(root, "activated-plugins.json")) ||
        Directory.Exists(Path.Combine(root, "plugins"));

    /// <summary>
    /// What happened during the migration, if anything. Set in the constructor and read once
    /// the log exists, which is later.
    /// </summary>
    public string? StartupNote { get; private set; }

    /// <summary>
    /// The app was called Mews until 1.0.0 and its settings lived under that name. Brings the
    /// old folder across on first run, rather than starting empty and looking like everything
    /// was lost.
    ///
    /// Copies rather than moves, so the original is still there if this goes wrong.
    /// </summary>
    private void CarryOverFromMews(string old, string wanted)
    {
        try
        {
            if (!Directory.Exists(old))
                return;

            // Created up front: an old folder with files but no subfolders makes nothing in the
            // loop below, and every copy then fails for want of a destination.
            Directory.CreateDirectory(wanted);

            foreach (var directory in Directory.GetDirectories(old, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(directory.Replace(old, wanted));

            foreach (var file in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
            {
                // The old log belongs to the old name and is not settings. Copying it would
                // leave two logs, neither of them complete.
                if (Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(file, file.Replace(old, wanted), overwrite: false);
            }

            // Plugin ids changed with the app name, and each folder is named after its id.
            var plugins = Path.Combine(wanted, "plugins");
            if (Directory.Exists(plugins))
            {
                foreach (var directory in Directory.GetDirectories(plugins, "mews.*"))
                {
                    var renamed = Path.Combine(plugins, "meows." + Path.GetFileName(directory)["mews.".Length..]);
                    if (!Directory.Exists(renamed))
                        Directory.Move(directory, renamed);
                }
            }

            RenameIdsIn(Path.Combine(wanted, "activated-plugins.json"));

            StartupNote = $"Settings were carried over from {old}, which is where they lived when " +
                          "this was called Mews. The old folder has been left exactly as it was.";
        }
        catch (Exception ex)
        {
            StartupNote = $"Could not carry settings over from {old}: {ex.Message}. " +
                          "Nothing was changed there, so it can be copied across by hand.";
        }
    }

    /// <summary>
    /// Where to report a file that could not be read. Wired up to the shell log after this is
    /// constructed, hence a property rather than a constructor argument.
    /// </summary>
    public Action<string>? Report { get; set; }

    private string ActivationFile => Path.Combine(Root, "activated-plugins.json");

    private string PreferencesFile => Path.Combine(Root, "preferences.json");

    /// <summary>
    /// How the window should look and read. Kept apart from the plugin activation list because
    /// it is read before any plugin exists: the theme has to be on the first frame, not after
    /// the catalogue has been walked.
    /// </summary>
    public ShellPreferences LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesFile))
                return new ShellPreferences();

            return JsonSerializer.Deserialize<ShellPreferences>(File.ReadAllText(PreferencesFile), Json)
                   ?? new ShellPreferences();
        }
        catch (Exception ex)
        {
            SetAside(PreferencesFile, ex);
            return new ShellPreferences();
        }
    }

    public void SavePreferences(ShellPreferences preferences)
    {
        try
        {
            WriteWholly(PreferencesFile, JsonSerializer.Serialize(preferences, Json));
        }
        catch (Exception ex)
        {
            Report?.Invoke($"Could not save your settings: {ex.Message}");
        }
    }

    public string PluginDataDirectory(string pluginId)
    {
        var safe = string.Concat(pluginId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(Root, "plugins", safe);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public HashSet<string> LoadActivatedPlugins()
    {
        try
        {
            if (!File.Exists(ActivationFile))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(ActivationFile), Json);
            return new HashSet<string>(ids ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            SetAside(ActivationFile, ex);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveActivatedPlugins(IEnumerable<string> ids)
    {
        try
        {
            WriteWholly(ActivationFile, JsonSerializer.Serialize(ids.ToArray(), Json));
        }
        catch (Exception ex)
        {
            // Worst case they re-tick a plugin. Not worth a dialog, but worth logging.
            Report?.Invoke($"Could not save which plugins are active: {ex.Message}");
        }
    }

    public T? LoadPluginSettings<T>(string pluginId) where T : class
    {
        var file = Path.Combine(PluginDataDirectory(pluginId), "settings.json");
        if (!File.Exists(file))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json);
        }
        catch (Exception ex)
        {
            SetAside(file, ex);
            return null;
        }
    }

    public void SavePluginSettings<T>(string pluginId, T settings) where T : class
    {
        var file = Path.Combine(PluginDataDirectory(pluginId), "settings.json");
        WriteWholly(file, JsonSerializer.Serialize(settings, Json));
    }

    /// <summary>
    /// Renames a file we could not read, and logs it.
    ///
    /// Returning null on a parse failure meant the plugin started on defaults, and the next
    /// setting anyone changed wrote those defaults over the file. One bad byte lost everything,
    /// silently. Renaming first keeps the original around to look at.
    /// </summary>
    private void SetAside(string file, Exception ex)
    {
        var moved = $"{file}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";

        try
        {
            File.Move(file, moved);
            Report?.Invoke($"{Path.GetFileName(file)} could not be read ({ex.Message}). " +
                           $"It has been kept as {Path.GetFileName(moved)} and defaults are being used.");
        }
        catch (Exception moveFailed)
        {
            // Could not even rename it, so leave it alone. Overwriting an unreadable file is
            // what this method exists to prevent.
            Report?.Invoke($"{Path.GetFileName(file)} could not be read ({ex.Message}) " +
                           $"and could not be set aside ({moveFailed.Message}).");
        }
    }

    /// <summary>
    /// Writes via a temporary file so the real one holds either the old contents or the new
    /// ones, never half of each. A write interrupted by a crash is how you get the unreadable
    /// file the method above has to deal with.
    /// </summary>
    private static void WriteWholly(string file, string contents)
    {
        var temporary = file + ".writing";
        File.WriteAllText(temporary, contents);

        if (File.Exists(file))
            File.Replace(temporary, file, destinationBackupFileName: null);
        else
            File.Move(temporary, file);
    }
}

/// <summary>
/// The two choices on the Settings tab. Both default to following the machine, so a first run
/// looks like the rest of the desktop rather than like whatever we happened to prefer.
/// </summary>
/// <summary>Where a window sat, in screen pixels, and whether it was maximised over that.</summary>
public sealed record WindowPlace(int X, int Y, double Width, double Height, bool Maximized = false);

/// <summary>
/// The monitors as one string: each one's bounds, in order. Plug a screen in or out and it is
/// a different layout, with places of its own, so a window remembered on the second screen goes
/// back there when that screen is there and lands somewhere sensible when it is not.
/// </summary>
public static class WindowLayout
{
    public static string? Of(Avalonia.Controls.Window? window)
    {
        try
        {
            var screens = window?.Screens.All;
            if (screens is null || screens.Count == 0)
                return null;
            return string.Join("|", screens.Select(s => $"{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The window's place now, or null when it is not somewhere worth keeping.</summary>
    public static WindowPlace? PlaceOf(Avalonia.Controls.Window window)
    {
        if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
            return null;
        var maximized = window.WindowState == Avalonia.Controls.WindowState.Maximized;
        return new WindowPlace(window.Position.X, window.Position.Y, window.Width, window.Height, maximized);
    }

    /// <summary>
    /// Puts the window where the place says, before it is shown. The size and the maximised
    /// state always; the position only when enough of the window would land on a screen that
    /// is there, since a layout string cannot tell a DPI change or a screen that moved from one
    /// that did not, and a window remembered off the edge is a window nobody can reach.
    /// </summary>
    public static void Apply(Avalonia.Controls.Window window, WindowPlace place)
    {
        if (place.Width > 0 && place.Height > 0)
        {
            window.Width = place.Width;
            window.Height = place.Height;
        }

        IReadOnlyList<Avalonia.PixelRect> screens;
        try
        {
            screens = window.Screens.All.Select(s => s.WorkingArea).ToList();
        }
        catch (Exception)
        {
            screens = [];
        }

        if (Fits(place, screens))
        {
            window.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual;
            window.Position = new Avalonia.PixelPoint(place.X, place.Y);
        }

        if (place.Maximized)
            window.WindowState = Avalonia.Controls.WindowState.Maximized;
    }

    /// <summary>
    /// Whether a window at this place shows enough of itself on one of these screens to be
    /// grabbed: its title bar's left corner, plus a hand's width of it, inside a working area.
    /// No screens at all means nobody can say, and the place is taken as it is.
    /// </summary>
    public static bool Fits(WindowPlace place, IReadOnlyList<Avalonia.PixelRect> screens)
    {
        if (screens.Count == 0)
            return true;

        const int grab = 120;
        var width = (int)Math.Max(place.Width, grab);
        var height = (int)Math.Max(place.Height, 40);
        var window = new Avalonia.PixelRect(place.X, place.Y, width, height);

        foreach (var screen in screens)
        {
            var seen = screen.Intersect(window);
            if (seen.Width >= grab && seen.Height >= 40 && seen.Y == window.Y)
                return true;
        }

        return false;
    }
}

public sealed class ShellPreferences
{
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>"system", or a two letter language code the shell ships.</summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// Closing the window hides it to the tray and Meows keeps running. On by default, since
    /// that is the whole point of having a tray icon; off makes the close button a quit again.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>When Windows starts Meows at login, start in the tray rather than with the window open.</summary>
    public bool StartInTray { get; set; }

    /// <summary>Purrge and Chonk, or Duplicates and Disk usage. On, because it is the app's character.</summary>
    public bool FelineNames { get; set; } = true;

    /// <summary>
    /// How long a line in the history is kept, in days, before the shell forgets it on its own.
    /// Zero keeps everything forever, which is the default and what the store did before this
    /// existed. Facts and the seen table are never subject to this.
    /// </summary>
    public int HistoryKeepDays { get; set; }

    /// <summary>
    /// When the window was last hidden or Meows last quit, which is what "since you were away"
    /// on the Home tab counts from. Null until the first time.
    /// </summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>The tabs that were in windows of their own when Meows last quit, by tab key.</summary>
    public List<string> PoppedOutTabs { get; set; } = [];

    /// <summary>
    /// Where each popped-out tab's window sat, per monitor layout: the outer key is the layout
    /// as a string of every screen's bounds, the inner one the tab key.
    /// </summary>
    public Dictionary<string, Dictionary<string, WindowPlace>> PopOutPlaces { get; set; } = [];

    /// <summary>Where the main window sat, per monitor layout, the same way.</summary>
    public Dictionary<string, WindowPlace> MainWindowPlaces { get; set; } = [];

    /// <summary>
    /// Per log source, the least a line has to be to show: "warning" for warnings and errors
    /// only, "quiet" for nothing. A source not listed shows everything. The file keeps it all
    /// regardless; this is about the pane and the Log tab.
    /// </summary>
    public Dictionary<string, string> LogLevels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How big the tab strip is drawn: "compact", "normal" or "large". Twenty-odd tabs at the
    /// normal size are small to hit, and a strip that wraps anyway has room to be taller.
    /// </summary>
    public string TabSize { get; set; } = TabSizes.Normal;

    /// <summary>
    /// The groups on the tab strip, in the order they are shown. Only groups somebody has
    /// touched are in here: the rest are worked out from what each plugin says its category is,
    /// so a fresh install is grouped correctly without anything being written down.
    /// </summary>
    public List<TabGroupSetting> TabGroups { get; set; } = [];

    /// <summary>
    /// Tabs that were moved out of the group they declared, by tab key. A tab in here is in the
    /// group named here whatever its plugin says.
    /// </summary>
    public Dictionary<string, string> TabGroupOf { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The order of the tabs themselves, by tab key, across every group. Anything not in here
    /// keeps the order it was switched on in, at the end.
    /// </summary>
    public List<string> TabOrder { get; set; } = [];
}

/// <summary>One group on the tab strip, as it is remembered.</summary>
public sealed class TabGroupSetting
{
    /// <summary>The category key: <c>group.disk</c>, the shell's own, or one somebody made.</summary>
    public string Key { get; set; } = "";

    /// <summary>What it was renamed to, or null to read the key through the string table.</summary>
    public string? Name { get; set; }

    /// <summary>A name from the palette: slate, blue, green, amber, rose, violet, teal.</summary>
    public string Colour { get; set; } = "slate";

    /// <summary>Shut, so its tabs are behind one chip.</summary>
    public bool Collapsed { get; set; }
}
