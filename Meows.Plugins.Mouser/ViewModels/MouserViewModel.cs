using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Mouser.Services;

namespace Meows.Plugins.Mouser.ViewModels;

public sealed class MouserSettings
{
    public string? Root { get; set; }

    public bool SkipSystemFolders { get; set; } = true;

    public bool ConfirmDeletes { get; set; } = true;
}

public sealed class FindingViewModel(Finding finding) : ObservableObject
{
    public Finding Finding { get; } = finding;

    public string Name => Finding.Name;

    public string Path => Finding.Path;

    public string Detail => Finding.DetailValues.Length == 0
        ? MeowsText.Current[Finding.Detail]
        : MeowsText.Current.Format(Finding.Detail, Finding.DetailValues);

    public string Folder => System.IO.Path.GetDirectoryName(Finding.Path) ?? "";

    public string Glyph => Finding.Kind switch
    {
        DeadKind.EmptyFolder => "📁",
        DeadKind.EmptyFile => "📄",
        DeadKind.BrokenShortcut => "🔗",
        _ => "🧹",
    };
}

public sealed class KindViewModel(DeadKind kind, int count) : ObservableObject
{
    private bool _isOn;

    public DeadKind Kind { get; } = kind;

    public string Name => MeowsText.Current[MouserScan.Describe(Kind)];

    public string Detail => count == 1
        ? MeowsText.Current["mouser.things.one"]
        : MeowsText.Current.Format("mouser.things.many", count);

    public bool IsOn
    {
        get => _isOn;
        set => SetField(ref _isOn, value);
    }
}

public sealed class MouserViewModel : ObservableObject, IDisposable
{
    private readonly IMeowsHost _host;
    private MouserSettings _settings;
    private IBackgroundTask? _scan;
    private IReadOnlyList<Finding> _all = [];
    private DeadKind? _filter;
    private List<FindingViewModel> _pending = [];

    private string _status = MeowsText.Current["mouser.status.start"];
    private string? _errorMessage;
    private bool _isScanning;
    private int _pendingCount;
    private bool _wasStopped;
    private int _foldersSeen;

    public MouserViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<MouserSettings>() ?? new MouserSettings();

        ScanCommand = new RelayCommand(StartScan, () => !IsScanning && HasRoot);
        CancelCommand = new RelayCommand(() => _scan?.Cancel(), () => IsScanning);
        FilterCommand = new RelayCommand(p => ApplyFilter((p as KindViewModel)?.Kind));
        ClearFilterCommand = new RelayCommand(() => ApplyFilter(null));
        DeleteCommand = new RelayCommand(Ask, () => Selected.Count > 0 && !IsScanning);
        ConfirmDeleteCommand = new RelayCommand(() => Remove(_pending), () => IsAsking);
        CancelDeleteCommand = new RelayCommand(() => PendingCount = 0, () => IsAsking);
        ExploreCommand = new RelayCommand(() => Open(SelectedOne?.Folder), () => SelectedOne is not null);

        if (HasRoot)
            StartScan();
    }

    public ObservableCollection<FindingViewModel> Findings { get; } = new();

    public ObservableCollection<KindViewModel> Kinds { get; } = new();

    public List<FindingViewModel> Selected { get; } = [];

    public RelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand FilterCommand { get; }

    public RelayCommand ClearFilterCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand ConfirmDeleteCommand { get; }

    public RelayCommand CancelDeleteCommand { get; }

    public RelayCommand ExploreCommand { get; }

    public string Root => _settings.Root ?? "";

    public bool HasRoot => !string.IsNullOrWhiteSpace(Root) && Directory.Exists(Root);

    public bool SkipSystemFolders
    {
        get => _settings.SkipSystemFolders;
        set
        {
            if (_settings.SkipSystemFolders == value)
                return;
            _settings.SkipSystemFolders = value;
            Save();
            OnPropertyChanged();
        }
    }

    public bool ConfirmDeletes
    {
        get => _settings.ConfirmDeletes;
        set
        {
            if (_settings.ConfirmDeletes == value)
                return;
            _settings.ConfirmDeletes = value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DoNotAskAgain));
        }
    }

    public bool DoNotAskAgain
    {
        get => !ConfirmDeletes;
        set => ConfirmDeletes = !value;
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetField(ref _isScanning, value))
                return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
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

    public bool IsEmpty => Findings.Count == 0;

    public string Summary => _all.Count switch
    {
        0 => "",
        1 => _host.Text["mouser.summary.one"],
        var n => _host.Text.Format("mouser.summary.many", n),
    };

    public bool WasStopped => _wasStopped;

    /// <summary>
    /// Shown on the tab, because a partial list looks exactly like a complete one. Empty
    /// folders suffer most from stopping early: a folder is not empty until everything below it
    /// has been read, so the ones near where the sweep stopped are held back rather than
    /// guessed at.
    /// </summary>
    public string StoppedNote => _host.Text.Format("mouser.stopped.note", _foldersSeen);

    public FindingViewModel? SelectedOne => Selected.Count == 1 ? Selected[0] : null;

    public string SelectionText => Selected.Count switch
    {
        0 => "",
        1 => Selected[0].Name,
        var n => _host.Text.Format("mouser.selected", n),
    };

    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (!SetField(ref _pendingCount, value))
                return;
            OnPropertyChanged(nameof(IsAsking));
            OnPropertyChanged(nameof(ConfirmPrompt));
            OnPropertyChanged(nameof(ConfirmDetail));
            ConfirmDeleteCommand.RaiseCanExecuteChanged();
            CancelDeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAsking => PendingCount > 0;

    public string ConfirmPrompt => PendingCount == 1
        ? _host.Text.Format("mouser.confirm.one", _pending.FirstOrDefault()?.Name ?? "")
        : _host.Text.Format("mouser.confirm.many", PendingCount);

    public string ConfirmDetail
    {
        get
        {
            if (!IsAsking)
                return "";

            var folders = _pending.Count(p => p.Finding.Kind == DeadKind.EmptyFolder);

            return folders > 0
                ? _host.Text.Format("mouser.confirm.detail.folders", folders)
                : _host.Text["mouser.confirm.detail"];
        }
    }

    public void SetSelection(IEnumerable<FindingViewModel> items)
    {
        Selected.Clear();
        Selected.AddRange(items);
        OnPropertyChanged(nameof(SelectedOne));
        OnPropertyChanged(nameof(SelectionText));
        DeleteCommand.RaiseCanExecuteChanged();
        ExploreCommand.RaiseCanExecuteChanged();
    }

    public void SetRoot(string folder)
    {
        _settings.Root = folder;
        Save();
        OnPropertyChanged(nameof(Root));
        OnPropertyChanged(nameof(HasRoot));
        ScanCommand.RaiseCanExecuteChanged();
        StartScan();
    }

    private void StartScan()
    {
        if (IsScanning || !HasRoot)
            return;

        ErrorMessage = null;
        IsScanning = true;
        Status = _host.Text["mouser.status.looking"];

        var root = Root;
        var options = new MouserOptions { SkipSystemFolders = SkipSystemFolders };

        _scan = _host.Background.Run(_host.Text.Format("mouser.task.look", root), async context =>
        {
            var progress = new Progress<MouserProgress>(p =>
                Status = $"{p.FoldersSeen} folders, {p.Found} found");

            // No cancellation token on the Task.Run, deliberately. Stopping is an ordinary way for
            // a sweep to end here, and the scan hands back what it got to rather than throwing it
            // away, so letting the task fault would discard the very thing Stop is meant to keep.
            var result = await Task.Run(() => MouserScan.Run(root, options, progress, context.Token));

            await Dispatcher.UIThread.InvokeAsync(() => Show(result));
        });
    }

    /// <summary>Puts a finished sweep on screen. Separate so it can be exercised on its own.</summary>
    public void Show(ScanResult result)
    {
        _all = result.Findings;
        _wasStopped = result.WasStopped;
        _foldersSeen = result.FoldersSeen;
        IsScanning = false;

        Kinds.Clear();
        foreach (var kind in Enum.GetValues<DeadKind>())
        {
            var count = _all.Count(f => f.Kind == kind);
            if (count > 0)
                Kinds.Add(new KindViewModel(kind, count));
        }

        Rebuild();

        Status = _wasStopped
            ? _host.Text.Format("mouser.status.stopped", _foldersSeen)
            : _all.Count == 0 ? _host.Text["mouser.status.empty"] : "";

        _host.Log(_wasStopped
            ? $"Mouser stopped after {_foldersSeen} folder(s) in {Root}, keeping {_all.Count} finding(s)"
            : $"Mouser found {_all.Count} thing(s) in {Root}");
    }

    private void ApplyFilter(DeadKind? kind)
    {
        _filter = _filter == kind ? null : kind;
        Rebuild();
    }

    private void Rebuild()
    {
        Findings.Clear();

        var shown = _filter is { } kind ? _all.Where(f => f.Kind == kind) : _all;
        foreach (var finding in shown.OrderBy(f => f.Kind).ThenBy(f => f.Path))
            Findings.Add(new FindingViewModel(finding));

        foreach (var bucket in Kinds)
            bucket.IsOn = bucket.Kind == _filter;

        SetSelection([]);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(WasStopped));
        OnPropertyChanged(nameof(StoppedNote));
    }

    private void Ask()
    {
        if (Selected.Count == 0)
            return;

        _pending = [.. Selected];

        if (!ConfirmDeletes)
        {
            Remove(_pending);
            return;
        }

        PendingCount = _pending.Count;
    }

    private void Remove(List<FindingViewModel> items)
    {
        PendingCount = 0;
        if (items.Count == 0)
            return;

        var outcome = RecycleBin.Send(items.Select(i => i.Path).ToList());

        if (!outcome.Succeeded)
        {
            ErrorMessage = outcome.FailureReason ?? _host.Text["mouser.error.nothing"];
            _host.Log($"Mouser could not remove {items.Count} thing(s): {ErrorMessage}");
        }
        else
        {
            Status = _host.Text.Format("mouser.status.removed", outcome.Deleted);
            _host.Log($"Mouser sent {outcome.Deleted} thing(s) to the Recycle Bin");
        }

        // Removing an empty folder can leave its parent empty, so the answer has genuinely
        // changed and the only honest thing to do is look again.
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
            ErrorMessage = _host.Text.Format("mouser.error.open", path, ex.Message);
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
            _host.Log($"Could not save Mouser settings: {ex.Message}");
        }
    }

    public void Dispose() => _scan?.Cancel();
}
