using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Molt.Services;

namespace Meows.Plugins.Molt.ViewModels;

public sealed class MoltSettings
{
    public string? BuildRoot { get; set; }

    public int TempOlderThanDays { get; set; } = 7;

    public bool Permanent { get; set; }
}

/// <summary>One file or folder on a card, which can be left out of what the card sheds.</summary>
public sealed class SheddablePartViewModel(string path, long size, Action changed) : ObservableObject
{
    private bool _isPicked = true;

    public string Path { get; } = path;

    public long Size { get; } = size;

    public string SizeText => FolderSize.Humanise(Size);

    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            if (SetField(ref _isPicked, value))
                changed();
        }
    }
}

public sealed class SheddableViewModel : ObservableObject
{
    private bool _isPicked;
    private List<SheddablePartViewModel>? _parts;

    public SheddableViewModel(Sheddable item) => Item = item;

    public Sheddable Item { get; }

    /// <summary>
    /// Every file and folder on the card, each ticked to start with. A card is all-or-nothing
    /// otherwise, and "bin and obj under F:\Kadsenstuff" can hold one folder somebody wants kept.
    /// Built the first time it is opened, since the temp card can hold thousands.
    /// </summary>
    public IReadOnlyList<SheddablePartViewModel> Parts => _parts ??=
        [.. Item.Paths.Select(p => new SheddablePartViewModel(p, Item.Sizes.GetValueOrDefault(p), PartsChanged))];

    /// <summary>More than one thing on the card, so there is something to leave out.</summary>
    public bool HasParts => Item.Paths.Count > 1;

    /// <summary>What the card would shed now: everything, unless some of it was unticked.</summary>
    public IReadOnlyList<string> PickedPaths => _parts is null ? Item.Paths : [.. _parts.Where(p => p.IsPicked).Select(p => p.Path)];

    public long PickedSize => _parts is null ? Item.Size : _parts.Where(p => p.IsPicked).Sum(p => p.Size);

    /// <summary>A part ticked or unticked: the card's size and count, and through them the tab's totals.</summary>
    private void PartsChanged()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>
    /// The catalogue hands us keys, so this is where they become words. A real path in Where is
    /// not a key and comes back untouched, which is the point of looking everything up the same
    /// way rather than only some of it.
    /// </summary>
    public string Name => Item.NameValues.Length == 0
        ? MeowsText.Current[Item.Name]
        : MeowsText.Current.Format(Item.Name, Item.NameValues);

    public string Where => MeowsText.Current[Item.Where];

    public string What => MeowsText.Current[Item.What];

    public string Cost => MeowsText.Current[Item.Cost];

    public string SizeText => FolderSize.Humanise(PickedSize);

    public string CountText
    {
        get
        {
            var picked = PickedPaths.Count;
            if (picked != Item.Paths.Count)
                return MeowsText.Current.Format("molt.items.some", picked, Item.Paths.Count);
            return picked == 1 ? MeowsText.Current["molt.items.one"] : MeowsText.Current.Format("molt.items.many", picked);
        }
    }

    public bool IsPicked
    {
        get => _isPicked;
        set => SetField(ref _isPicked, value);
    }
}

public sealed class MoltViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable
{
    private readonly IMeowsHost _host;
    private MoltSettings _settings;
    private IBackgroundTask? _scan;

    private string? _status;
    private string? _errorMessage;
    private bool _isScanning;
    private bool _isAsking;

    /// <summary>
    /// Text worked out in code rather than bound with {m:Tr} has to be read again when the
    /// language changes. Nothing moves, but everything reads differently.
    /// </summary>
    private readonly LanguageWatch _language;

    public MoltViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<MoltSettings>() ?? new MoltSettings();

        ScanCommand = new RelayCommand(StartScan, () => !IsScanning);
        CancelCommand = new RelayCommand(() => _scan?.Cancel(), () => IsScanning);
        ShedCommand = new RelayCommand(Ask, () => PickedCount > 0 && !IsScanning);
        ConfirmCommand = new RelayCommand(() => _ = ShedAsync(), () => IsAsking);
        CancelShedCommand = new RelayCommand(() => IsAsking = false, () => IsAsking);
        PickAllCommand = new RelayCommand(() => SetAllPicked(true));
        PickNoneCommand = new RelayCommand(() => SetAllPicked(false));
        ExploreCommand = new RelayCommand(p => Open((p as SheddableViewModel)?.Where));

        StartScan();
        _language = new LanguageWatch(OnEverythingChanged);
    }

    public ObservableCollection<SheddableViewModel> Items { get; } = new();

    public RelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ShedCommand { get; }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelShedCommand { get; }

    public RelayCommand PickAllCommand { get; }

    public RelayCommand PickNoneCommand { get; }

    public RelayCommand ExploreCommand { get; }

    public string BuildRoot => _settings.BuildRoot ?? "";

    public bool HasBuildRoot => !string.IsNullOrWhiteSpace(BuildRoot);

    /// <summary>
    /// Off by default. The bin is the house rule everywhere else in Meows, and someone who wants
    /// the room back this second should have to say so rather than have it assumed.
    /// </summary>
    public bool Permanent
    {
        get => _settings.Permanent;
        set
        {
            if (_settings.Permanent == value)
                return;
            _settings.Permanent = value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(ConfirmDetail));
        }
    }

    public string ModeText => _host.Text[Permanent ? "molt.mode.permanent" : "molt.mode.bin"];

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetField(ref _isScanning, value))
                return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ShedCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAsking
    {
        get => _isAsking;
        private set
        {
            if (!SetField(ref _isAsking, value))
                return;
            OnPropertyChanged(nameof(ConfirmPrompt));
            OnPropertyChanged(nameof(ConfirmDetail));
            ConfirmCommand.RaiseCanExecuteChanged();
            CancelShedCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? MeowsText.Current["molt.status.start"];
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

    public int PickedCount => Items.Count(i => i.IsPicked);

    public long PickedSize => Items.Where(i => i.IsPicked).Sum(i => i.PickedSize);

    public string PickedText => PickedCount == 0
        ? _host.Text["molt.picked.none"]
        : _host.Text.Format("molt.picked.some", PickedCount, FolderSize.Humanise(PickedSize));

    public string TotalText => Items.Count == 0
        ? ""
        : _host.Text.Format("molt.total", FolderSize.Humanise(Items.Sum(i => i.Item.Size)));

    private string Lots => PickedCount == 1
        ? _host.Text["molt.lots.one"]
        : _host.Text.Format("molt.lots.many", PickedCount);

    public string ConfirmPrompt => _host.Text.Format(
        Permanent ? "molt.confirm.permanent" : "molt.confirm.bin",
        Lots, FolderSize.Humanise(PickedSize));

    public string ConfirmDetail => _host.Text[Permanent ? "molt.detail.permanent" : "molt.detail.bin"];

    public void Repick()
    {
        OnPropertyChanged(nameof(PickedCount));
        OnPropertyChanged(nameof(PickedSize));
        OnPropertyChanged(nameof(PickedText));
        ShedCommand.RaiseCanExecuteChanged();
    }

    public void SetBuildRoot(string folder)
    {
        _settings.BuildRoot = folder;
        Save();
        OnPropertyChanged(nameof(BuildRoot));
        OnPropertyChanged(nameof(HasBuildRoot));
        StartScan();
    }

    private void SetAllPicked(bool picked)
    {
        foreach (var item in Items)
            item.IsPicked = picked;
        Repick();
    }

    private void StartScan()
    {
        if (IsScanning)
            return;

        ErrorMessage = null;
        IsScanning = true;
        Status = _host.Text["molt.status.measuring"];

        var options = new MoltOptions
        {
            TempOlderThanDays = _settings.TempOlderThanDays,
            BuildRoot = _settings.BuildRoot,
        };

        _scan = _host.Background.Run(_host.Text["molt.task.measure"], async context =>
        {
            var progress = new Progress<string>(where => Status = where);
            var found = await Task.Run(
                () => MoltCatalog.Build(options, progress, context.Token), context.Token);

            await Dispatcher.UIThread.InvokeAsync(() => Show(found));
        });
    }

    /// <summary>Puts a finished catalogue on screen. Separate so it can be exercised on its own.</summary>
    public void Show(IReadOnlyList<Sheddable> found)
    {
        foreach (var old in Items)
            old.PropertyChanged -= OnItemChanged;
        Items.Clear();

        foreach (var item in found.OrderByDescending(f => f.Size))
        {
            var row = new SheddableViewModel(item);

            // Without this the box ticks and nothing else happens: the count stays at nothing
            // picked and the button stays dead, which is exactly how it behaved before.
            row.PropertyChanged += OnItemChanged;
            Items.Add(row);
        }

        IsScanning = false;
        Status = Items.Count == 0 ? _host.Text["molt.status.nothing"] : "";
        OnPropertyChanged(nameof(TotalText));
        Repick();
        _host.Log($"Molt found {Items.Count} thing(s) worth {FolderSize.Humanise(Items.Sum(i => i.Item.Size))}");
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A card ticked or unticked, or one of its folders left out.
        if (e.PropertyName is nameof(SheddableViewModel.IsPicked) or nameof(SheddableViewModel.SizeText))
            Repick();
    }

    private void Ask()
    {
        if (PickedCount == 0)
            return;

        // Always asks. There is no turning this one off, because permanent is on the table.
        IsAsking = true;
    }

    /// <summary>
    /// On a worker thread: a cache can be gigabytes in tens of thousands of files, and deleting
    /// them on the window's thread stood the whole of Meows still until it was done.
    /// </summary>
    private async Task ShedAsync()
    {
        IsAsking = false;

        var picked = Items.Where(i => i.IsPicked).ToList();
        if (picked.Count == 0)
            return;

        var paths = picked.SelectMany(i => i.PickedPaths).ToList();
        var expected = picked.Sum(i => i.PickedSize);
        if (paths.Count == 0)
            return;
        var mode = Permanent ? ShedMode.Permanent : ShedMode.RecycleBin;

        IsScanning = true;
        Status = _host.Text["molt.status.shedding"];
        ShedResult result;
        try
        {
            result = await Task.Run(() => Shedder.Shed(paths, mode, expected));
        }
        finally
        {
            IsScanning = false;
        }

        // Into the bin, recorded the shared way so History can put it back; outright, nothing to put back.
        if (mode == ShedMode.RecycleBin)
            Recycled.Record(_host.Store, paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList(),
                detail: _host.Text.Format("molt.status.done.bin", FolderSize.Humanise(result.Freed)));

        // Whole sentences rather than a size glued to a phrase. German puts the verb somewhere
        // else entirely, so there is nothing to glue it to.
        var freed = FolderSize.Humanise(result.Freed);
        var wording = Permanent ? "permanent" : "bin";
        Status = result.Failed == 0
            ? _host.Text.Format($"molt.status.done.{wording}", freed)
            : _host.Text.Format($"molt.status.left.{wording}", freed, result.Failed);

        if (result.FailureReason is not null)
            ErrorMessage = result.FailureReason;

        _host.Log($"Molt shed {result.Removed} item(s) {(Permanent ? "outright" : "to the Recycle Bin")}, " +
                  $"{freed}, {result.Failed} failed");
        _host.Notifications.Post(NotificationSeverity.Info, _host.Text["molt.notify.finished"],
            _host.Text.Format($"molt.notify.{wording}", freed));

        StartScan();
    }

    private void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            Explorer.Open(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("molt.error.open", path, ex.Message);
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
            _host.Log(LogLevel.Warning, $"Could not save Molt settings: {ex.Message}");
        }
    }

    /// <summary>On the Home card: how much could be shed, once it has been measured.</summary>
    public Glance? Glance() => TotalText.Length == 0 ? null : new Glance(TotalText);

    /// <summary>
    /// Ctrl+K reaching into what can be shed, by name or by what it is. Nothing in Molt is
    /// selected, so landing on one opens its folder, which is what the row's own button does.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var item in Items)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, item.Name, item.What, item.Where))
                continue;

            var chosen = item;
            hits.Add(new SearchHit(item.Name, $"{item.What} · {item.SizeText}", () => ExploreCommand.Execute(chosen)));
        }

        return hits;
    }

    public void Dispose()
    {
        _language.Dispose();
        foreach (var item in Items)
            item.PropertyChanged -= OnItemChanged;

        _scan?.Cancel();
    }

    // ---- picking, through the host's dialogs rather than a TopLevel of our own ----

    private RelayCommand? _pickBuildRootCommand;

    public RelayCommand PickBuildRootCommand => _pickBuildRootCommand ??= new RelayCommand(() => _ = PickBuildRootAsync());

    private async Task PickBuildRootAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["molt.dialog.folder"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;
            SetBuildRoot(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }
}
