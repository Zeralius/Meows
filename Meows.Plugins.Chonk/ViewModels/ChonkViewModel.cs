using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Chonk.Services;

namespace Meows.Plugins.Chonk.ViewModels;

public sealed class ChonkSettings
{
    public string? LastRoot { get; set; }

    public bool SkipSystemFolders { get; set; } = true;

    /// <summary>On by default. Removing a folder here takes everything inside it with it.</summary>
    public bool ConfirmDeletes { get; set; } = true;
}

/// <summary>One row in the ranked list.</summary>
public sealed class EntryViewModel(DiskEntry entry, long parentSize) : ObservableObject
{
    public DiskEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string Path => Entry.Path;

    public string SizeText => DiskScan.Humanise(Entry.Size);

    public bool CanDrillInto => Entry.CanDrillInto;

    public bool CanDelete => Entry.CanDelete;

    /// <summary>Share of the folder it sits in, as a bar the eye can compare down a column.</summary>
    public double Fraction => parentSize <= 0 ? 0 : (double)Entry.Size / parentSize;

    public string PercentText => parentSize <= 0 ? "" : $"{Fraction * 100:0.#}%";

    public string Detail => Entry.Kind switch
    {
        DiskEntryKind.Folder => Entry.FileCount == 1
            ? MeowsText.Current["chonk.files.one"]
            : MeowsText.Current.Format("chonk.files.many", Entry.FileCount),
        DiskEntryKind.SmallFiles => MeowsText.Current["chonk.notlisted"],
        _ => "",
    };

    public string Glyph => Entry.Kind switch
    {
        DiskEntryKind.Folder => "📁",
        DiskEntryKind.SmallFiles => "···",
        _ => "📄",
    };
}

public sealed class CrumbViewModel(DiskEntry entry, bool isLast) : ObservableObject
{
    public DiskEntry Entry { get; } = entry;

    public string Name { get; } = entry.Name;

    public bool IsLast { get; } = isLast;
}

public sealed class ChonkViewModel : ObservableObject, IDisposable
{
    private readonly IMeowsHost _host;
    private ChonkSettings _settings;
    private IBackgroundTask? _scan;

    private DiskEntry? _current;
    private EntryViewModel? _selected;
    private EntryViewModel? _pendingDelete;
    private string _status = MeowsText.Current["chonk.status.start"];
    private string? _errorMessage;
    private bool _isScanning;

    private FolderIdentity? _identity;
    private bool _isIdentifying;
    private CancellationTokenSource? _identifying;

    public ChonkViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<ChonkSettings>() ?? new ChonkSettings();

        ScanCommand = new RelayCommand(p => StartScan(p as string ?? SelectedRoot), _ => !IsScanning);
        CancelCommand = new RelayCommand(() => _scan?.Cancel(), () => IsScanning);
        OpenCommand = new RelayCommand(p => Drill((p as EntryViewModel)?.Entry));
        UpCommand = new RelayCommand(GoUp, () => Current?.Parent is not null);
        GoToCommand = new RelayCommand(p => Show((p as CrumbViewModel)?.Entry));
        DeleteCommand = new RelayCommand(DeleteSelected, () => Selected is { CanDelete: true } && !IsScanning);
        ExploreCommand = new RelayCommand(() => OpenInExplorer(Selected?.Path), () => Selected is not null);
        ConfirmDeleteCommand = new RelayCommand(() => Remove(PendingDelete), () => PendingDelete is not null);
        CancelDeleteCommand = new RelayCommand(() => PendingDelete = null, () => PendingDelete is not null);

        LoadDrives();
        SelectedRoot = _settings.LastRoot ?? Drives.FirstOrDefault()?.Path;
    }

    public ObservableCollection<DriveViewModel> Drives { get; } = new();

    public ObservableCollection<EntryViewModel> Entries { get; } = new();

    public ObservableCollection<CrumbViewModel> Crumbs { get; } = new();

    public RelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand UpCommand { get; }

    public RelayCommand GoToCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand ExploreCommand { get; }

    public RelayCommand ConfirmDeleteCommand { get; }

    public RelayCommand CancelDeleteCommand { get; }

    public string? SelectedRoot { get; set; }

    public bool SkipSystemFolders
    {
        get => _settings.SkipSystemFolders;
        set
        {
            if (_settings.SkipSystemFolders == value)
                return;
            _settings.SkipSystemFolders = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether to ask first. On by default: folders go whole here, so a mis-click costs
    /// everything inside rather than the one file you were looking at.
    /// </summary>
    public bool ConfirmDeletes
    {
        get => _settings.ConfirmDeletes;
        set
        {
            if (_settings.ConfirmDeletes == value)
                return;
            _settings.ConfirmDeletes = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DoNotAskAgain));
        }
    }

    /// <summary>The same setting worded the way a confirmation box has to word it.</summary>
    public bool DoNotAskAgain
    {
        get => !ConfirmDeletes;
        set => ConfirmDeletes = !value;
    }

    /// <summary>What we are waiting to be told to remove, or null when we are not asking.</summary>
    public EntryViewModel? PendingDelete
    {
        get => _pendingDelete;
        private set
        {
            if (!SetField(ref _pendingDelete, value))
                return;
            OnPropertyChanged(nameof(IsAsking));
            OnPropertyChanged(nameof(ConfirmPrompt));
            OnPropertyChanged(nameof(ConfirmDetail));
            ConfirmDeleteCommand.RaiseCanExecuteChanged();
            CancelDeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAsking => PendingDelete is not null;

    public string ConfirmPrompt => PendingDelete is null
        ? ""
        : _host.Text.Format("chonk.confirm.prompt", PendingDelete.Name);

    /// <summary>
    /// The size, plus the file count for a folder. The count is the useful part: the name
    /// alone does not tell you it holds four thousand files.
    /// </summary>
    public string ConfirmDetail
    {
        get
        {
            if (PendingDelete is not { } pending)
                return "";

            // One whole sentence per case rather than fragments glued together, because the
            // count sits in the middle of it and no two languages put it in the same place.
            var line = pending.Entry.IsFolder
                ? pending.Entry.FileCount switch
                {
                    1 => _host.Text.Format("chonk.confirm.folder.one", pending.SizeText),
                    var n => _host.Text.Format("chonk.confirm.folder.many", pending.SizeText, n),
                }
                : _host.Text.Format("chonk.confirm.file", pending.SizeText);

            // What it is belongs here more than anywhere else on the tab. This is the last moment
            // anyone gets to notice that the folder is a Steam game or that a program has it open.
            var warning = Identity switch
            {
                { Verdict: FolderVerdict.Game } => _host.Text["chonk.warn.game"],
                { InUse: true } => _host.Text["chonk.warn.inuse"],
                _ => "",
            };

            return warning.Length == 0 ? line : $"{line} {warning}";
        }
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

    public DiskEntry? Current
    {
        get => _current;
        private set
        {
            if (!SetField(ref _current, value))
                return;
            OnPropertyChanged(nameof(CurrentPath));
            OnPropertyChanged(nameof(CurrentSizeText));
            OnPropertyChanged(nameof(HasResults));
            UpCommand.RaiseCanExecuteChanged();
        }
    }

    public string CurrentPath => Current?.Path ?? "";

    public string CurrentSizeText => Current is null ? "" : $"{DiskScan.Humanise(Current.Size)} in here";

    public bool HasResults => Current is not null;

    public EntryViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            DeleteCommand.RaiseCanExecuteChanged();
            ExploreCommand.RaiseCanExecuteChanged();
            Identify(value);
        }
    }

    /// <summary>
    /// What the selected folder is. Null until it has been worked out, and for anything that
    /// is not a folder.
    /// </summary>
    public FolderIdentity? Identity
    {
        get => _identity;
        private set
        {
            if (!SetField(ref _identity, value))
                return;

            OnPropertyChanged(nameof(HasIdentity));
            OnPropertyChanged(nameof(IdentityHeadline));
            OnPropertyChanged(nameof(IdentityAdvice));
            OnPropertyChanged(nameof(IdentityInUse));
            OnPropertyChanged(nameof(IdentityIsWarning));
            OnPropertyChanged(nameof(ConfirmDetail));

            Evidence.Clear();
            foreach (var line in value?.Evidence ?? [])
                Evidence.Add(line);
        }
    }

    /// <summary>Why it thinks so, which is the part that makes the answer worth anything.</summary>
    public ObservableCollection<string> Evidence { get; } = new();

    public bool HasIdentity => Identity is not null;

    public string IdentityHeadline => Identity?.Headline ?? "";

    public string IdentityAdvice => Identity?.Advice ?? "";

    public bool IdentityInUse => Identity?.InUse ?? false;

    /// <summary>
    /// Whether to highlight this rather than show it as another grey line. Games have to be
    /// uninstalled through their launcher, and anything a running program has open should not be
    /// deleted underneath it.
    /// </summary>
    public bool IdentityIsWarning =>
        Identity is { Verdict: FolderVerdict.Game } || IdentityInUse;

    public bool IsIdentifying
    {
        get => _isIdentifying;
        private set => SetField(ref _isIdentifying, value);
    }

    /// <summary>
    /// Works out what the selection is, off the UI thread. Selection changes as fast as the
    /// arrow key repeats, so each one cancels the last rather than queueing a walk per keypress.
    /// </summary>
    private async void Identify(EntryViewModel? entry)
    {
        _identifying?.Cancel();
        _identifying = null;
        Identity = null;

        if (entry is not { Entry.IsFolder: true })
        {
            IsIdentifying = false;
            return;
        }

        using var source = new CancellationTokenSource();
        _identifying = source;
        IsIdentifying = true;

        try
        {
            var path = entry.Path;
            var found = await Task.Run(() => FolderInspector.Of(path, source.Token), source.Token);

            // A newer selection may have started while this ran, and its answer wins.
            if (ReferenceEquals(_identifying, source))
                Identity = found;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host.Log($"Could not work out what {entry.Path} is: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_identifying, source))
            {
                _identifying = null;
                IsIdentifying = false;
            }
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

    private void LoadDrives()
    {
        Drives.Clear();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType != DriveType.CDRom))
                Drives.Add(new DriveViewModel(drive));
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("chonk.error.drives", ex.Message);
        }
    }

    public void StartScan(string? root)
    {
        if (IsScanning || string.IsNullOrWhiteSpace(root))
            return;

        if (!Directory.Exists(root))
        {
            ErrorMessage = _host.Text.Format("chonk.error.missing", root);
            return;
        }

        SelectedRoot = root;
        _settings.LastRoot = root;
        SaveSettings();

        ErrorMessage = null;
        IsScanning = true;
        Status = _host.Text.Format("chonk.status.measuring", root);

        var options = new ScanOptions { SkipSystemFolders = SkipSystemFolders };

        // Background work, so switching tabs does not abandon a drive scan half way through
        // and the Tasks panel can say what it is doing.
        _scan = _host.Background.Run(_host.Text.Format("chonk.status.measuring", root), async context =>
        {
            var progress = new Progress<ScanProgress>(p =>
                Status = _host.Text.Format("chonk.status.progress", p.FoldersSeen, DiskScan.Humanise(p.BytesSeen)));

            var tree = await Task.Run(
                () => DiskScan.Run(root, options, progress, context.Token), context.Token);

            await Dispatcher.UIThread.InvokeAsync(() => ShowScanned(tree));
        });
    }

    /// <summary>
    /// Puts a finished measurement on screen. Separate from the scan so this can be tested
    /// without a background task and a dispatcher.
    /// </summary>
    public void ShowScanned(DiskEntry tree)
    {
        Show(tree);
        Status = $"{DiskScan.Humanise(tree.Size)} across {tree.FileCount} files";
        IsScanning = false;
    }

    /// <summary>Shows one folder's contents, biggest first.</summary>
    private void Show(DiskEntry? folder)
    {
        if (folder is null)
            return;

        Current = folder;
        Selected = null;

        Entries.Clear();
        foreach (var child in folder.Children.OrderByDescending(c => c.Size))
            Entries.Add(new EntryViewModel(child, folder.Size));

        Crumbs.Clear();
        var chain = new List<DiskEntry>();
        for (var node = folder; node is not null; node = node.Parent)
            chain.Insert(0, node);
        for (var i = 0; i < chain.Count; i++)
            Crumbs.Add(new CrumbViewModel(chain[i], i == chain.Count - 1));

        // Raised unconditionally. Re-showing the folder you are already in is exactly what
        // happens after a delete, and the size setter would otherwise short circuit on the
        // reference being unchanged and leave a stale total in the header.
        OnPropertyChanged(nameof(CurrentSizeText));
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(HasResults));
    }

    private void Drill(DiskEntry? entry)
    {
        if (entry is { CanDrillInto: true })
            Show(entry);
    }

    private void GoUp() => Show(Current?.Parent);

    /// <summary>
    /// Asks first, unless that has been turned off. Nothing is touched here; <see cref="Remove"/>
    /// is the only thing that deletes.
    /// </summary>
    private void DeleteSelected()
    {
        if (Selected is not { } row || !row.CanDelete)
            return;

        if (ConfirmDeletes)
        {
            PendingDelete = row;
            return;
        }

        Remove(row);
    }

    private void Remove(EntryViewModel? row)
    {
        PendingDelete = null;

        if (row is null || !row.CanDelete)
            return;

        var entry = row.Entry;
        var what = entry.IsFolder ? "folder" : "file";
        var outcome = RecycleBin.Send([entry.Path]);

        if (!outcome.Succeeded)
        {
            ErrorMessage = outcome.FailureReason ?? _host.Text[$"chonk.error.{what}"];
            _host.Log($"Chonk could not remove {entry.Path}: {ErrorMessage}");
            return;
        }

        var freed = entry.Size;
        DiskScan.Forget(entry);

        // Rebuild this level rather than the whole tree: the numbers above have already been
        // adjusted, so a rescan would only tell us what we know.
        Show(Current);

        Status = _host.Text.Format("chonk.status.removed", entry.Name, DiskScan.Humanise(freed));
        _host.Log($"Chonk sent {what} {entry.Path} to the Recycle Bin, {DiskScan.Humanise(freed)} freed");
        _host.Notifications.Post(NotificationSeverity.Info, _host.Text["chonk.notify.recycled"],
            _host.Text.Format("chonk.notify.detail", entry.Name, DiskScan.Humanise(freed)));
    }

    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("chonk.error.open", path, ex.Message);
        }
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Chonk settings: {ex.Message}");
        }
    }

    public void Dispose() => _scan?.Cancel();
}

public sealed class DriveViewModel(DriveInfo drive) : ObservableObject
{
    public string Path { get; } = drive.RootDirectory.FullName;

    public string Name { get; } = string.IsNullOrWhiteSpace(drive.VolumeLabel)
        ? drive.Name
        : $"{drive.Name} {drive.VolumeLabel}";

    public string UsageText { get; } = MeowsText.Current.Format("chonk.drive.usage",
        DiskScan.Humanise(drive.TotalSize - drive.TotalFreeSpace),
        DiskScan.Humanise(drive.TotalSize));

    public double Fraction { get; } =
        drive.TotalSize <= 0 ? 0 : (double)(drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize;
}
