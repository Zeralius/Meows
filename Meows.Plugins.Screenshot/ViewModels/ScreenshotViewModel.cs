using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Screenshot.Services;

namespace Meows.Plugins.Screenshot.ViewModels;

public sealed class ScreenshotSettings
{
    public List<ShotFolder> Folders { get; set; } = [];

    /// <summary>Best-of picks, by path. Pruned to what the last scan actually saw.</summary>
    public List<string> KeptPaths { get; set; } = [];

    public ShotSummary? LastSummary { get; set; }
}

public sealed class FolderRowViewModel : ObservableObject
{
    private readonly ScreenshotViewModel _owner;

    public FolderRowViewModel(ShotFolder folder, ScreenshotViewModel owner, bool missing)
    {
        Folder = folder;
        _owner = owner;
        IsMissing = missing;
    }

    public ShotFolder Folder { get; }

    public bool IsMissing { get; }

    public string Shown => Folder.Label.Trim().Length > 0 ? Folder.Label : Folder.Path;

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

public sealed class ShotRowViewModel : ObservableObject
{
    private readonly ScreenshotViewModel _owner;

    public ShotRowViewModel(Shot shot, bool kept, ScreenshotViewModel owner)
    {
        Shot = shot;
        _kept = kept;
        _owner = owner;
    }

    public Shot Shot { get; }

    private bool _kept;

    public string Path => Shot.Path;

    public string Name => System.IO.Path.GetFileName(Shot.Path);

    public string Group => Shot.Group;

    public string SizeText => Shots.Humanise(Shot.Size);

    public string TakenText => Shot.TakenUtc.ToLocalTime().ToString("g", ScreenshotViewModel.Culture);

    public bool IsDuplicate => Shot.IsDuplicate;

    public string BurstText => Shot.Burst > 1
        ? MeowsText.Current.Format("screenshot.burst", Shot.Burst)
        : "";

    public bool Kept
    {
        get => _kept;
        set
        {
            if (_kept == value)
                return;
            _kept = value;
            OnPropertyChanged();
            _owner.KeepChanged(this);
        }
    }

    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// Screenshot: every game screenshot sorted by game, identical copies named, bursts grouped,
/// best-of picks kept and handed to Scruff. It never deletes by itself: everything that goes
/// goes through the Recycle Bin, said out loud first.
/// </summary>
public sealed class ScreenshotViewModel : ObservableObject, IDisposable, ISearchable, IHandoffTarget, IGlanceable, IActionTarget
{
    public static readonly IReadOnlyList<string> Sorts =
        ["screenshot.sort.newest", "screenshot.sort.largest", "screenshot.sort.name"];

    private readonly IMeowsHost _host;
    private readonly string _profileRoot;
    private readonly string _appDataRoot;
    private readonly Func<IReadOnlyList<string>> _steamLibraries;
    private readonly ScreenshotSettings _settings;
    private readonly HashSet<string> _kept;
    private readonly LanguageWatch _language;
    private IBackgroundTask? _scan;

    private List<Shot> _all = [];
    private ShotRowViewModel? _selected;
    private FolderRowViewModel? _selectedFolder;
    private string? _status;
    private string? _errorMessage;
    private bool _isScanning;
    private bool _hasRead;
    private int _sortIndex;
    private int _groupIndex;

    public ScreenshotViewModel(IMeowsHost host) : this(host,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        SteamLibrary.LibraryFolders)
    {
    }

    /// <summary>With the roots and libraries supplied from elsewhere, which is how a test hands it fake folders.</summary>
    public ScreenshotViewModel(IMeowsHost host, string profileRoot, string appDataRoot, Func<IReadOnlyList<string>> steamLibraries)
    {
        _host = host;
        _profileRoot = profileRoot;
        _appDataRoot = appDataRoot;
        _steamLibraries = steamLibraries;
        _settings = host.LoadSettings<ScreenshotSettings>() ?? new ScreenshotSettings();
        _kept = new HashSet<string>(_settings.KeptPaths, StringComparer.OrdinalIgnoreCase);

        RefreshCommand = new RelayCommand(Refresh, () => !IsScanning);
        RecycleDuplicatesCommand = new RelayCommand(() => _ = RecycleDuplicatesAsync(), () => !IsScanning && _all.Any(s => s.IsDuplicate));
        SendKeptCommand = new RelayCommand(SendKept, () => KeptPaths().Count > 0);
        OpenCommand = new RelayCommand(() => Open(Selected?.Path), () => Selected is not null);
        RevealCommand = new RelayCommand(() => Reveal(Selected?.Path), () => Selected is not null);
        RecycleOneCommand = new RelayCommand(() => _ = RecycleOneAsync(), () => Selected is not null);
        AddFolderCommand = new RelayCommand(() => _ = AddFolderAsync());
        RemoveFolderCommand = new RelayCommand(RemoveFolder, () => SelectedFolder is not null);

        RebuildFolders();

        _language = new LanguageWatch(Retranslate);

        // A fresh tab looks around first: the popular folders arrive switched on by themselves.
        if (_settings.Folders.Count == 0)
            Discover();
        else
            Refresh();
    }

    /// <summary>Dates as the window's language writes them, not as the machine does.</summary>
    public static CultureInfo Culture => MeowsText.Current.Language == "de"
        ? CultureInfo.GetCultureInfo("de-DE")
        : CultureInfo.GetCultureInfo("en-GB");

    public ObservableCollection<ShotRowViewModel> Rows { get; } = [];

    public ObservableCollection<FolderRowViewModel> Folders { get; } = [];

    public ObservableCollection<string> Groups { get; } = [];

    public IReadOnlyList<string> SortChoices => Sorts.Select(k => MeowsText.Current[k]).ToList();

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

    public int GroupIndex
    {
        get => _groupIndex;
        set
        {
            if (value == _groupIndex)
                return;
            _groupIndex = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand RecycleDuplicatesCommand { get; }

    public RelayCommand SendKeptCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand RecycleOneCommand { get; }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand RemoveFolderCommand { get; }

    public ShotRowViewModel? Selected
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

    public bool CanReachScruff => _host.Handoff.CanReach(KnownPlugins.Scruff);

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                RecycleDuplicatesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["screenshot.status.ready"];
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

    /// <summary>The whole library in one line, from the last scan.</summary>
    public string Summary => Shots.SummaryOf(_settings.LastSummary, _host.Text);

    /// <summary>The Home line: what the library holds, and what copies hold. Red while copies do.</summary>
    public Glance? Glance() => Shots.GlanceOf(_settings.LastSummary, _host.Text);

    public string EmptyText => _hasRead && _all.Count == 0
        ? _host.Text["screenshot.empty.none"]
        : _host.Text["screenshot.empty"];

    /// <summary>Reads every switched-on folder again. Images only; nothing is hashed yet.</summary>
    private void Refresh()
    {
        if (IsScanning)
            return;
        ErrorMessage = null;
        IsScanning = true;
        Status = _host.Text["screenshot.status.reading"];

        _scan = _host.Background.Run(_host.Text["screenshot.task"], async context =>
        {
            var shots = await Task.Run(() => RunScan(_settings.Folders), context.Token);
            await Dispatcher.UIThread.InvokeAsync(() => Show(shots));
        });
    }

    /// <summary>The scan without the scheduling: what --do runs with no window.</summary>
    public static List<Shot> RunScan(IReadOnlyList<ShotFolder> folders) => Shots.Scan(folders);

    /// <summary>Puts a finished scan on screen. Separate so a test can hand it shots directly.</summary>
    public void Show(List<Shot> shots)
    {
        _all = shots;
        _hasRead = true;
        IsScanning = false;

        // Best-of picks for files that are gone are forgotten, quietly.
        var present = new HashSet<string>(shots.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        _kept.RemoveWhere(p => !present.Contains(p));
        _settings.KeptPaths = _kept.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        _settings.LastSummary = Shots.Summarise(shots, DateTime.UtcNow);
        Save();

        Rebuild();
        var summary = _settings.LastSummary;
        Status = _host.Text.Format("screenshot.status.read",
            summary.Shots, Shots.Humanise(summary.Bytes),
            summary.Duplicates, Shots.Humanise(summary.DuplicateBytes),
            DateTime.Now.ToString("HH:mm"));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EmptyText));
        _host.Store.Record("scanned", _host.Text.Format("screenshot.records.scanned",
            summary.Shots, Shots.Humanise(summary.Bytes)));
        _host.Log($"Screenshot saw {summary.Shots} shot(s), {summary.Duplicates} duplicate(s).");
    }

    private void Rebuild()
    {
        var groups = _all.Select(s => s.Group).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
        Groups.Clear();
        Groups.Add(_host.Text["screenshot.group.all"]);
        foreach (var group in groups)
            Groups.Add(group);
        if (_groupIndex < 0 || _groupIndex > groups.Count)
        {
            _groupIndex = 0;
            OnPropertyChanged(nameof(GroupIndex));
        }

        IEnumerable<Shot> shown = _all;
        if (GroupIndex > 0)
        {
            var group = groups[GroupIndex - 1];
            shown = shown.Where(s => string.Equals(s.Group, group, StringComparison.CurrentCultureIgnoreCase));
        }

        shown = SortIndex switch
        {
            1 => shown.OrderByDescending(s => s.Size).ThenByDescending(s => s.TakenUtc),
            2 => shown.OrderBy(s => System.IO.Path.GetFileName(s.Path), StringComparer.CurrentCultureIgnoreCase),
            _ => shown.OrderBy(s => s.Group, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(s => s.TakenUtc),
        };

        var keep = Selected?.Path;
        Rows.Clear();
        foreach (var shot in shown)
            Rows.Add(new ShotRowViewModel(shot, _kept.Contains(shot.Path), this));
        Selected = Rows.FirstOrDefault(r => string.Equals(r.Path, keep, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(IsEmpty));
        RecycleDuplicatesCommand.RaiseCanExecuteChanged();
        SendKeptCommand.RaiseCanExecuteChanged();
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

    /// <summary>Folders found, merged in, and read. The first thing a fresh tab does is look around.</summary>
    public void Discover()
    {
        IReadOnlyList<string> libraries;
        try
        {
            libraries = _steamLibraries();
        }
        catch (Exception)
        {
            libraries = [];
        }

        var added = Shots.MergeFolders(_settings.Folders,
            Shots.FindSources(_profileRoot, _appDataRoot, libraries));
        Save();
        RebuildFolders();
        if (added > 0)
            Status = _host.Text.Format("screenshot.status.found", added);
        Refresh();
    }

    /// <summary>A tick on a row: a best-of pick, kept across rescans until its file is gone.</summary>
    internal void KeepChanged(ShotRowViewModel row)
    {
        if (row.Kept)
        {
            _kept.Add(row.Path);
            _host.Store.Record("kept", System.IO.Path.GetFileName(row.Path));
        }
        else
        {
            _kept.Remove(row.Path);
        }
        _settings.KeptPaths = _kept.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        Save();
        SendKeptCommand.RaiseCanExecuteChanged();
    }

    internal void FoldersTouched()
    {
        Save();
        Refresh();
    }

    private IReadOnlyList<string> KeptPaths() =>
        _kept.Where(p => File.Exists(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Best-of picks handed to Scruff, which is what a posting queue wants: files.</summary>
    private void SendKept()
    {
        var kept = KeptPaths();
        if (kept.Count == 0)
            return;

        var handoff = Handoff.Files(kept);
        if (!_host.Handoff.Send(KnownPlugins.Scruff, handoff))
        {
            ErrorMessage = _host.Text["screenshot.error.noscruff"];
            return;
        }

        ErrorMessage = null;
        _host.Store.Record("sent", _host.Text.Format("screenshot.records.sent", kept.Count));
        handoff.Answer(_host.Text.Format("screenshot.status.sent", kept.Count));
        Status = _host.Text.Format("screenshot.status.sent", kept.Count);
    }

    private async Task RecycleOneAsync()
    {
        if (Selected is not { } selected)
            return;

        var outcome = await Task.Run(() => RecycleBin.Send([selected.Path]));
        if (!outcome.Succeeded)
        {
            ErrorMessage = outcome.FailureReason ?? _host.Text.Format("screenshot.error.gone", selected.Path);
            return;
        }

        ErrorMessage = null;
        _kept.Remove(selected.Path);
        _host.Store.Record("recycled", System.IO.Path.GetFileName(selected.Path));
        Refresh();
        Status = _host.Text.Format("screenshot.status.recycled", 1);
    }

    private async Task RecycleDuplicatesAsync()
    {
        var victims = Shots.DuplicateVictims(_all);
        if (victims.Count == 0)
            return;

        var paths = victims.Select(v => v.Path).ToList();
        var outcome = await Task.Run(() => RecycleBin.Send(paths));
        if (!outcome.Succeeded)
        {
            ErrorMessage = outcome.FailureReason ?? _host.Text.Format("screenshot.status.recycled", 0);
            return;
        }

        ErrorMessage = null;
        foreach (var path in paths)
            _kept.Remove(path);
        var freed = victims.Sum(v => v.Size);
        _host.Store.Record("recycled", _host.Text.Format("screenshot.records.recycled", victims.Count, Shots.Humanise(freed)));
        Refresh();
        Status = _host.Text.Format("screenshot.status.recycled", victims.Count);
    }

    /// <summary>
    /// A rule's "recycle the copies": identical bytes go to the Bin, the newest of each set
    /// stays. Declines when there is nothing to do or the Bin says no.
    /// </summary>
    public Task<string> Perform(ActionRequest request, CancellationToken token)
    {
        if (request.Action != ScreenshotPlugin.RecycleAction)
            throw new ActionDeclinedException(_host.Text.Format("screenshot.action.unknown", request.Action));

        var victims = Shots.DuplicateVictims(RunScan(_settings.Folders));
        if (victims.Count == 0)
            throw new ActionDeclinedException(_host.Text["screenshot.status.nodups"]);

        var outcome = RecycleBin.Send(victims.Select(v => v.Path).ToList());
        if (!outcome.Succeeded)
            throw new ActionDeclinedException(outcome.FailureReason ?? _host.Text.Format("screenshot.status.recycled", 0));

        foreach (var victim in victims)
            _kept.Remove(victim.Path);
        var freed = victims.Sum(v => v.Size);
        _host.Store.Record("recycled", _host.Text.Format("screenshot.records.recycled", victims.Count, Shots.Humanise(freed)));
        Save();

        return Task.FromResult(_host.Text.Format("screenshot.status.recycled", victims.Count));
    }

    public bool Accepts(Handoff handoff) =>
        handoff.Verb == ScreenshotPlugin.ShowVerb && handoff.Note is { Length: > 0 } &&
        _all.Any(s => string.Equals(s.Path, handoff.Note, StringComparison.OrdinalIgnoreCase));

    public void Receive(Handoff handoff)
    {
        if (!Accepts(handoff))
            return;

        // From Ctrl+K while Screenshot was off: the hit, now that there is a list to select in.
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
            ErrorMessage = _host.Text.Format("screenshot.error.gone", path);
            return;
        }

        ErrorMessage = null;

        try
        {
            Explorer.Open(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("screenshot.error.open", path, ex.Message);
        }
    }

    private void Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            ErrorMessage = _host.Text.Format("screenshot.error.gone", path);
            return;
        }

        ErrorMessage = null;

        try
        {
            Explorer.Reveal(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("screenshot.error.open", path, ex.Message);
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
            _host.Log(LogLevel.Warning, $"Could not save Screenshot settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the shots: by file or group name, from what is already loaded. Landing on one selects it.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var row in Rows)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, row.Name, row.Group))
                continue;

            var chosen = row;
            hits.Add(new SearchHit(chosen.Name, chosen.Group, () => Selected = chosen));
        }

        return hits;
    }

    public void Dispose()
    {
        _scan?.Cancel();
        _language.Dispose();
    }

    // ---- picking a folder by hand, through the host's dialog rather than a TopLevel of our own ----

    private async Task AddFolderAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["screenshot.dialog.folder"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;

            if (_settings.Folders.Any(f => string.Equals(f.Path, picked, StringComparison.OrdinalIgnoreCase)))
            {
                Status = _host.Text.Format("screenshot.status.already", picked);
                return;
            }

            _settings.Folders.Add(new ShotFolder
            {
                Path = picked,
                Label = System.IO.Path.GetFileName(picked.TrimEnd('\\', '/')),
                Enabled = true,
            });
            Save();
            RebuildFolders();
            Refresh();
            Status = _host.Text.Format("screenshot.status.added", picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    private void Retranslate()
    {
        foreach (var row in Rows)
            row.Reread();
        foreach (var folder in Folders)
            folder.Reread();

        OnEverythingChanged();
        OnPropertyChanged(nameof(Summary));
    }
}
