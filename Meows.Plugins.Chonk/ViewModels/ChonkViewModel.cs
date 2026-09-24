using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;

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

    public bool IsArchive { get; } = entry.Kind == DiskEntryKind.File && Archives.IsArchive(entry.Name);

    /// <summary>
    /// Whether the other half of an archive and folder pair is in the same listing. Read off the
    /// tree the scan already built rather than the disk, so it costs nothing per row; whether the
    /// two really hold the same thing is worked out when the row is selected.
    /// </summary>
    public bool HasSibling { get; } = SiblingIn(entry);

    private static bool SiblingIn(DiskEntry entry)
    {
        if (entry.Parent is not { } parent)
            return false;

        return entry.Kind switch
        {
            DiskEntryKind.Folder => parent.Children.Any(c =>
                c.Kind == DiskEntryKind.File && Archives.IsArchive(c.Name) &&
                string.Equals(Archives.Stem(c.Name), entry.Name, StringComparison.OrdinalIgnoreCase)),
            DiskEntryKind.File when Archives.IsArchive(entry.Name) => parent.Children.Any(c =>
                c.Kind == DiskEntryKind.Folder &&
                string.Equals(c.Name, Archives.Stem(entry.Name), StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    public string Detail
    {
        get
        {
            var text = MeowsText.Current;
            var line = Entry.Kind switch
            {
                DiskEntryKind.Folder => Entry.FileCount == 1
                    ? text["chonk.files.one"]
                    : text.Format("chonk.files.many", Entry.FileCount),
                DiskEntryKind.SmallFiles => text["chonk.notlisted"],
                _ when IsArchive => text["chonk.archive"],
                _ => "",
            };

            if (!HasSibling)
                return line;

            var beside = text[IsArchive ? "chonk.beside.folder" : "chonk.beside.archive"];
            return line.Length == 0 ? beside : $"{line} · {beside}";
        }
    }

    public string Glyph => Entry.Kind switch
    {
        DiskEntryKind.Folder => "📁",
        DiskEntryKind.SmallFiles => "···",
        _ when IsArchive => "📦",
        _ => "📄",
    };
}

public sealed class CrumbViewModel(DiskEntry entry, bool isLast) : ObservableObject
{
    public DiskEntry Entry { get; } = entry;

    public string Name { get; } = entry.Name;

    public bool IsLast { get; } = isLast;
}

public sealed class ChonkViewModel : ObservableObject, IDisposable, ISearchable
{
    private readonly IMeowsHost _host;
    private ChonkSettings _settings;
    private IBackgroundTask? _scan;

    private DiskEntry? _current;
    private EntryViewModel? _selected;
    private EntryViewModel? _pendingDelete;
    private string? _status;
    private string? _errorMessage;
    private bool _isScanning;

    private FolderIdentity? _identity;
    private bool _isIdentifying;
    private CancellationTokenSource? _identifying;

    /// <summary>
    /// Text worked out in code rather than bound with {m:Tr} has to be read again when the
    /// language changes. Nothing moves, but everything reads differently.
    /// </summary>
    private readonly LanguageWatch _language;

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
        FindDuplicatesCommand = new RelayCommand(() => HandTo(KnownPlugins.Purrge), () => Selected is { CanDrillInto: true });
        SortWithKibbleCommand = new RelayCommand(() => HandTo(KnownPlugins.Kibble), () => Selected is { CanDrillInto: true });
        CarryElsewhereCommand = new RelayCommand(() => HandTo(CarryPlugin), () => Selected is { CanDrillInto: true });
        ConfirmDeleteCommand = new RelayCommand(() => Remove(PendingDelete), () => PendingDelete is not null);
        ExtractCommand = new RelayCommand(() => _ = PlanExtractAsync(), () => CanExtract);
        ConfirmExtractCommand = new RelayCommand(RunExtract, () => PendingExtract is { CanGo: true });
        CancelExtractCommand = new RelayCommand(() => PendingExtract = null, () => PendingExtract is not null);
        CancelDeleteCommand = new RelayCommand(() => PendingDelete = null, () => PendingDelete is not null);

        LoadDrives();

        // Where the last scan was, shown and selected but not run. A tab that opened by measuring
        // an entire drive again, unasked, was the reason this tree exists.
        Choose(_settings.LastRoot ?? Tree.FirstOrDefault()?.Path);

        _language = new LanguageWatch(OnEverythingChanged);
    }

    /// <summary>Every ready drive, each opening into the folders under it.</summary>
    public ObservableCollection<NodeViewModel> Tree { get; } = new();

    public ObservableCollection<EntryViewModel> Entries { get; } = new();

    public ObservableCollection<CrumbViewModel> Crumbs { get; } = new();

    public RelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand UpCommand { get; }

    public RelayCommand GoToCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand ExploreCommand { get; }

    /// <summary>The README's argument made real: a fat folder of near duplicates is Purrge's problem.</summary>
    public RelayCommand FindDuplicatesCommand { get; }

    /// <summary>And a fat folder of unsorted material is Kibble's.</summary>
    public RelayCommand SortWithKibbleCommand { get; }

    public bool CanReachPurrge => _host.Handoff.CanReach(KnownPlugins.Purrge);

    public bool CanReachKibble => _host.Handoff.CanReach(KnownPlugins.Kibble);

    /// <summary>Carry's id, spelled here rather than in the contract, which has not changed for it.</summary>
    private const string CarryPlugin = "meows.carry";

    /// <summary>And a big folder worth keeping, but not on this drive, is Carry's.</summary>
    public RelayCommand CarryElsewhereCommand { get; }

    public bool CanReachCarry => _host.Handoff.CanReach(CarryPlugin);

    private void HandTo(string pluginId)
    {
        if (Selected is not { CanDrillInto: true } folder)
            return;

        // The answer lands in the status line, so the result is known without the tab switch.
        var name = System.IO.Path.GetFileName(folder.Path);
        var handoff = Handoff.Folder(folder.Path) with
        {
            Reply = outcome => Status = _host.Text.Format("chonk.status.reply", name, outcome),
        };

        if (!_host.Handoff.Send(pluginId, handoff))
            ErrorMessage = _host.Text.Format("chonk.error.handoff", pluginId);
    }

    public RelayCommand ConfirmDeleteCommand { get; }

    public RelayCommand CancelDeleteCommand { get; }

    private string? _selectedRoot;
    private NodeViewModel? _selectedNode;

    /// <summary>What Scan will measure. Chosen in the tree, or with the folder picker.</summary>
    public string? SelectedRoot
    {
        get => _selectedRoot;
        private set
        {
            if (SetField(ref _selectedRoot, value))
                OnPropertyChanged(nameof(HasRoot));
        }
    }

    public bool HasRoot => !string.IsNullOrWhiteSpace(SelectedRoot);

    /// <summary>
    /// The node picked in the tree. Picking one chooses it and nothing more: the measuring waits
    /// for the Scan button, because a whole drive is a long time to wait for a mis-click.
    /// </summary>
    public NodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetField(ref _selectedNode, value))
                return;

            if (value is { IsPlaceholder: false })
                Choose(value.Path, reveal: false);
        }
    }

    /// <summary>
    /// Chooses what to scan, and shows it in the tree. Nothing is measured until Scan is pressed.
    /// </summary>
    public void Choose(string? path, bool reveal = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        SelectedRoot = path;
        ErrorMessage = null;

        if (!IsScanning && Current is null)
            Status = _host.Text.Format("chonk.status.ready", path);

        if (reveal)
            Reveal(path);
    }

    /// <summary>
    /// Opens the tree down to a path and selects it, listing each folder on the way. The path
    /// may be nowhere under a drive the tree knows, a network share for instance, and then it is
    /// simply chosen without being shown.
    /// </summary>
    private void Reveal(string path)
    {
        var full = path.TrimEnd('\\', '/');

        var drive = Tree.FirstOrDefault(d =>
            full.StartsWith(d.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

        if (drive is null)
            return;

        var node = drive;
        var root = drive.Path.TrimEnd('\\', '/');

        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            var rest = full[(root.Length + 1)..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var walked = root;

            foreach (var segment in rest)
            {
                walked = System.IO.Path.Combine(walked, segment);
                node.IsExpanded = true;

                if (node.Find(walked) is not { } next)
                    return;

                node = next;
            }
        }

        _selectedNode = node;
        OnPropertyChanged(nameof(SelectedNode));
    }

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

    /// <summary>
    /// The one case where the confirmation can be reassuring: the same files are on the other
    /// side of the pair. Unless the folder holds more than the archive, in which case removing
    /// the folder loses exactly those, and that is the thing to say.
    /// </summary>
    private string TwinWarning(TwinReport twin, bool removingFolder)
    {
        var other = System.IO.Path.GetFileName(removingFolder ? twin.ArchivePath : twin.FolderPath);

        return removingFolder && twin.Extra > 0
            ? _host.Text.Format("chonk.warn.twin.extras", twin.Extra, other)
            : _host.Text.Format("chonk.warn.twin", other);
    }

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
                { Twin: { } twin } => TwinWarning(twin, pending.Entry.IsFolder),
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
            ExtractCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanExtract));
        FindDuplicatesCommand.RaiseCanExecuteChanged();
        SortWithKibbleCommand.RaiseCanExecuteChanged();
        CarryElsewhereCommand.RaiseCanExecuteChanged();
            Identify(value);
        }
    }

    /// <summary>
    /// What the selected folder or archive is. Null until it has been worked out, and for a
    /// plain file, which is what it says it is.
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
            OnPropertyChanged(nameof(IdentityIsTwin));
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

    /// <summary>The good news case: the same files are beside it, so either side can go.</summary>
    public bool IdentityIsTwin => Identity is { Verdict: FolderVerdict.Twin };

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

        if (entry is not ({ Entry.IsFolder: true } or { IsArchive: true }))
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
            var archive = entry.IsArchive;
            var found = await Task.Run(
                () => archive ? ArchiveInspector.Of(path, source.Token) : FolderInspector.Of(path, source.Token),
                source.Token);

            // A newer selection may have started while this ran, and its answer wins.
            if (ReferenceEquals(_identifying, source))
                Identity = found;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not work out what {entry.Path} is: {ex.Message}");
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

    // ---- extract and check ----

    private ExtractPlan? _pendingExtract;
    private IBackgroundTask? _extracting;

    /// <summary>
    /// Unpacks the selected archive beside itself and checks the result against its listing.
    /// Asks first, with what it will cost; refuses what it cannot do without writing anything.
    /// </summary>
    public RelayCommand ExtractCommand { get; }

    public RelayCommand ConfirmExtractCommand { get; }

    public RelayCommand CancelExtractCommand { get; }

    /// <summary>A zip-family archive with nothing of its name beside it, and nothing else under way.</summary>
    public bool CanExtract =>
        Selected is { IsArchive: true } archive && Archives.CanRead(archive.Path) && !IsScanning && _extracting is not { IsRunning: true };

    /// <summary>The extraction waiting for a yes: where it lands and what it costs.</summary>
    public ExtractPlan? PendingExtract
    {
        get => _pendingExtract;
        private set
        {
            if (!SetField(ref _pendingExtract, value))
                return;
            OnPropertyChanged(nameof(IsAskingExtract));
            OnPropertyChanged(nameof(ExtractPrompt));
            OnPropertyChanged(nameof(ExtractDetail));
            ConfirmExtractCommand.RaiseCanExecuteChanged();
            CancelExtractCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAskingExtract => _pendingExtract is not null;

    public string ExtractPrompt => _pendingExtract is { } plan
        ? _host.Text.Format("chonk.extract.prompt", System.IO.Path.GetFileName(plan.Archive))
        : "";

    public string ExtractDetail => _pendingExtract is not { } plan
        ? ""
        : plan.Free is { } free
            ? _host.Text.Format("chonk.extract.cost.free", plan.Files, DiskScan.Humanise(plan.Bytes), System.IO.Path.GetFileName(plan.Folder), DiskScan.Humanise(free))
            : _host.Text.Format("chonk.extract.cost", plan.Files, DiskScan.Humanise(plan.Bytes), System.IO.Path.GetFileName(plan.Folder));

    /// <summary>What a refused plan means, in words.</summary>
    private string Refused(ExtractPlan plan) => plan.Refusal switch
    {
        ArchiveExtractor.Refusal.FolderThere => _host.Text.Format("chonk.extract.refused.there", System.IO.Path.GetFileName(plan.Folder)),
        ArchiveExtractor.Refusal.Password => _host.Text["chonk.extract.refused.password"],
        ArchiveExtractor.Refusal.Empty => _host.Text["chonk.extract.refused.empty"],
        ArchiveExtractor.Refusal.NoRoom => _host.Text.Format("chonk.extract.refused.room", DiskScan.Humanise(plan.Bytes), DiskScan.Humanise(plan.Free ?? 0)),
        ArchiveExtractor.Refusal.NotZip => _host.Text["chonk.extract.refused.notzip"],
        _ => _host.Text["chonk.extract.refused.unreadable"],
    };

    private async Task PlanExtractAsync()
    {
        if (!CanExtract || Selected is not { } archive)
            return;
        ErrorMessage = null;
        var path = archive.Path;
        var plan = await Task.Run(() => ArchiveExtractor.Plan(path));
        if (!ReferenceEquals(Selected, archive))
            return;
        if (plan.CanGo)
            PendingExtract = plan;
        else
            ErrorMessage = Refused(plan);
    }

    /// <summary>
    /// Unpacks as background work, then checks. When every listed file is there with its bytes
    /// intact, the archive's identity is worked out again, and it now says it is a twin: the
    /// Recycle Bin button beside it is the offer, with the twin warning that already goes with it.
    /// </summary>
    private void RunExtract()
    {
        if (PendingExtract is not { CanGo: true } plan)
            return;
        PendingExtract = null;
        var name = System.IO.Path.GetFileName(plan.Archive);
        var selected = Selected;

        _extracting = _host.Background.Run(_host.Text.Format("chonk.extract.task", name), async context =>
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                context.Report(_host.Text.Format("chonk.extract.progress", p.Done, p.Total));
                context.ReportProgress(p.Total == 0 ? null : (double)p.Done / p.Total);
            });
            var report = await Task.Run(() => ArchiveExtractor.Extract(plan, progress, context.Token), context.Token);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Extracted(plan, report, selected));
        });
        ExtractCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanExtract));
    }

    private void Extracted(ExtractPlan plan, ExtractReport report, EntryViewModel? selected)
    {
        var name = System.IO.Path.GetFileName(plan.Archive);
        var folder = System.IO.Path.GetFileName(plan.Folder);

        if (report.Check is null)
        {
            ErrorMessage = _host.Text.Format("chonk.extract.failed", name, report.Error ?? "");
        }
        else
        {
            Status = report.Verified
                ? _host.Text.Format("chonk.extract.verified", name, report.Files, folder)
                : _host.Text.Format("chonk.extract.unverified", name, folder, report.Error ?? "");
            _host.Store.Record("extracted", plan.Archive, Status, new Dictionary<string, string>
            {
                [ActionRequest.DestinationKey] = plan.Folder,
                ["files"] = report.Files.ToString(),
                ["verified"] = report.Verified ? "yes" : "no",
            });
            _host.Log($"Chonk unpacked {plan.Archive} into {plan.Folder}: {report.Files} file(s), {(report.Verified ? "every one checked" : report.Error)}.");

            // The pair is new, so what the archive is has changed: it now has a twin beside it.
            if (selected is not null && ReferenceEquals(Selected, selected))
                Identify(selected);
        }

        ExtractCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanExtract));
    }

    public string Status
    {
        get => _status ?? MeowsText.Current["chonk.status.start"];
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
        Tree.Clear();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType != DriveType.CDRom))
                Tree.Add(NodeViewModel.ForDrive(drive));
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

        Choose(root, reveal: false);
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

    /// <summary>
    /// Ctrl+K reaching into the last measurement: every folder and listed file under the scanned
    /// root, wherever it sits, biggest first among the matches. Landing on one shows the level it
    /// is in and selects it, so the card says what it is.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var root = Current;
        while (root?.Parent is not null)
            root = root.Parent;
        if (root is null)
            return [];

        var words = SearchWords.Split(query);
        var matches = new List<DiskEntry>();
        var stack = new Stack<DiskEntry>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            foreach (var child in entry.Children)
            {
                if (child.Kind != DiskEntryKind.SmallFiles && SearchWords.Match(words, child.Name))
                    matches.Add(child);
                if (child.IsFolder)
                    stack.Push(child);
            }
        }

        return matches
            .OrderByDescending(e => e.Size)
            .Take(limit)
            .Select(e => new SearchHit(e.Name, $"{DiskScan.Humanise(e.Size)} · {e.Parent?.Path}", () => Reveal(e)))
            .ToList();
    }

    /// <summary>Shows the level an entry sits in and selects it there.</summary>
    private void Reveal(DiskEntry entry)
    {
        if (entry.Parent is not { } parent)
            return;

        Show(parent);
        Selected = Entries.FirstOrDefault(row => ReferenceEquals(row.Entry, entry));
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
            _host.Log(LogLevel.Warning, $"Chonk could not remove {entry.Path}: {ErrorMessage}");
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
            Explorer.Open(path);
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
            _host.Log(LogLevel.Warning, $"Could not save Chonk settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _language.Dispose();
        _scan?.Cancel();
    }

    // ---- picking, through the host's dialogs rather than a TopLevel of our own ----

    private RelayCommand? _pickFolderCommand;

    public RelayCommand PickFolderCommand => _pickFolderCommand ??= new RelayCommand(() => _ = PickFolderAsync());

    private async Task PickFolderAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["chonk.dialog.folder"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;
            Choose(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }
}
