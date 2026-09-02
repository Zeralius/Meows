using System.Collections.ObjectModel;
using System.Diagnostics;
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

public sealed class SheddableViewModel(Sheddable item) : ObservableObject
{
    private bool _isPicked;

    public Sheddable Item { get; } = item;

    public string Name => Item.Name;

    public string Where => Item.Where;

    public string What => Item.What;

    public string Cost => Item.Cost;

    public string SizeText => FolderSize.Humanise(Item.Size);

    public string CountText => Item.Paths.Count == 1 ? "1 item" : $"{Item.Paths.Count} items";

    public bool IsPicked
    {
        get => _isPicked;
        set => SetField(ref _isPicked, value);
    }
}

public sealed class MoltViewModel : ObservableObject, IDisposable
{
    private readonly IMeowsHost _host;
    private MoltSettings _settings;
    private IBackgroundTask? _scan;

    private string _status = "Nothing measured yet.";
    private string? _errorMessage;
    private bool _isScanning;
    private bool _isAsking;

    public MoltViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<MoltSettings>() ?? new MoltSettings();

        ScanCommand = new RelayCommand(StartScan, () => !IsScanning);
        CancelCommand = new RelayCommand(() => _scan?.Cancel(), () => IsScanning);
        ShedCommand = new RelayCommand(Ask, () => PickedCount > 0 && !IsScanning);
        ConfirmCommand = new RelayCommand(Shed, () => IsAsking);
        CancelShedCommand = new RelayCommand(() => IsAsking = false, () => IsAsking);
        PickAllCommand = new RelayCommand(() => SetAllPicked(true));
        PickNoneCommand = new RelayCommand(() => SetAllPicked(false));
        ExploreCommand = new RelayCommand(p => Open((p as SheddableViewModel)?.Where));

        StartScan();
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

    public string ModeText => Permanent
        ? "Deleted outright. The room comes back immediately and nothing can be undone."
        : "Sent to the Recycle Bin. Recoverable, but the room is not back until the bin is emptied.";

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
        get => _status;
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

    public long PickedSize => Items.Where(i => i.IsPicked).Sum(i => i.Item.Size);

    public string PickedText => PickedCount == 0
        ? "Nothing picked"
        : $"{PickedCount} picked, {FolderSize.Humanise(PickedSize)}";

    public string TotalText => Items.Count == 0
        ? ""
        : $"{FolderSize.Humanise(Items.Sum(i => i.Item.Size))} could be shed";

    private string Lots => PickedCount == 1 ? "1 lot" : $"{PickedCount} lots";

    public string ConfirmPrompt => Permanent
        ? $"Permanently delete {Lots}, {FolderSize.Humanise(PickedSize)}?"
        : $"Send {Lots} to the Recycle Bin, {FolderSize.Humanise(PickedSize)}?";

    public string ConfirmDetail => Permanent
        ? "This cannot be undone. Everything here is rebuilt by the tool that made it, so the cost is time rather than data."
        : "Recoverable from the Recycle Bin. Note that the room does not actually come back until the bin is emptied.";

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
        Status = "Measuring";

        var options = new MoltOptions
        {
            TempOlderThanDays = _settings.TempOlderThanDays,
            BuildRoot = _settings.BuildRoot,
        };

        _scan = _host.Background.Run("Measuring what can be shed", async context =>
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
        Status = Items.Count == 0 ? "Nothing to shed." : "";
        OnPropertyChanged(nameof(TotalText));
        Repick();
        _host.Log($"Molt found {Items.Count} thing(s) worth {FolderSize.Humanise(Items.Sum(i => i.Item.Size))}");
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SheddableViewModel.IsPicked))
            Repick();
    }

    private void Ask()
    {
        if (PickedCount == 0)
            return;

        // Always asks. There is no turning this one off, because permanent is on the table.
        IsAsking = true;
    }

    private void Shed()
    {
        IsAsking = false;

        var picked = Items.Where(i => i.IsPicked).ToList();
        if (picked.Count == 0)
            return;

        var paths = picked.SelectMany(i => i.Item.Paths).ToList();
        var expected = picked.Sum(i => i.Item.Size);
        var mode = Permanent ? ShedMode.Permanent : ShedMode.RecycleBin;

        var result = Shedder.Shed(paths, mode, expected);

        var where = Permanent ? "deleted" : "sent to the Recycle Bin";
        Status = result.Failed == 0
            ? $"{FolderSize.Humanise(result.Freed)} {where}"
            : $"{FolderSize.Humanise(result.Freed)} {where}, {result.Failed} left behind";

        if (result.FailureReason is not null)
            ErrorMessage = result.FailureReason;

        _host.Log($"Molt {where} {result.Removed} item(s), {FolderSize.Humanise(result.Freed)}, {result.Failed} failed");
        _host.Notifications.Post(NotificationSeverity.Info, "Molt finished",
            $"{FolderSize.Humanise(result.Freed)} {where}.");

        StartScan();
    }

    private void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open {path}: {ex.Message}";
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
            _host.Log($"Could not save Molt settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var item in Items)
            item.PropertyChanged -= OnItemChanged;

        _scan?.Cancel();
    }
}
