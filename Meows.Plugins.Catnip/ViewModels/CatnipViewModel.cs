using System.Collections.ObjectModel;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Catnip.Services;

namespace Meows.Plugins.Catnip.ViewModels;

public sealed class CatnipSettings
{
    /// <summary>Folders to walk. Empty means the Downloads folder.</summary>
    public List<string> Roots { get; set; } = [];

    /// <summary>Only files not opened since they arrived, rather than everything untouched for the window.</summary>
    public bool OnlyNeverOpened { get; set; } = true;

    /// <summary>Untouched for at least this long.</summary>
    public int OlderThanDays { get; set; } = 90;

    /// <summary>Anything smaller is not worth a row.</summary>
    public int MinMegabytes { get; set; } = 1;

    public bool SkipSystemFolders { get; set; } = true;
}

/// <summary>One folder in the left column.</summary>
public sealed class RootViewModel(string path)
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : Path;
}

/// <summary>One file in the middle column: what it is, how big, and how long it has sat there.</summary>
public sealed class NeglectedViewModel(NeglectedFile file, DateTime now) : ObservableObject
{
    public NeglectedFile File { get; } = file;

    public string Path => File.Path;

    public string Name => File.Name;

    public string Folder => File.Folder;

    public string SizeText => Neglect.Humanise(File.Size);

    public bool NeverOpened => File.NeverOpened;

    /// <summary>"never opened, arrived 7 months ago" or "last opened 2 years ago".</summary>
    public string WhenText => File.NeverOpened
        ? MeowsText.Current.Format("catnip.when.never", Neglect.Ago(File.Arrived, now, k => MeowsText.Current[k]))
        : MeowsText.Current.Format("catnip.when.opened", Neglect.Ago(File.Accessed, now, k => MeowsText.Current[k]));

    public string ArrivedText => File.Arrived.ToString("d MMM yyyy");

    public string AccessedText => File.Accessed.ToString("d MMM yyyy");

    public void Reread() => OnEverythingChanged();
}

/// <summary>
/// What was downloaded and never opened. Chonk sorts by size and Litter by age; this sorts by
/// the last-access stamp, which is the only record of what was acquired in a burst of enthusiasm
/// and never touched since. Reading it is free: the walk takes sizes and dates from directory
/// metadata and opens nothing, so the stamps stay true.
/// </summary>
public sealed class CatnipViewModel : ObservableObject, IDisposable, ISearchable, IHandoffTarget
{
    private const int ShowAtMost = 500;

    private readonly IMeowsHost _host;
    private readonly CatnipSettings _settings;
    private readonly LanguageWatch _language;
    private IBackgroundTask? _scan;
    private int _generation;

    private IReadOnlyList<NeglectedFile> _found = [];
    private NeglectReport? _report;
    private DateTime _scannedAt;
    private RootViewModel? _selectedRoot;
    private NeglectedViewModel? _selected;
    private string? _status;
    private string? _errorMessage;
    private bool _isScanning;

    public CatnipViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<CatnipSettings>() ?? new CatnipSettings();

        ScanCommand = new RelayCommand(Scan, () => !IsScanning && Roots.Count > 0);
        CancelCommand = new RelayCommand(() => _scan?.Cancel(), () => IsScanning);
        RemoveRootCommand = new RelayCommand(RemoveRoot, () => SelectedRoot is not null);
        OpenCommand = new RelayCommand(() => Open(Selected), () => Selected is not null);
        RevealCommand = new RelayCommand(() => Reveal(Selected), () => Selected is not null);
        RecycleCommand = new RelayCommand(Recycle, () => Chosen.Count > 0 && !IsScanning);
        AskPurrgeCommand = new RelayCommand(AskPurrge, () => Chosen.Count > 0 && CanReachPurrge);

        // Several rows at once: the list is long and binning them one at a time is a chore.
        Selection = new SelectionModel<NeglectedViewModel> { SingleSelect = false, Source = Files };
        Selection.SelectionChanged += (_, _) =>
        {
            _selected = Selection.SelectedItems.FirstOrDefault();
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ChosenCount));
            OnPropertyChanged(nameof(ChosenText));
            OpenCommand.RaiseCanExecuteChanged();
            RevealCommand.RaiseCanExecuteChanged();
            RecycleCommand.RaiseCanExecuteChanged();
            AskPurrgeCommand.RaiseCanExecuteChanged();
        };

        foreach (var root in EffectiveRoots())
            Roots.Add(new RootViewModel(root));

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var file in Files) file.Reread();
        });
    }

    public ObservableCollection<RootViewModel> Roots { get; } = [];

    public ObservableCollection<NeglectedViewModel> Files { get; } = [];

    public RelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand RemoveRootCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand RecycleCommand { get; }

    public RelayCommand AskPurrgeCommand { get; }

    /// <summary>The rows picked, one or many. The list binds to it; the verbs read it.</summary>
    public SelectionModel<NeglectedViewModel> Selection { get; }

    /// <summary>What the verbs act on: every picked row, in list order.</summary>
    public IReadOnlyList<NeglectedViewModel> Chosen => Selection.SelectedItems.Where(f => f is not null).Select(f => f!).ToList();

    public int ChosenCount => Selection.Count;

    /// <summary>"3 files, 41 MB" for the right column when more than one is picked.</summary>
    public string ChosenText => ChosenCount <= 1 ? "" : _host.Text.Format("catnip.chosen", ChosenCount, Neglect.Humanise(Chosen.Sum(f => f.File.Size)));

    public bool CanReachPurrge => _host.Handoff.CanReach(KnownPlugins.Purrge);

    public RootViewModel? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (SetField(ref _selectedRoot, value))
                RemoveRootCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The first of what is picked; setting it picks that one alone.</summary>
    public NeglectedViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
                return;
            Selection.Clear();
            if (value is not null && Files.IndexOf(value) is var i && i >= 0)
                Selection.Select(i);
            else
            {
                _selected = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetField(ref _isScanning, value))
                return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            RecycleCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => Files.Count == 0;

    public bool HasScanned => _report is not null;

    public string Status
    {
        get => _status ?? _host.Text["catnip.status.start"];
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

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    /// <summary>"1,204 files, 38 GB, not opened since they arrived; the oldest from Aug 2023."</summary>
    public string Headline
    {
        get
        {
            if (_report is null)
                return "";
            var shown = Sifted();
            if (shown.Count == 0)
                return _host.Text.Format("catnip.headline.none", _report.Files.Count, _settings.OlderThanDays);
            var oldest = shown[0];
            return _host.Text.Format(_settings.OnlyNeverOpened ? "catnip.headline.never" : "catnip.headline.idle",
                shown.Count, Neglect.Humanise(shown.Sum(f => f.Size)), _settings.OlderThanDays, oldest.Accessed.ToString("MMM yyyy"));
        }
    }

    public string TruncatedText => Sifted().Count > ShowAtMost ? _host.Text.Format("catnip.truncated", ShowAtMost, Sifted().Count) : "";

    // ---- dials ----

    public bool OnlyNeverOpened
    {
        get => _settings.OnlyNeverOpened;
        set
        {
            if (_settings.OnlyNeverOpened == value)
                return;
            _settings.OnlyNeverOpened = value;
            SaveSettings();
            OnPropertyChanged();
            Rebuild();
        }
    }

    public int OlderThanDays
    {
        get => _settings.OlderThanDays;
        set
        {
            if (value < 0 || _settings.OlderThanDays == value)
                return;
            _settings.OlderThanDays = value;
            SaveSettings();
            OnPropertyChanged();
            Rebuild();
        }
    }

    public int MinMegabytes
    {
        get => _settings.MinMegabytes;
        set
        {
            if (value < 0 || _settings.MinMegabytes == value)
                return;
            _settings.MinMegabytes = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    // ---- roots ----

    private IReadOnlyList<string> EffectiveRoots()
    {
        if (_settings.Roots.Count > 0)
            return _settings.Roots;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? [downloads] : [];
    }

    public void AddRoot(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || Roots.Any(r => string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase)))
            return;
        Roots.Add(new RootViewModel(folder));
        _settings.Roots = Roots.Select(r => r.Path).ToList();
        SaveSettings();
        ScanCommand.RaiseCanExecuteChanged();
    }

    private void RemoveRoot()
    {
        if (SelectedRoot is not { } root)
            return;
        Roots.Remove(root);
        SelectedRoot = null;
        _settings.Roots = Roots.Select(r => r.Path).ToList();
        SaveSettings();
        ScanCommand.RaiseCanExecuteChanged();
    }

    public bool Accepts(Handoff handoff) => handoff.Verb == HandoffVerbs.Folder && handoff.Paths.Count == 1 && Directory.Exists(handoff.Paths[0]);

    /// <summary>A folder from another tab is added to the list and walked at once.</summary>
    public void Receive(Handoff handoff)
    {
        if (!Accepts(handoff))
            return;
        AddRoot(handoff.Paths[0]);
        Scan(handoff);
    }

    // ---- the walk ----

    private void Scan() => Scan(null);

    private void Scan(Handoff? asked)
    {
        if (IsScanning || Roots.Count == 0)
            return;
        IsScanning = true;
        ErrorMessage = null;
        Status = _host.Text["catnip.status.scanning"];
        var generation = ++_generation;
        var roots = Roots.Select(r => r.Path).ToList();
        var skip = _settings.SkipSystemFolders;
        var minBytes = _settings.MinMegabytes * 1_000_000L;

        _scan = _host.Background.Run(_host.Text["catnip.task"], async context =>
        {
            try
            {
                var report = await Task.Run(
                    () => Neglect.Scan(roots, skip, minBytes, folder => context.Report(_host.Text.Format("catnip.progress", folder)), context.Token),
                    context.Token);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _generation)
                        return;
                    ShowWalked(report);
                    asked?.Answer(_host.Text.Format("catnip.reply", Sifted().Count));
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Status = _host.Text["catnip.status.cancelled"]);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _host.Text.Format("catnip.error.scan", ex.Message));
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsScanning = false);
            }
        });
    }

    /// <summary>A finished walk, shown: the list rebuilt, the status and the history line written.</summary>
    public void ShowWalked(NeglectReport report)
    {
        _report = report;
        _found = report.Files;
        _scannedAt = DateTime.Now;
        Rebuild();
        Status = _host.Text.Format("catnip.status.scanned", report.Files.Count, report.Folders, _scannedAt.ToString("HH:mm"));
        var never = report.Files.Where(f => f.NeverOpened).ToList();
        _host.Store.Record("scan", string.Join("; ", Roots.Select(r => r.Path)),
            _host.Text.Format("catnip.journal.scan", never.Count, Neglect.Humanise(never.Sum(f => f.Size))));
    }

    private IReadOnlyList<NeglectedFile> Sifted() => Neglect.Sift(_found, _settings.OnlyNeverOpened, _settings.OlderThanDays, DateTime.Now);

    private void Rebuild()
    {
        var keep = Selected?.Path;
        Selection.Clear();
        Files.Clear();
        var now = DateTime.Now;
        foreach (var file in Sifted().Take(ShowAtMost))
            Files.Add(new NeglectedViewModel(file, now));
        Selection.Clear();
        Selected = Files.FirstOrDefault(f => string.Equals(f.Path, keep, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasScanned));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(TruncatedText));
    }

    // ---- the verbs ----

    /// <summary>Opening it is the one honest way off the list: the stamp moves, and the row goes.</summary>
    private void Open(NeglectedViewModel? file)
    {
        if (file is null)
            return;
        try
        {
            Explorer.Open(file.Path);
            Forget(file);
            Status = _host.Text.Format("catnip.status.opened", file.Name);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("catnip.error.open", file.Name, ex.Message);
        }
    }

    private void Reveal(NeglectedViewModel? file)
    {
        if (file is null)
            return;
        try
        {
            Explorer.Reveal(file.Path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("catnip.error.open", file.Name, ex.Message);
        }
    }

    /// <summary>Everything picked, to the Recycle Bin in one go, a History line each.</summary>
    private void Recycle()
    {
        var chosen = Chosen;
        if (chosen.Count == 0)
            return;
        var outcome = RecycleBin.Send(chosen.Select(f => f.Path).ToList());
        if (!outcome.Succeeded)
        {
            ErrorMessage = _host.Text.Format("catnip.error.recycle", chosen.Count == 1 ? chosen[0].Name : chosen.Count.ToString(), outcome.FailureReason ?? "");
            return;
        }

        var bytes = chosen.Sum(f => f.File.Size);
        foreach (var file in chosen)
            _host.Store.Record("recycled", file.Path, _host.Text.Format("catnip.status.recycled", file.Name, file.SizeText),
                new Dictionary<string, string> { ["size"] = file.File.Size.ToString() });
        Forget(chosen);
        Status = chosen.Count == 1
            ? _host.Text.Format("catnip.status.recycled", chosen[0].Name, chosen[0].SizeText)
            : _host.Text.Format("catnip.status.recycled.many", chosen.Count, Neglect.Humanise(bytes));
        _host.Log($"Catnip sent {chosen.Count} file(s) to the Recycle Bin, {Neglect.Humanise(bytes)}");
    }

    /// <summary>
    /// Before binning: is there another copy? Purrge walks the drives these sit on, opening only
    /// files of exactly their sizes, and answers in a sentence that lands on the status line.
    /// </summary>
    private void AskPurrge()
    {
        var chosen = Chosen;
        if (chosen.Count == 0)
            return;
        var handoff = Handoff.Files(chosen.Select(f => f.Path)) with
        {
            Reply = answer => Status = _host.Text.Format("catnip.status.purrge", answer),
        };
        if (!_host.Handoff.Send(KnownPlugins.Purrge, handoff))
            ErrorMessage = _host.Text["catnip.error.purrge"];
        else
            Status = _host.Text.Format("catnip.status.asking", chosen.Count);
    }

    /// <summary>Drops a file from what was found, without walking again.</summary>
    private void Forget(NeglectedViewModel file) => Forget([file]);

    private void Forget(IReadOnlyList<NeglectedViewModel> files)
    {
        var index = files.Select(f => Files.IndexOf(f)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var gone = new HashSet<NeglectedFile>(files.Select(f => f.File));
        _found = _found.Where(f => !gone.Contains(f)).ToList();
        Selection.Clear();
        foreach (var file in files)
            Files.Remove(file);
        Selected = Files.ElementAtOrDefault(Math.Min(index, Files.Count - 1));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Headline));
    }

    /// <summary>Ctrl+K: a neglected file by name or folder.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();
        foreach (var file in Files)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, file.Name, file.Folder))
                continue;
            var chosen = file;
            hits.Add(new SearchHit(file.Name, $"{file.SizeText} · {file.WhenText}", () => Selected = chosen));
        }
        return hits;
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not save Catnip settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _scan?.Cancel();
        _language.Dispose();
    }

    // ---- picking, through the host's dialogs rather than a TopLevel of our own ----

    private RelayCommand? _addRootCommand;

    public RelayCommand AddRootCommand => _addRootCommand ??= new RelayCommand(() => _ = AddRootsAsync());

    private async Task AddRootsAsync()
    {
        try
        {
            var picked = await _host.Pick.Folders(new PickOptions { Title = _host.Text["catnip.dialog.root"] });
            if (picked.Count == 0)
                return;
            foreach (var folder in picked)
                AddRoot(folder);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }
}
