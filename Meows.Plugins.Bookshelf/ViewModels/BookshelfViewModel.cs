using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Bookshelf.Services;

namespace Meows.Plugins.Bookshelf.ViewModels;

public sealed class BookshelfSettings
{
    public List<BookFolder> Folders { get; set; } = [];

    public List<BookState> States { get; set; } = [];

    public string? StagingFolder { get; set; }

    public BookSummary? LastSummary { get; set; }
}

/// <summary>One choice in a dropdown, named by the language the window is in.</summary>
public sealed class Choice(string key, int value) : ObservableObject
{
    public int Value { get; } = value;

    public string Name => MeowsText.Current[key];

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

public sealed class FolderRowViewModel : ObservableObject
{
    private readonly BookshelfViewModel _owner;

    public FolderRowViewModel(BookFolder folder, BookshelfViewModel owner, bool missing)
    {
        Folder = folder;
        _owner = owner;
        IsMissing = missing;
    }

    public BookFolder Folder { get; }

    public bool IsMissing { get; }

    public string Shown => Folder.Path;

    public bool Enabled
    {
        get => Folder.Enabled;
        set
        {
            if (Folder.Enabled == value)
                return;
            Folder.Enabled = value;
            OnPropertyChanged();
            _owner.FoldersTouched();
        }
    }

    internal void Reread() => OnEverythingChanged();
}

public sealed class BookRowViewModel : ObservableObject
{
    private readonly BookshelfViewModel _owner;

    public BookRowViewModel(Book book, BookshelfViewModel owner)
    {
        Book = book;
        _owner = owner;
    }

    public Book Book { get; }

    public string Path => Book.Path;

    public string Shown => Book.Title.Trim().Length > 0
        ? Book.Title
        : MeowsText.Current["bookshelf.untitled"];

    public string Author => Book.Author;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(Book.Author);

    public string Format => Book.Format.ToUpperInvariant();

    public string SizeText => Library.Humanise(Book.Size);

    /// <summary>Opened how long ago, in the one unit that reads naturally.</summary>
    public string OpenedText => Library.Ago(Book.LastOpenedUtc, DateTime.UtcNow, MeowsText.Current);

    public bool IsDuplicate => Book.IsDuplicate;

    public BookStatus Status
    {
        get => Book.Status;
        set
        {
            if (Book.Status == value)
                return;
            Book.Status = value;
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
        get => _owner.Statuses.FirstOrDefault(c => c.Value == (int)Book.Status);
        set
        {
            if (value is not null)
                Status = (BookStatus)value.Value;
        }
    }

    public string StatusText => MeowsText.Current[Library.Describe(Book.Status)];

    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// Bookshelf: the ebooks on disk, which are the same book twice, and which were started but
/// never finished, least recently opened first. It never opens a book itself, and what goes
/// goes through the Recycle Bin.
/// </summary>
public sealed class BookshelfViewModel : ObservableObject, IDisposable, ISearchable, IHandoffTarget, IGlanceable, IActionTarget
{
    public static readonly IReadOnlyList<string> Sorts =
        ["bookshelf.sort.unfinished", "bookshelf.sort.largest", "bookshelf.sort.name", "bookshelf.sort.recent"];

    public static readonly IReadOnlyList<string> Filters =
        ["bookshelf.filter.all", "bookshelf.filter.unfinished", "bookshelf.filter.duplicates", "bookshelf.filter.finished"];

    private readonly IMeowsHost _host;
    private readonly string _profileRoot;
    private readonly BookshelfSettings _settings;
    private readonly LanguageWatch _language;
    private IBackgroundTask? _scan;

    private List<Book> _all = [];
    private BookRowViewModel? _selected;
    private FolderRowViewModel? _selectedFolder;
    private string? _status;
    private string? _errorMessage;
    private bool _isScanning;
    private bool _hasRead;
    private int _sortIndex;
    private int _filterIndex;

    public BookshelfViewModel(IMeowsHost host) : this(host,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <summary>With the profile supplied from elsewhere, which is how a test hands it a fake one.</summary>
    public BookshelfViewModel(IMeowsHost host, string profileRoot)
    {
        _host = host;
        _profileRoot = profileRoot;
        _settings = host.LoadSettings<BookshelfSettings>() ?? new BookshelfSettings();

        RefreshCommand = new RelayCommand(Refresh, () => !IsScanning);
        StageUnfinishedCommand = new RelayCommand(() => _ = StageUnfinishedAsync(), () => !IsScanning && HasStaging);
        OpenCommand = new RelayCommand(() => Open(Selected?.Path), () => Selected is not null);
        RevealCommand = new RelayCommand(() => Reveal(Selected?.Path), () => Selected is not null);
        RecycleOneCommand = new RelayCommand(() => _ = RecycleOneAsync(), () => Selected is not null);
        SetStagingCommand = new RelayCommand(() => _ = SetStagingAsync());
        AddFolderCommand = new RelayCommand(() => _ = AddFolderAsync());
        RemoveFolderCommand = new RelayCommand(RemoveFolder, () => SelectedFolder is not null);

        RebuildFolders();

        _language = new LanguageWatch(Retranslate);

        // A fresh shelf looks for a Calibre library first; the usual folders arrive switched on.
        if (_settings.Folders.Count == 0)
            Discover();
        else
            Refresh();
    }

    /// <summary>Dates as the window's language writes them, not as the machine does.</summary>
    public static CultureInfo Culture => MeowsText.Current.Language == "de"
        ? CultureInfo.GetCultureInfo("de-DE")
        : CultureInfo.GetCultureInfo("en-GB");

    public ObservableCollection<BookRowViewModel> Rows { get; } = [];

    public ObservableCollection<FolderRowViewModel> Folders { get; } = [];

    public ObservableCollection<Choice> Statuses { get; } =
    [
        new("bookshelf.state.unread", (int)BookStatus.Unread),
        new("bookshelf.state.reading", (int)BookStatus.Reading),
        new("bookshelf.state.finished", (int)BookStatus.Finished),
    ];

    public IReadOnlyList<string> SortChoices => Sorts.Select(k => MeowsText.Current[k]).ToList();

    public IReadOnlyList<string> FilterChoices => Filters.Select(k => MeowsText.Current[k]).ToList();

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (value == _sortIndex)
                return;
            _sortIndex = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (value == _filterIndex)
                return;
            _filterIndex = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand StageUnfinishedCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand RecycleOneCommand { get; }

    public RelayCommand SetStagingCommand { get; }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand RemoveFolderCommand { get; }

    public BookRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            OpenCommand.RaiseCanExecuteChanged();
            RevealCommand.RaiseCanExecuteChanged();
            RecycleOneCommand.RaiseCanExecuteChanged();
        }
    }

    public FolderRowViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetField(ref _selectedFolder, value))
                return;
            RemoveFolderCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    public bool HasStaging => !string.IsNullOrWhiteSpace(_settings.StagingFolder);

    public string StagingText => HasStaging
        ? _settings.StagingFolder!
        : _host.Text["bookshelf.nostaging"];

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                StageUnfinishedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["bookshelf.status.ready"];
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

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The whole shelf in one line, from the last scan.</summary>
    public string Summary => Library.SummaryOf(_settings.LastSummary, _host.Text);

    /// <summary>The Home line. Red while copies are holding room.</summary>
    public Glance? Glance() => Library.GlanceOf(_settings.LastSummary, _host.Text);

    public string EmptyText => _hasRead && _all.Count == 0
        ? _host.Text["bookshelf.empty.none"]
        : _host.Text["bookshelf.empty"];

    /// <summary>Reads every switched-on folder again. Files are listed, never opened.</summary>
    private void Refresh()
    {
        if (IsScanning)
            return;
        ErrorMessage = null;
        IsScanning = true;
        Status = _host.Text["bookshelf.status.reading"];

        _scan = _host.Background.Run(_host.Text["bookshelf.task"], async context =>
        {
            var books = await Task.Run(() => RunScan(_settings.Folders, _settings.States), context.Token);
            await Dispatcher.UIThread.InvokeAsync(() => Show(books));
        });
    }

    /// <summary>The scan without the scheduling: pure listing, hashing and joining.</summary>
    public static List<Book> RunScan(List<BookFolder> folders, List<BookState> states) =>
        Library.Scan(folders, states);

    /// <summary>Scans the kept folders with the kept states, right now. What --do and the tests run.</summary>
    public void RescanNow() => Show(RunScan(_settings.Folders, _settings.States));

    /// <summary>Puts a finished scan on screen. Separate so a test can hand it books directly.</summary>
    public void Show(List<Book> books)
    {
        _all = books;
        _hasRead = true;
        IsScanning = false;

        _settings.LastSummary = Library.Summarise(books, DateTime.UtcNow);
        Save();

        Rebuild();
        var summary = _settings.LastSummary;
        Status = _host.Text.Format("bookshelf.status.read",
            summary.Books, Library.Humanise(summary.Bytes),
            summary.Unfinished, summary.Duplicates, Library.Humanise(summary.DuplicateBytes),
            DateTime.Now.ToString("HH:mm"));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EmptyText));
        _host.Store.Record("scanned", _host.Text.Format("bookshelf.records.scanned", summary.Books));
        _host.Log($"Bookshelf saw {summary.Books} book(s), {summary.Unfinished} unfinished, {summary.Duplicates} duplicate(s).");
    }

    private void Rebuild()
    {
        IEnumerable<Book> shown = FilterIndex switch
        {
            1 => Library.Unfinished(_all),
            2 => _all.Where(b => b.IsDuplicate),
            3 => _all.Where(b => b.Status == BookStatus.Finished),
            _ => _all,
        };

        shown = SortIndex switch
        {
            // The shelf's own order: reading by least recently opened, then unread the same
            // way, then the finished, each by name within.
            1 => shown.OrderByDescending(b => b.Size),
            2 => shown.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase),
            3 => shown.OrderByDescending(b => b.LastOpenedUtc),
            _ => shown.OrderBy(b => b.Status switch
                {
                    BookStatus.Reading => 0,
                    BookStatus.Unread => 1,
                    _ => 2,
                }).ThenBy(b => b.LastOpenedUtc).ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase),
        };

        var keep = Selected?.Path;
        Rows.Clear();
        foreach (var book in shown)
            Rows.Add(new BookRowViewModel(book, this));
        Selected = Rows.FirstOrDefault(r => string.Equals(r.Path, keep, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(IsEmpty));
        StageUnfinishedCommand.RaiseCanExecuteChanged();
    }

    private void RebuildFolders()
    {
        var keep = SelectedFolder?.Folder.Path;
        Folders.Clear();
        foreach (var folder in _settings.Folders)
        {
            bool missing;
            try
            {
                missing = !Directory.Exists(folder.Path);
            }
            catch (Exception)
            {
                missing = true;
            }
            Folders.Add(new FolderRowViewModel(folder, this, missing));
        }
        SelectedFolder = Folders.FirstOrDefault(f => string.Equals(f.Folder.Path, keep, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Folders found, merged in, and read. The first thing a fresh shelf does is look around.</summary>
    public void Discover()
    {
        var added = Library.MergeFolders(_settings.Folders, Library.FindSources(_profileRoot));
        Save();
        RebuildFolders();
        if (added > 0)
            Status = _host.Text.Format("bookshelf.status.found", added);
        Refresh();
    }

    /// <summary>A status set on a row: kept, journaled where it matters, and shown again.</summary>
    internal void StatusChanged(BookRowViewModel row)
    {
        var state = _settings.States.FirstOrDefault(s => string.Equals(s.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            state = new BookState { Path = row.Path };
            _settings.States.Add(state);
        }
        state.Status = row.Book.Status;

        if (row.Book.Status == BookStatus.Finished)
            _host.Store.Record("finished", row.Shown);

        // The summary is kept, not just shown: Home reads it with no scan in memory.
        _settings.LastSummary = Library.Summarise(_all, DateTime.UtcNow);
        Save();
        var keep = Selected?.Path;
        Rebuild();
        Selected = Rows.FirstOrDefault(r => string.Equals(r.Path, keep ?? row.Path, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(Summary));
    }

    internal void FoldersTouched()
    {
        Save();
        Refresh();
    }

    /// <summary>Unfinished books copied to the staging folder for the ereader: nothing is moved.</summary>
    private async Task StageUnfinishedAsync()
    {
        if (!HasStaging)
        {
            ErrorMessage = _host.Text["bookshelf.nostaging"];
            return;
        }

        var paths = Library.Unfinished(_all).Select(b => b.Path).ToList();
        if (paths.Count == 0)
        {
            Status = _host.Text["bookshelf.status.nounfinished"];
            return;
        }

        var (copied, skipped) = await Task.Run(() => Library.Stage(paths, _settings.StagingFolder!));
        ErrorMessage = null;
        _host.Store.Record("sent", _host.Text.Format("bookshelf.records.sent", copied));
        Status = _host.Text.Format("bookshelf.status.staged", copied, skipped);
    }

    private async Task RecycleOneAsync()
    {
        if (Selected is not { } selected)
            return;

        var outcome = await Task.Run(() => RecycleBin.Send([selected.Path]));
        if (!outcome.Succeeded)
        {
            ErrorMessage = outcome.FailureReason ?? _host.Text.Format("bookshelf.error.gone", selected.Path);
            return;
        }

        ErrorMessage = null;
        _host.Store.Record("recycled", System.IO.Path.GetFileName(selected.Path));
        Refresh();
        Status = _host.Text.Format("bookshelf.status.recycled", 1);
    }

    /// <summary>
    /// A rule's "stage the unfinished": copies what was started but never finished to the
    /// staging folder. Declines when there is nowhere to stage to, or nothing unfinished.
    /// </summary>
    public Task<string> Perform(ActionRequest request, CancellationToken token)
    {
        if (request.Action != BookshelfPlugin.StageAction)
            throw new ActionDeclinedException(_host.Text.Format("bookshelf.action.unknown", request.Action));
        if (!HasStaging)
            throw new ActionDeclinedException(_host.Text["bookshelf.nostaging"]);

        var paths = Library.Unfinished(RunScan(_settings.Folders, _settings.States)).Select(b => b.Path).ToList();
        if (paths.Count == 0)
            throw new ActionDeclinedException(_host.Text["bookshelf.status.nounfinished"]);

        var (copied, _) = Library.Stage(paths, _settings.StagingFolder!);
        _host.Store.Record("sent", _host.Text.Format("bookshelf.records.sent", copied));

        return Task.FromResult(_host.Text.Format("bookshelf.status.staged", copied, paths.Count - copied));
    }

    public bool Accepts(Handoff handoff) =>
        handoff.Verb == BookshelfPlugin.ShowVerb && handoff.Note is { Length: > 0 } &&
        _all.Any(b => string.Equals(b.Path, handoff.Note, StringComparison.OrdinalIgnoreCase));

    public void Receive(Handoff handoff)
    {
        if (!Accepts(handoff))
            return;

        // From Ctrl+K while Bookshelf was off: the hit, now that there is a list to select in.
        Selected = Rows.FirstOrDefault(r => string.Equals(r.Path, handoff.Note, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveFolder()
    {
        if (SelectedFolder is not { } selected)
            return;

        _settings.Folders.RemoveAll(f => string.Equals(f.Path, selected.Folder.Path, StringComparison.OrdinalIgnoreCase));
        Save();
        SelectedFolder = null;
        RebuildFolders();
        Refresh();
    }

    private void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            ErrorMessage = _host.Text.Format("bookshelf.error.gone", path);
            return;
        }

        ErrorMessage = null;

        try
        {
            Explorer.Open(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("bookshelf.error.open", path, ex.Message);
        }
    }

    private void Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            ErrorMessage = _host.Text.Format("bookshelf.error.gone", path);
            return;
        }

        ErrorMessage = null;

        try
        {
            Explorer.Reveal(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("bookshelf.error.open", path, ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not save Bookshelf settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the shelf: by title or author, from what is already loaded. Landing on one selects it.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var row in Rows)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, row.Shown, row.Author))
                continue;

            var chosen = row;
            hits.Add(new SearchHit(chosen.Shown, chosen.StatusText, () => Selected = chosen));
        }

        return hits;
    }

    public void Dispose()
    {
        _scan?.Cancel();
        _language.Dispose();
    }

    // ---- picking folders by hand, through the host's dialogs rather than a TopLevel of our own ----

    private async Task SetStagingAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["bookshelf.dialog.staging"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;

            _settings.StagingFolder = picked;
            Save();
            OnPropertyChanged(nameof(HasStaging));
            OnPropertyChanged(nameof(StagingText));
            StageUnfinishedCommand.RaiseCanExecuteChanged();
            Status = _host.Text.Format("bookshelf.status.staging", picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    private async Task AddFolderAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["bookshelf.dialog.folder"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;
            AddFolder(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    /// <summary>A folder by hand. Picking it twice watches it once.</summary>
    public void AddFolder(string picked)
    {
        if (_settings.Folders.Any(f => string.Equals(f.Path, picked, StringComparison.OrdinalIgnoreCase)))
        {
            Status = _host.Text.Format("bookshelf.status.already", picked);
            return;
        }

        _settings.Folders.Add(new BookFolder { Path = picked, Enabled = true });
        Save();
        RebuildFolders();
        Refresh();
        Status = _host.Text.Format("bookshelf.status.added", picked);
    }

    private void Retranslate()
    {
        foreach (var row in Rows)
            row.Reread();
        foreach (var folder in Folders)
            folder.Reread();
        foreach (var choice in Statuses)
            choice.Reread();

        OnEverythingChanged();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StagingText));
    }
}
