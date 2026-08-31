using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mews.Plugins.Abstractions;
using Mews.Plugins.Purrge.Services;

namespace Mews.Plugins.Purrge.ViewModels;

public sealed class PurrgeSettings
{
    public string? LastRoot { get; set; }

    public long MinimumBytes { get; set; } = 4096;

    public bool SkipSystemFolders { get; set; } = true;

    public AgeBasis AgeBasis { get; set; } = AgeBasis.Modified;
}

public sealed class PurrgeViewModel : ObservableObject, IDisposable
{
    private const int ThumbnailWidth = 96;
    private const int PreviewWidth = 720;

    private readonly IMewsHost _host;
    private readonly DuplicateScanner _scanner = new();
    private PurrgeSettings _settings;

    private IBackgroundTask? _scanTask;
    private CancellationTokenSource? _thumbnailCts;
    private FolderNodeViewModel? _selectedFolder;
    private DuplicateSetViewModel? _selectedSet;
    private DuplicateFileViewModel? _selectedFile;
    private Bitmap? _previewImage;
    private bool _isScanning;
    private string _statusMessage = "Pick a folder on the left, then scan.";
    private string? _errorMessage;
    private string _scanRoot = "";

    public PurrgeViewModel(IMewsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<PurrgeSettings>() ?? new PurrgeSettings();
        _scanRoot = _settings.LastRoot ?? "";

        Roots = FolderNodeViewModel.CreateRoots();

        ScanCommand = new RelayCommand(StartScan, () => !IsScanning && ScanRoot.Length > 0);
        CancelScanCommand = new RelayCommand(() => _scanTask?.Cancel(), () => IsScanning);
        KeepOldestCommand = new RelayCommand(() => _ = KeepAsync(keepOldest: true), CanAct);
        KeepNewestCommand = new RelayCommand(() => _ = KeepAsync(keepOldest: false), CanAct);
        DeleteSelectedCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), CanDeleteSelected);
        RevealCommand = new RelayCommand(RevealSelected, () => SelectedFile is not null);
        SelectFileCommand = new RelayCommand(SelectFile);
    }

    public ObservableCollection<FolderNodeViewModel> Roots { get; }

    public ObservableCollection<DuplicateSetViewModel> Sets { get; } = new();

    public RelayCommand ScanCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public RelayCommand KeepOldestCommand { get; }

    public RelayCommand KeepNewestCommand { get; }

    public RelayCommand DeleteSelectedCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand SelectFileCommand { get; }

    public IReadOnlyList<AgeBasis> AgeBasisOptions { get; } = [AgeBasis.Modified, AgeBasis.Created];

    public AgeBasis AgeBasis
    {
        get => _settings.AgeBasis;
        set
        {
            if (_settings.AgeBasis == value)
                return;
            _settings.AgeBasis = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeepOldestLabel));
            OnPropertyChanged(nameof(KeepNewestLabel));
            OnPropertyChanged(nameof(AgeBasisHint));
        }
    }

    /// <summary>On the button itself, since the two timestamps disagree after a copy.</summary>
    public string KeepOldestLabel => $"Keep oldest ({BasisWord})";

    public string KeepNewestLabel => $"Keep newest ({BasisWord})";

    public string AgeBasisHint => AgeBasis == AgeBasis.Modified
        ? "Copying usually preserves the modified time, so the original and its copies often match here."
        : "Copying sets a new created time, so the copy looks newer than the original.";

    private string BasisWord => AgeBasis == AgeBasis.Created ? "created" : "modified";

    public string ScanRoot
    {
        get => _scanRoot;
        private set
        {
            if (SetField(ref _scanRoot, value))
                ScanCommand.RaiseCanExecuteChanged();
        }
    }

    public FolderNodeViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetField(ref _selectedFolder, value) || value is null || value.Path.Length == 0)
                return;
            ScanRoot = value.Path;
        }
    }

    public DuplicateSetViewModel? SelectedSet
    {
        get => _selectedSet;
        set
        {
            if (!SetField(ref _selectedSet, value))
                return;
            SelectedFile = value?.Files.FirstOrDefault();
            RaiseActionState();
        }
    }

    public DuplicateFileViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            var previous = _selectedFile;
            if (!SetField(ref _selectedFile, value))
                return;
            if (previous is not null)
                previous.IsSelected = false;
            if (value is not null)
                value.IsSelected = true;
            OnPropertyChanged(nameof(HasSelectedFile));
            RaiseActionState();
            _ = LoadPreviewAsync(value);
        }
    }

    public bool HasSelectedFile => _selectedFile is not null;

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            var old = _previewImage;
            if (!SetField(ref _previewImage, value))
                return;
            OnPropertyChanged(nameof(HasPreviewImage));
            old?.Dispose();
        }
    }

    public bool HasPreviewImage => _previewImage is not null;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetField(ref _isScanning, value))
                return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelScanCommand.RaiseCanExecuteChanged();
            RaiseActionState();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
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

    public bool HasResults => Sets.Count > 0;

    public string ResultSummary
    {
        get
        {
            if (Sets.Count == 0)
                return "";
            var recoverable = Sets.Sum(s => s.RedundantBytes);
            var copies = Sets.Sum(s => s.Count - 1);
            return $"{Sets.Count} set(s) · {copies} redundant copies · {DuplicateSetViewModel.Format(recoverable)} recoverable";
        }
    }

    /// <summary>How many files the button is about to take. No surprises.</summary>
    public string PendingActionText
    {
        get
        {
            if (SelectedSet is null || SelectedSet.Count < 2)
                return "";
            return $"Either keep button removes {SelectedSet.Count - 1} file(s) to the Recycle Bin.";
        }
    }

    private void SelectFile(object? parameter)
    {
        if (parameter is not DuplicateFileViewModel file)
            return;

        var owner = Sets.FirstOrDefault(s => s.Files.Contains(file));
        if (owner is not null && !ReferenceEquals(owner, _selectedSet))
        {
            _selectedSet = owner;
            OnPropertyChanged(nameof(SelectedSet));
        }

        SelectedFile = file;
        RaiseActionState();
    }

    private bool CanAct() => !IsScanning && SelectedSet is { Count: > 1 };

    /// <summary>Never let the last copy go. A set always keeps one.</summary>
    private bool CanDeleteSelected() => !IsScanning && SelectedFile is not null && SelectedSet is { Count: > 1 };

    /// <summary>
    /// Runs through the shell, so it survives a tab switch, shows up in the task panel, and
    /// gets cancelled for us if the plugin is switched off halfway through.
    /// </summary>
    private void StartScan()
    {
        ErrorMessage = null;
        CancelThumbnails();
        ClearSets();

        var root = ScanRoot;
        _settings.LastRoot = root;
        SaveSettings();
        IsScanning = true;

        var options = new ScanOptions(_settings.MinimumBytes, _settings.SkipSystemFolders);

        _scanTask = _host.Background.Run($"Scanning {Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))}",
            async context =>
            {
                var total = 0;
                var progress = new Progress<ScanProgress>(p =>
                {
                    switch (p.Phase)
                    {
                        case ScanPhase.Enumerating:
                            context.Report($"Listing files… {p.FilesSeen:N0} seen");
                            context.ReportProgress(null);
                            break;
                        case ScanPhase.Hashing:
                            if (total == 0)
                                total = p.Candidates;
                            context.Report($"Comparing content… {p.Detail}");
                            context.ReportProgress(total == 0 ? null : (double)(total - p.Candidates) / total);
                            break;
                        default:
                            context.Report(p.Detail);
                            break;
                    }
                });

                try
                {
                    var found = await _scanner.ScanAsync(root, options, progress, context.Token);
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyResults(found));
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "Scan cancelled.");
                    throw;
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() => IsScanning = false);
                }
            });

        _host.Log($"Scanning {root} for duplicates");
    }

    private void ApplyResults(IReadOnlyList<DuplicateSet> found)
    {
        foreach (var set in found)
            Sets.Add(new DuplicateSetViewModel(set));

        StatusMessage = Sets.Count == 0 ? "No duplicates found." : $"Done. {ResultSummary}";
        _host.Log($"Scan finished: {(Sets.Count == 0 ? "no duplicates" : ResultSummary)}");

        if (Sets.Count > 0)
            _host.Notifications.Post(NotificationSeverity.Info, "Duplicate scan finished", ResultSummary);

        RaiseResultState();
        _ = LoadThumbnailsAsync();
    }

    private async Task KeepAsync(bool keepOldest)
    {
        if (SelectedSet is not { Count: > 1 } set)
            return;

        var survivor = keepOldest ? set.Oldest(AgeBasis) : set.Newest(AgeBasis);
        if (survivor is null)
            return;

        var doomed = set.Files.Where(f => !ReferenceEquals(f, survivor)).ToList();
        await RemoveAsync(set, doomed, $"kept {(keepOldest ? "oldest" : "newest")} of {set.Count}");
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedSet is not { Count: > 1 } set || SelectedFile is null)
            return;

        await RemoveAsync(set, [SelectedFile], "deleted one file");
    }

    private async Task RemoveAsync(DuplicateSetViewModel set, IReadOnlyList<DuplicateFileViewModel> doomed, string what)
    {
        ErrorMessage = null;
        var paths = doomed.Select(d => d.FullPath).ToList();

        var outcome = await Task.Run(() => RecycleBin.Send(paths));

        // Only remove rows for files that actually went. A partial failure should show.
        var gone = doomed.Where(d => !File.Exists(d.FullPath)).ToList();
        if (ReferenceEquals(SelectedFile, null) || gone.Contains(SelectedFile))
            SelectedFile = null;

        set.Remove(gone);
        if (set.Count < 2)
        {
            Sets.Remove(set);
            set.Dispose();
            SelectedSet = Sets.FirstOrDefault();
        }
        else
        {
            SelectedFile = set.Files.FirstOrDefault();
        }

        RaiseResultState();

        if (outcome.FailureReason is not null)
            ErrorMessage = outcome.FailureReason;

        StatusMessage = $"{what}: {outcome.Deleted} file(s) sent to the Recycle Bin.";
        _host.Log($"Purrge {what}: {outcome.Deleted} deleted, {outcome.Failed} failed");
    }

    private async Task LoadThumbnailsAsync()
    {
        CancelThumbnails();
        _thumbnailCts = new CancellationTokenSource();
        var token = _thumbnailCts.Token;

        try
        {
            foreach (var file in Sets.SelectMany(s => s.Files).Take(600))
            {
                if (token.IsCancellationRequested)
                    return;
                await file.LoadThumbnailAsync(ThumbnailWidth, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Another scan started. Expected.
        }
    }

    private async Task LoadPreviewAsync(DuplicateFileViewModel? file)
    {
        if (file is null)
        {
            PreviewImage = null;
            return;
        }

        var path = file.FullPath;
        var bitmap = await Task.Run(() => PreviewSupport.Decode(path, PreviewWidth));

        if (SelectedFile?.FullPath != path)
        {
            bitmap?.Dispose();
            return;
        }

        PreviewImage = bitmap;
    }

    private void RevealSelected()
    {
        if (SelectedFile is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { "/select,", SelectedFile.FullPath },
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open Explorer: {ex.Message}";
        }
    }

    private void ClearSets()
    {
        foreach (var set in Sets)
            set.Dispose();
        Sets.Clear();
        SelectedSet = null;
        SelectedFile = null;
        PreviewImage = null;
        RaiseResultState();
    }

    private void CancelThumbnails()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
    }

    private void RaiseResultState()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ResultSummary));
        RaiseActionState();
    }

    private void RaiseActionState()
    {
        OnPropertyChanged(nameof(PendingActionText));
        KeepOldestCommand.RaiseCanExecuteChanged();
        KeepNewestCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        RevealCommand.RaiseCanExecuteChanged();
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Purrge settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _scanTask?.Dispose();
        CancelThumbnails();
        PreviewImage = null;
        foreach (var set in Sets)
            set.Dispose();
    }
}
