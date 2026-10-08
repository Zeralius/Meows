using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Larder.ViewModels;

public sealed class LarderSettings
{
    /// <summary>Libraries added by hand, for a Steam the registry does not know about or a drive that moved.</summary>
    public List<string> ExtraLibraries { get; set; } = [];

    public int SortIndex { get; set; }

    public int FilterIndex { get; set; }
}

/// <summary>One game, as the list shows it.</summary>
public sealed class GameViewModel(SteamInstall game) : ObservableObject
{
    public SteamInstall Game { get; } = game;

    public string Name => Game.Name;

    public string SizeText => FolderSize.Humanise(Game.SizeOnDisk);

    /// <summary>Never, unknown, or how long ago. Unknown is never passed off as never.</summary>
    public string PlayedText => Game.NeverPlayed
        ? MeowsText.Current["larder.played.never"]
        : Game.PlayedUnknown
            ? MeowsText.Current["larder.played.unknown"]
            : LarderViewModel.Ago(Game.LastPlayed!.Value.ToLocalTime(), DateTime.Now);

    public bool IsNeverPlayed => Game.NeverPlayed;

    public bool IsUnknown => Game.PlayedUnknown;

    public string Library => Game.Library;

    internal void Reread() => OnEverythingChanged();
}

/// <summary>One library: how many games, what they hold, and what the drive has left.</summary>
public sealed record LibraryLine(string Path, int Games, long Bytes, int Never, long NeverBytes, long? Free)
{
    public string Text => MeowsText.Current.Format("larder.library.line", Games, FolderSize.Humanise(Bytes),
        Never, FolderSize.Humanise(NeverBytes));

    public string FreeText => Free is { } free ? MeowsText.Current.Format("larder.library.free", FolderSize.Humanise(free)) : MeowsText.Current["larder.library.missing"];
}

/// <summary>
/// Larder: every installed Steam game, what it costs on disk, when it was last played, and which
/// library holds it, read from Steam's own manifests. The question it exists for is all of them at
/// once, never touched and largest first. It never deletes: a game folder removed behind Steam's
/// back leaves Steam believing it is installed, so uninstalling is handed to Steam.
/// </summary>
public sealed class LarderViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable
{
    public static readonly IReadOnlyList<string> Sorts =
        ["larder.sort.nevertouched", "larder.sort.largest", "larder.sort.longest", "larder.sort.name"];

    public static readonly IReadOnlyList<string> Filters =
        ["larder.filter.all", "larder.filter.never", "larder.filter.year", "larder.filter.unknown"];

    private readonly IMeowsHost _host;
    private readonly Func<IReadOnlyList<string>> _steamLibraries;
    private readonly LarderSettings _settings;
    private IReadOnlyList<SteamInstall> _all = [];
    private IBackgroundTask? _scan;
    private GameViewModel? _selected;
    private string? _status;
    private string? _errorMessage;
    private bool _isScanning;
    private bool _hasRead;

    private readonly LanguageWatch _language;

    public LarderViewModel(IMeowsHost host) : this(host, SteamLibrary.LibraryFolders)
    {
    }

    /// <summary>With the libraries Steam knows supplied from elsewhere, which is how a test hands it a folder.</summary>
    public LarderViewModel(IMeowsHost host, Func<IReadOnlyList<string>> steamLibraries)
    {
        _host = host;
        _steamLibraries = steamLibraries;
        _settings = host.LoadSettings<LarderSettings>() ?? new LarderSettings();

        RefreshCommand = new RelayCommand(Refresh, () => !IsScanning);
        UninstallCommand = new RelayCommand(Uninstall, () => Selected is not null);
        RevealCommand = new RelayCommand(Reveal, () => Selected is not null);
        AddLibraryCommand = new RelayCommand(() => _ = AddLibraryAsync());
        RemoveLibraryCommand = new RelayCommand(p => RemoveLibrary(p as string));

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var game in Games)
                game.Reread();
            Rebuild();
        });

        Refresh();
    }

    public ObservableCollection<GameViewModel> Games { get; } = [];

    public ObservableCollection<LibraryLine> Libraries { get; } = [];

    public IReadOnlyList<string> ExtraLibraries => _settings.ExtraLibraries.ToList();

    public RelayCommand RefreshCommand { get; }

    public RelayCommand UninstallCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand AddLibraryCommand { get; }

    public RelayCommand RemoveLibraryCommand { get; }

    public IReadOnlyList<string> SortChoices => Sorts.Select(k => MeowsText.Current[k]).ToList();

    public IReadOnlyList<string> FilterChoices => Filters.Select(k => MeowsText.Current[k]).ToList();

    public int SortIndex
    {
        get => _settings.SortIndex;
        set
        {
            if (value < 0 || value >= Sorts.Count || value == _settings.SortIndex)
                return;
            _settings.SortIndex = value;
            SaveSettings();
            OnPropertyChanged();
            Rebuild();
        }
    }

    public int FilterIndex
    {
        get => _settings.FilterIndex;
        set
        {
            if (value < 0 || value >= Filters.Count || value == _settings.FilterIndex)
                return;
            _settings.FilterIndex = value;
            SaveSettings();
            OnPropertyChanged();
            Rebuild();
        }
    }

    public GameViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            UninstallCommand.RaiseCanExecuteChanged();
            RevealCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
                RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["larder.status.ready"];
        private set => SetField(ref _status, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsEmpty => Games.Count == 0;

    public bool HasGames => _all.Count > 0;

    /// <summary>The whole larder in one line.</summary>
    public string Summary => _all.Count == 0
        ? ""
        : _host.Text.Format("larder.summary", _all.Count, FolderSize.Humanise(_all.Sum(g => g.SizeOnDisk)), Libraries.Count);

    /// <summary>The line the tab exists for: never launched, and what they hold.</summary>
    public string NeverLine
    {
        get
        {
            var never = _all.Where(g => g.NeverPlayed).ToList();
            return never.Count == 0 ? "" : _host.Text.Format("larder.never", never.Count, FolderSize.Humanise(never.Sum(g => g.SizeOnDisk)));
        }
    }

    /// <summary>Said apart, so the never-played number is not quietly inflated by games Steam kept no record for.</summary>
    public string UnknownLine
    {
        get
        {
            var unknown = _all.Count(g => g.PlayedUnknown);
            return unknown == 0 ? "" : _host.Text.Format("larder.unknown", unknown);
        }
    }

    public bool HasUnknown => _all.Any(g => g.PlayedUnknown);

    public string EmptyText => _hasRead && _all.Count == 0
        ? _host.Text["larder.empty.none"]
        : _host.Text["larder.empty"];

    /// <summary>Reads every library again. Manifests only; no game folder is walked.</summary>
    private void Refresh()
    {
        if (IsScanning)
            return;
        ErrorMessage = null;
        IsScanning = true;
        Status = _host.Text["larder.status.reading"];
        var libraries = AllLibraries();

        _scan = _host.Background.Run(_host.Text["larder.task"], async context =>
        {
            var games = await Task.Run(() => Read(libraries), context.Token);
            await Dispatcher.UIThread.InvokeAsync(() => Show(games, libraries));
        });
    }

    /// <summary>The libraries Steam lists, then any added by hand, each once.</summary>
    public IReadOnlyList<string> AllLibraries()
    {
        IReadOnlyList<string> known;
        try
        {
            known = _steamLibraries();
        }
        catch (Exception)
        {
            known = [];
        }
        return known.Concat(_settings.ExtraLibraries)
            .Select(l => l.TrimEnd('\\', '/'))
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<SteamInstall> Read(IReadOnlyList<string> libraries) =>
        libraries.SelectMany(SteamLibrary.InstalledIn)
            .GroupBy(g => g.AppId)
            .Select(g => g.First())
            .ToList();

    /// <summary>Puts a finished read on screen. Separate so a test can hand it games directly.</summary>
    public void Show(IReadOnlyList<SteamInstall> games, IReadOnlyList<string> libraries)
    {
        _all = games;
        _hasRead = true;
        IsScanning = false;

        Libraries.Clear();
        foreach (var library in libraries)
        {
            var here = games.Where(g => string.Equals(g.Library, library, StringComparison.OrdinalIgnoreCase)).ToList();
            var never = here.Where(g => g.NeverPlayed).ToList();
            Libraries.Add(new LibraryLine(library, here.Count, here.Sum(g => g.SizeOnDisk), never.Count, never.Sum(g => g.SizeOnDisk), FreeOn(library)));
        }

        Rebuild();
        Status = libraries.Count == 0
            ? _host.Text["larder.status.nosteam"]
            : _host.Text.Format("larder.status.read", DateTime.Now.ToString("HH:mm"));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(NeverLine));
        OnPropertyChanged(nameof(UnknownLine));
        OnPropertyChanged(nameof(HasUnknown));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(EmptyText));
        _host.Log($"Larder read {games.Count} game(s) from {libraries.Count} librar(ies).");
    }

    private static long? FreeOn(string library)
    {
        try
        {
            if (!Directory.Exists(library))
                return null;
            var root = Path.GetPathRoot(Path.GetFullPath(library));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Rebuild()
    {
        var yearAgo = DateTime.UtcNow.AddYears(-1);
        IEnumerable<SteamInstall> shown = FilterIndex switch
        {
            1 => _all.Where(g => g.NeverPlayed),
            2 => _all.Where(g => g.LastPlayed is { } played && played != DateTime.UnixEpoch && played < yearAgo),
            3 => _all.Where(g => g.PlayedUnknown),
            _ => _all,
        };

        shown = SortIndex switch
        {
            1 => shown.OrderByDescending(g => g.SizeOnDisk),
            2 => shown.OrderBy(g => g.PlayedUnknown ? 1 : 0).ThenBy(g => g.LastPlayed ?? DateTime.MaxValue).ThenByDescending(g => g.SizeOnDisk),
            3 => shown.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            // Never touched first, then the ones Steam kept no record for, then the rest by how
            // long it has been, and the largest first within each.
            _ => shown.OrderBy(g => g.NeverPlayed ? 0 : g.PlayedUnknown ? 1 : 2)
                .ThenBy(g => g.NeverPlayed || g.PlayedUnknown ? DateTime.MinValue : g.LastPlayed)
                .ThenByDescending(g => g.SizeOnDisk),
        };

        var keep = Selected?.Game.AppId;
        Games.Clear();
        foreach (var game in shown)
            Games.Add(new GameViewModel(game));
        Selected = Games.FirstOrDefault(g => g.Game.AppId == keep);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>How long ago, in the one unit that reads naturally.</summary>
    public static string Ago(DateTime when, DateTime now)
    {
        var days = (now - when).TotalDays;
        return days switch
        {
            < 1 => MeowsText.Current["larder.ago.today"],
            < 2 => MeowsText.Current["larder.ago.yesterday"],
            < 60 => MeowsText.Current.Format("larder.ago.days", (int)days),
            < 730 => MeowsText.Current.Format("larder.ago.months", (int)(days / 30.44)),
            _ => MeowsText.Current.Format("larder.ago.years", (int)(days / 365.25)),
        };
    }

    /// <summary>Steam asks its own question and does the uninstall; nothing here touches the folder.</summary>
    private void Uninstall()
    {
        if (Selected is not { } game)
            return;
        try
        {
            Explorer.Open($"steam://uninstall/{game.Game.AppId}");
            _host.Log($"Asked Steam to uninstall {game.Name} ({game.Game.AppId}).");
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("larder.error.steam", ex.Message);
        }
    }

    private void Reveal()
    {
        if (Selected is not { } game)
            return;
        try
        {
            if (Directory.Exists(game.Game.InstallFolder))
                Explorer.Open(game.Game.InstallFolder);
            else
                ErrorMessage = _host.Text.Format("larder.error.gone", game.Game.InstallFolder);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task AddLibraryAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["larder.dialog.library"] });
            if (!string.IsNullOrWhiteSpace(picked))
                AddLibrary(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    /// <summary>
    /// A library by hand. Picking the steamapps folder itself, or a game's folder inside common,
    /// is taken to mean the library it belongs to.
    /// </summary>
    public void AddLibrary(string folder)
    {
        var library = folder.TrimEnd('\\', '/');
        var parts = library.Split('\\', '/');
        var at = Array.FindLastIndex(parts, p => p.Equals("steamapps", StringComparison.OrdinalIgnoreCase));
        if (at > 0)
            library = library[..library.LastIndexOf(parts[at], StringComparison.OrdinalIgnoreCase)].TrimEnd('\\', '/');

        if (_settings.ExtraLibraries.Contains(library, StringComparer.OrdinalIgnoreCase))
            return;
        _settings.ExtraLibraries.Add(library);
        SaveSettings();
        OnPropertyChanged(nameof(ExtraLibraries));
        Refresh();
    }

    private void RemoveLibrary(string? library)
    {
        if (library is null || _settings.ExtraLibraries.RemoveAll(l => string.Equals(l, library, StringComparison.OrdinalIgnoreCase)) == 0)
            return;
        SaveSettings();
        OnPropertyChanged(nameof(ExtraLibraries));
        Refresh();
    }

    /// <summary>Ctrl+K reaching into this tab: games by name, from what is already loaded.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        return _all
            .Where(g => SearchWords.Match(words, g.Name))
            .Take(limit)
            .Select(g => new SearchHit(g.Name, $"{FolderSize.Humanise(g.SizeOnDisk)} · {g.Library}", () =>
            {
                FilterIndex = 0;
                Selected = Games.FirstOrDefault(v => v.Game.AppId == g.AppId);
            }))
            .ToList();
    }

    /// <summary>Home's line: what never-launched games are holding. A fact, not trouble.</summary>
    public Glance? Glance()
    {
        var never = _all.Where(g => g.NeverPlayed).ToList();
        return never.Count == 0
            ? null
            : new Glance(_host.Text.Format("larder.glance", never.Count, FolderSize.Humanise(never.Sum(g => g.SizeOnDisk))));
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Larder settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _language.Dispose();
        _scan?.Cancel();
    }
}
