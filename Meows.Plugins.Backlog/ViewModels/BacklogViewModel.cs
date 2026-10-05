using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Backlog.Services;

namespace Meows.Plugins.Backlog.ViewModels;

public sealed class BacklogSettings
{
    public List<BacklogEntry> Entries { get; set; } = [];

    public int SortIndex { get; set; }

    public int FilterIndex { get; set; }
}

/// <summary>One choice in a dropdown, named by the language the window is in.</summary>
public sealed class Choice(string key, int value) : ObservableObject
{
    public int Value { get; } = value;

    public string Name => MeowsText.Current[key];

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

public sealed class GameRowViewModel : ObservableObject
{
    private readonly BacklogViewModel _owner;

    public GameRowViewModel(BacklogEntry entry, SteamInstall? install, BacklogViewModel owner)
    {
        Entry = entry;
        Install = install;
        _owner = owner;
    }

    public BacklogEntry Entry { get; }

    public SteamInstall? Install { get; }

    public string Id => Entry.Id;

    public string Shown => Entry.Name.Trim().Length > 0
        ? Entry.Name
        : MeowsText.Current["backlog.untitled"];

    public string Note
    {
        get => Entry.Note;
        set
        {
            if (Entry.Note == value)
                return;
            Entry.Note = value ?? "";
            OnPropertyChanged();
            _owner.Touched();
        }
    }

    public BacklogStatus Status
    {
        get => Entry.Status;
        set
        {
            if (Entry.Status == value)
                return;
            Entry.Status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusChoice));
            OnPropertyChanged(nameof(StatusText));
            _owner.StatusChanged(this);
        }
    }

    /// <summary>
    /// The same thing as a dropdown row. A ComboBox hands back the object it was given, and the
    /// object it was given has to be one of the very items in the list or it shows blank, so
    /// these look the choice up rather than making a new one.
    /// </summary>
    public Choice? StatusChoice
    {
        get => _owner.Statuses.FirstOrDefault(c => c.Value == (int)Entry.Status);
        set
        {
            if (value is not null)
                Status = (BacklogStatus)value.Value;
        }
    }

    public int Rating
    {
        get => Entry.Rating;
        set
        {
            var stars = Math.Clamp(value, 0, 5);
            if (Entry.Rating == stars)
                return;
            Entry.Rating = stars;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RatingChoice));
            OnPropertyChanged(nameof(Stars));
            _owner.Touched();
        }
    }

    public Choice? RatingChoice
    {
        get => _owner.Ratings.FirstOrDefault(c => c.Value == Entry.Rating);
        set
        {
            if (value is not null)
                Rating = value.Value;
        }
    }

    public string Stars => Pile.Stars(Entry.Rating, MeowsText.Current);

    public string StatusText => MeowsText.Current[Pile.Describe(Entry.Status)];

    public string SizeText => Install is null
        ? MeowsText.Current["backlog.notinstalled"]
        : FolderSize.Humanise(Install.SizeOnDisk);

    public bool IsInstalled => Install is not null;

    /// <summary>Never, unknown, or how long ago. Unknown is never passed off as never.</summary>
    public string PlayedText
    {
        get
        {
            if (Install is null)
                return MeowsText.Current["backlog.notinstalled"];
            if (Install.NeverPlayed)
                return MeowsText.Current["backlog.played.never"];
            if (Install.PlayedUnknown)
                return MeowsText.Current["backlog.played.unknown"];
            return BacklogViewModel.Ago(Install.LastPlayed!.Value.ToLocalTime(), DateTime.Now);
        }
    }

    /// <summary>Everything on the row reads differently after a scan or a language change.</summary>
    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// Backlog: which game to play next. Steam's manifests say what is installed and what it costs;
/// the pile says what that means: waiting, playing, shelved or done, with a rating once it can
/// be given one. It never touches a game folder: uninstalling stays Steam's job, as in Larder.
/// </summary>
public sealed class BacklogViewModel : ObservableObject, IDisposable, ISearchable, IHandoffTarget, IGlanceable, IActionTarget
{
    public static readonly IReadOnlyList<string> Sorts =
        ["backlog.sort.toplay", "backlog.sort.largest", "backlog.sort.rated", "backlog.sort.name"];

    public static readonly IReadOnlyList<string> Filters =
        ["backlog.filter.all", "backlog.filter.open", "backlog.filter.shelved", "backlog.filter.done"];

    private readonly IMeowsHost _host;
    private readonly Func<IReadOnlyList<string>> _steamLibraries;
    private readonly BacklogSettings _settings;
    private readonly Dictionary<string, SteamInstall> _installs = new(StringComparer.Ordinal);
    private readonly LanguageWatch _language;
    private readonly Random _random = new();
    private IBackgroundTask? _scan;

    private GameRowViewModel? _selected;
    private string? _status;
    private string? _errorMessage;
    private string _newGameName = "";
    private bool _isScanning;
    private bool _hasRead;

    public BacklogViewModel(IMeowsHost host) : this(host, SteamLibrary.LibraryFolders)
    {
    }

    /// <summary>With the libraries supplied from elsewhere, which is how a test hands it a folder.</summary>
    public BacklogViewModel(IMeowsHost host, Func<IReadOnlyList<string>> steamLibraries)
    {
        _host = host;
        _steamLibraries = steamLibraries;
        _settings = host.LoadSettings<BacklogSettings>() ?? new BacklogSettings();

        RefreshCommand = new RelayCommand(Refresh, () => !IsScanning);
        PickCommand = new RelayCommand(Pick, () => !IsScanning);
        AddCommand = new RelayCommand(AddManual, () => !string.IsNullOrWhiteSpace(NewGameName));
        ForgetCommand = new RelayCommand(Forget, () => Selected is not null);
        PlayCommand = new RelayCommand(() => SetStatus(BacklogStatus.Playing), () => Selected is not null);
        DoneCommand = new RelayCommand(() => SetStatus(BacklogStatus.Done), () => Selected is not null);

        _language = new LanguageWatch(Retranslate);

        Refresh();
    }

    public ObservableCollection<GameRowViewModel> Games { get; } = [];

    public ObservableCollection<Choice> Statuses { get; } =
    [
        new("backlog.status.backlog", (int)BacklogStatus.Backlog),
        new("backlog.status.playing", (int)BacklogStatus.Playing),
        new("backlog.status.shelved", (int)BacklogStatus.Shelved),
        new("backlog.status.done", (int)BacklogStatus.Done),
    ];

    public ObservableCollection<Choice> Ratings { get; } =
    [
        new("backlog.rating.none", 0),
        new("backlog.rating.1", 1),
        new("backlog.rating.2", 2),
        new("backlog.rating.3", 3),
        new("backlog.rating.4", 4),
        new("backlog.rating.5", 5),
    ];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand PickCommand { get; }

    public RelayCommand AddCommand { get; }

    public RelayCommand ForgetCommand { get; }

    public RelayCommand PlayCommand { get; }

    public RelayCommand DoneCommand { get; }

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
            Save();
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
            Save();
            OnPropertyChanged();
            Rebuild();
        }
    }

    public GameRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            foreach (var command in new[] { ForgetCommand, PlayCommand, DoneCommand })
                command.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                PickCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string NewGameName
    {
        get => _newGameName;
        set
        {
            if (SetField(ref _newGameName, value))
                AddCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["backlog.status.ready"];
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

    /// <summary>The whole pile in one line.</summary>
    public string Summary => Pile.SummaryOf(_settings.Entries, _host.Text);

    /// <summary>The Home line: what is waiting. A fact, never trouble.</summary>
    public Glance? Glance() => Pile.GlanceOf(_settings.Entries, _host.Text);

    public string EmptyText => _hasRead && _settings.Entries.Count == 0
        ? _host.Text["backlog.empty.none"]
        : _host.Text["backlog.empty"];

    /// <summary>Reads Steam again. Manifests only; no game folder is walked.</summary>
    private void Refresh()
    {
        if (IsScanning)
            return;
        ErrorMessage = null;
        IsScanning = true;
        Status = _host.Text["backlog.status.reading"];

        IReadOnlyList<string> libraries;
        try
        {
            libraries = _steamLibraries();
        }
        catch (Exception)
        {
            libraries = [];
        }

        _scan = _host.Background.Run(_host.Text["backlog.task"], async context =>
        {
            var games = await Task.Run(() => libraries.SelectMany(SteamLibrary.InstalledIn)
                .GroupBy(g => g.AppId).Select(g => g.First()).ToList(), context.Token);
            await Dispatcher.UIThread.InvokeAsync(() => Show(games));
        });
    }

    /// <summary>Puts a finished read on screen. Separate so a test can hand it games directly.</summary>
    public void Show(IReadOnlyList<SteamInstall> installs)
    {
        _installs.Clear();
        foreach (var install in installs)
            _installs[install.AppId] = install;

        var added = Pile.EnsureFromSteam(_settings.Entries, installs);
        _hasRead = true;
        IsScanning = false;
        Save();

        Rebuild();
        Status = _host.Text.Format("backlog.status.read", DateTime.Now.ToString("HH:mm"));
        if (added > 0)
            Status = _host.Text.Format("backlog.status.added", added, Status);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EmptyText));
        _host.Log($"Backlog knows {_settings.Entries.Count} game(s), {added} new from Steam.");
    }

    private void Rebuild()
    {
        IEnumerable<BacklogEntry> shown = FilterIndex switch
        {
            1 => _settings.Entries.Where(e => e.Status is BacklogStatus.Backlog or BacklogStatus.Playing),
            2 => _settings.Entries.Where(e => e.Status == BacklogStatus.Shelved),
            3 => _settings.Entries.Where(e => e.Status == BacklogStatus.Done),
            _ => _settings.Entries,
        };

        long SizeOf(BacklogEntry e) => _installs.TryGetValue(e.Id, out var install) ? install.SizeOnDisk : -1;

        shown = SortIndex switch
        {
            // The pile's own order: waiting first by size, then playing, then the rest by name.
            1 => shown.OrderBy(e => e.Status is BacklogStatus.Backlog or BacklogStatus.Playing ? 0 : 1)
                .ThenByDescending(SizeOf).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => shown.OrderByDescending(e => e.Rating).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            3 => shown.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => shown.OrderBy(e => e.Status switch
                {
                    BacklogStatus.Playing => 0,
                    BacklogStatus.Backlog => 1,
                    BacklogStatus.Shelved => 2,
                    _ => 3,
                }).ThenByDescending(SizeOf).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var keep = Selected?.Id;
        Games.Clear();
        foreach (var entry in shown)
            Games.Add(new GameRowViewModel(entry, _installs.GetValueOrDefault(entry.Id), this));
        Selected = Games.FirstOrDefault(g => g.Id == keep);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>One game for tonight, selected and said out loud.</summary>
    public void Pick()
    {
        var picked = Pile.Pick(_settings.Entries, _random);
        if (picked is null)
        {
            Status = _host.Text["backlog.status.nothing"];
            return;
        }

        _host.Store.Record("picked", picked.Name);
        Selected = Games.FirstOrDefault(g => g.Id == picked.Id);
        Status = _host.Text.Format("backlog.status.picked", picked.Name);
    }

    /// <summary>A game Steam never heard of, added by hand. It has no size and no played time.</summary>
    public void AddManual()
    {
        var name = NewGameName.Trim();
        if (name.Length == 0)
            return;

        if (_settings.Entries.Any(e => string.Equals(e.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            Status = _host.Text.Format("backlog.status.already", name);
            return;
        }

        var entry = new BacklogEntry { Id = "manual:" + Guid.NewGuid().ToString("N"), Name = name };
        _settings.Entries.Add(entry);
        _host.Store.Record("added", name);
        NewGameName = "";
        Save();
        Rebuild();
        Selected = Games.FirstOrDefault(g => g.Id == entry.Id);
        Status = _host.Text.Format("backlog.status.manual", name);
    }

    private void Forget()
    {
        if (Selected is not { } selected)
            return;

        // No confirmation, deliberately. This forgets what the pile thought, never a game folder.
        var name = selected.Shown;
        _settings.Entries.RemoveAll(e => e.Id == selected.Id);
        Save();
        Selected = null;
        Rebuild();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EmptyText));
        Status = _host.Text.Format("backlog.status.deleted", name);
    }

    private void SetStatus(BacklogStatus status)
    {
        if (Selected is not null)
            Selected.Status = status;
    }

    /// <summary>A status set on a row: saved, journaled where it matters, and shown again.</summary>
    internal void StatusChanged(GameRowViewModel row)
    {
        if (row.Status == BacklogStatus.Playing)
            _host.Store.Record("started", row.Shown);
        else if (row.Status == BacklogStatus.Done)
            _host.Store.Record("finished", row.Shown);

        Save();
        var keep = Selected?.Id;
        Rebuild();
        Selected = Games.FirstOrDefault(g => g.Id == (keep ?? row.Id));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Something on a row changed that needs no resort: saved, and the header re-read.</summary>
    internal void Touched()
    {
        Save();
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// A rule's "pick a game": one weighted pick, said in a sentence for the History tab. Nothing
    /// is selected, since nobody is necessarily looking.
    /// </summary>
    public Task<string> Perform(ActionRequest request, CancellationToken token)
    {
        if (request.Action != BacklogPlugin.PickAction)
            throw new ActionDeclinedException(_host.Text.Format("backlog.action.unknown", request.Action));

        var picked = Pile.Pick(_settings.Entries, _random);
        if (picked is null)
            throw new ActionDeclinedException(_host.Text["backlog.status.nothing"]);

        _host.Store.Record("picked", picked.Name);
        return Task.FromResult(_host.Text.Format("backlog.status.picked", picked.Name));
    }

    public bool Accepts(Handoff handoff) =>
        handoff.Verb == BacklogPlugin.ShowVerb && handoff.Note is { Length: > 0 };

    public void Receive(Handoff handoff)
    {
        if (!Accepts(handoff))
            return;

        // From Ctrl+K while Backlog was off: the hit, now that there is a list to select in.
        Selected = Games.FirstOrDefault(g => g.Id == handoff.Note);
    }

    /// <summary>How long ago, in the one unit that reads naturally.</summary>
    public static string Ago(DateTime when, DateTime now)
    {
        var days = (now - when).TotalDays;
        return days switch
        {
            < 1 => MeowsText.Current["backlog.ago.today"],
            < 2 => MeowsText.Current["backlog.ago.yesterday"],
            < 60 => MeowsText.Current.Format("backlog.ago.days", (int)days),
            < 730 => MeowsText.Current.Format("backlog.ago.months", (int)(days / 30.44)),
            _ => MeowsText.Current.Format("backlog.ago.years", (int)(days / 365.25)),
        };
    }

    private void Retranslate()
    {
        foreach (var game in Games)
            game.Reread();
        foreach (var choice in Statuses.Concat(Ratings))
            choice.Reread();

        OnEverythingChanged();
        OnPropertyChanged(nameof(Summary));
    }

    private void Save()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not save Backlog settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the pile: games by name, from what is already loaded.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var game in Games)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, game.Shown))
                continue;

            var chosen = game;
            hits.Add(new SearchHit(chosen.Shown, chosen.StatusText, () => Selected = chosen));
        }

        return hits;
    }

    public void Dispose()
    {
        _scan?.Cancel();
        _language.Dispose();
    }
}
