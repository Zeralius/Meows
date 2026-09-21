using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Rehome.Services;

namespace Meows.Plugins.Rehome.ViewModels;

public sealed class RehomeSettings
{
    /// <summary>The drives the reinstall wipes. Empty means the system drive.</summary>
    public List<string> WipedDrives { get; set; } = [];

    /// <summary>Where the Rehome folder goes.</summary>
    public string? Destination { get; set; }

    /// <summary>The Rehome folder opened last on the way back.</summary>
    public string? LastRehomeFolder { get; set; }

    /// <summary>Programs only from the drives being wiped; off shows the ones on other drives too, marked.</summary>
    public bool OnlyWipedDrives { get; set; } = true;

    /// <summary>The programs ticked for the to-get list, by name, so the ticks survive a rescan.</summary>
    public List<string> Wanted { get; set; } = [];

    /// <summary>Carry OneDrive's cloud-only files too, which means pulling each one down first.</summary>
    public bool PullCloudOnly { get; set; }
}

public enum RehomeStep { Programs, Folders, Extras, Keys, WayBack }

/// <summary>One entry in the left column: a step and how much is in it.</summary>
public sealed class StepViewModel(RehomeStep step, Func<string> count) : ObservableObject
{
    public RehomeStep Step { get; } = step;

    public string Name => MeowsText.Current[$"rehome.step.{Step.ToString().ToLowerInvariant()}"];

    public string Count => count();

    public void Reread() => OnEverythingChanged();
}

/// <summary>
/// Packs the machine up before a clean Windows install, and unpacks it afterwards. Three lists
/// and a short fourth on the way out: what is installed and how to get it back, what the wipe
/// takes, the not-folders, and the keys. Everything is written to the drive the user picked
/// before anything else, because the tab it is shown in will not exist next week. Nothing is
/// ever removed from the source; the wipe is the delete.
/// </summary>
public sealed class RehomeViewModel : ObservableObject, IDisposable, ISearchable
{
    private readonly IMeowsHost _host;
    private readonly RehomeSettings _settings;
    private readonly LanguageWatch _language;
    private IBackgroundTask? _work;
    private int _generation;

    private IReadOnlyList<ProgramMatch> _matches = [];
    private ManagerReport _managers = ManagerReport.Empty;
    private IReadOnlyList<LicenceRow> _keys = [];
    private Manifest? _manifest;
    private string? _rehomeFolder;

    private readonly List<FolderRowViewModel> _allFolders = [];
    private readonly List<RestoreRowViewModel> _allRestoreRows = [];
    private string _filter = "";
    private RehomeStep _step = RehomeStep.Programs;
    private StepViewModel? _selectedStep;
    private ProgramRowViewModel? _selectedProgram;
    private FolderRowViewModel? _selectedFolder;
    private string? _status;
    private string? _errorMessage;
    private bool _isBusy;
    private bool _hasScanned;
    private bool _driveListStale;
    private string? _lastPacked;

    public RehomeViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<RehomeSettings>() ?? new RehomeSettings();

        ScanCommand = new RelayCommand(Scan, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _work?.Cancel(), () => IsBusy);
        PackCommand = new RelayCommand(Pack, () => !IsBusy && HasScanned && CanPack);
        OpenPackedCommand = new RelayCommand(() => OpenFolder(_lastPacked), () => _lastPacked is not null);
        OpenProgramUrlCommand = new RelayCommand(() => Open(SelectedProgram?.Url), () => SelectedProgram?.Url is not null);
        RevealProgramCommand = new RelayCommand(() => Reveal(SelectedProgram?.Where), () => !string.IsNullOrEmpty(SelectedProgram?.Where) && Directory.Exists(SelectedProgram!.Where));
        RevealFolderCommand = new RelayCommand(() => Reveal(SelectedFolder?.Path), () => SelectedFolder is not null);
        PickAllFoldersCommand = new RelayCommand(() => PickFolders(_ => true));
        PickNoFoldersCommand = new RelayCommand(() => PickFolders(_ => false));
        PickSuggestedFoldersCommand = new RelayCommand(() => PickFolders(SuggestedPick));
        CopyKeyCommand = new RelayCommand(p => CopyText?.Invoke((p as KeyRowViewModel)?.Key ?? ""));
        BringBackCommand = new RelayCommand(BringBack, () => !IsBusy && _manifest is not null && (_allRestoreRows.Any(r => r.IsPicked) || RestoreExtras.Any(r => r.IsPicked)));
        PickAllLibraryCommand = new RelayCommand(() => PickLibrary(true));
        PickNoLibraryCommand = new RelayCommand(() => PickLibrary(false));
        WriteToGetCommand = new RelayCommand(WriteToGet, () => !IsBusy && HasScanned && WantedCount > 0 && HasDestination);
        ClearFilterCommand = new RelayCommand(() => Filter = "");
        OpenRehomeFolderCommand = new RelayCommand(() => OpenFolder(_rehomeFolder), () => _rehomeFolder is not null);
        RevealStoredCommand = new RelayCommand(p => Reveal((p as RestoreRowViewModel)?.Stored ?? (p as RestoreExtraViewModel)?.Stored));

        foreach (var step in Enum.GetValues<RehomeStep>())
            Steps.Add(new StepViewModel(step, () => CountFor(step)));
        _selectedStep = Steps[0];

        LoadDrives();

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var s in Steps) s.Reread();
            foreach (var r in Programs) r.Reread();
            foreach (var r in _allFolders) r.Reread();
            foreach (var r in Extras) r.Reread();
            foreach (var r in KeyRows) r.Reread();
            foreach (var r in Drives) r.Reread();
            foreach (var r in _allRestoreRows) r.Reread();
            foreach (var r in RestoreExtras) r.Reread();
        });

        if (_settings.LastRehomeFolder is { } last && Manifest.Load(last) is not null)
            LoadRehomeFolder(last);
    }

    // ---- collections ----

    public ObservableCollection<StepViewModel> Steps { get; } = [];

    public ObservableCollection<ProgramRowViewModel> Programs { get; } = [];

    /// <summary>The folders shown: everything but the library group, as the search box leaves it.</summary>
    public ObservableCollection<FolderRowViewModel> Folders { get; } = [];

    /// <summary>Desktop, Documents, Downloads, Pictures, Videos, Music: the person's own, grouped at the top.</summary>
    public ObservableCollection<FolderRowViewModel> LibraryFolders { get; } = [];

    public ObservableCollection<ExtraRowViewModel> Extras { get; } = [];

    public ObservableCollection<KeyRowViewModel> KeyRows { get; } = [];

    public ObservableCollection<DriveRowViewModel> Drives { get; } = [];

    public ObservableCollection<RestoreRowViewModel> RestoreRows { get; } = [];

    public ObservableCollection<RestoreExtraViewModel> RestoreExtras { get; } = [];

    public IReadOnlyList<ConflictOption> ConflictOptions { get; } =
    [
        new(Conflict.KeepMine, "rehome.conflict.mine"),
        new(Conflict.KeepTheirs, "rehome.conflict.theirs"),
        new(Conflict.KeepBoth, "rehome.conflict.both"),
    ];

    // ---- commands ----

    public RelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand PackCommand { get; }

    public RelayCommand OpenPackedCommand { get; }

    public RelayCommand OpenProgramUrlCommand { get; }

    public RelayCommand RevealProgramCommand { get; }

    public RelayCommand RevealFolderCommand { get; }

    public RelayCommand PickAllFoldersCommand { get; }

    public RelayCommand PickNoFoldersCommand { get; }

    public RelayCommand PickSuggestedFoldersCommand { get; }

    public RelayCommand CopyKeyCommand { get; }

    public RelayCommand BringBackCommand { get; }

    public RelayCommand OpenRehomeFolderCommand { get; }

    public RelayCommand RevealStoredCommand { get; }

    public RelayCommand PickAllLibraryCommand { get; }

    public RelayCommand PickNoLibraryCommand { get; }

    public RelayCommand WriteToGetCommand { get; }

    public RelayCommand ClearFilterCommand { get; }

    // ---- the search box ----

    /// <summary>Narrows the programs, the folders and the way back to what matches; the ticks underneath are untouched.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetField(ref _filter, value ?? ""))
                return;
            OnPropertyChanged(nameof(HasFilter));
            RebuildPrograms();
            RebuildFolders();
            RebuildRestoreRows();
        }
    }

    public bool HasFilter => _filter.Trim().Length > 0;

    private bool Matches(params string?[] texts) => !HasFilter || SearchWords.Match(SearchWords.Split(_filter), texts);

    // ---- the to-get list ----

    public int WantedCount => _settings.Wanted.Count;

    /// <summary>"4 programs ticked to get again" beside the button.</summary>
    public string WantedText => _host.Text.Format("rehome.toget.count", WantedCount);

    private void OnWantedChanged(ProgramRowViewModel row)
    {
        if (row.IsWanted)
        {
            if (!_settings.Wanted.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
                _settings.Wanted.Add(row.Name);
        }
        else
            _settings.Wanted.RemoveAll(n => string.Equals(n, row.Name, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        OnPropertyChanged(nameof(WantedCount));
        OnPropertyChanged(nameof(WantedText));
        WriteToGetCommand.RaiseCanExecuteChanged();
    }

    private IReadOnlyList<ProgramMatch> WantedMatches => _matches.Where(m => _settings.Wanted.Contains(m.Program.Name, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Writes to-get.md and to-get.ps1: into the folder packed this session if there is one, else straight into the destination.</summary>
    private void WriteToGet()
    {
        var wanted = WantedMatches;
        if (wanted.Count == 0 || Destination is null)
            return;
        var folder = _lastPacked ?? Destination;
        try
        {
            var files = ProgramLists.WriteToGet(folder, wanted, DateTime.Now);
            Status = _host.Text.Format("rehome.status.toget", wanted.Count, files[0]);
            _host.Store.Record("to-get", folder, _host.Text.Format("rehome.journal.toget", wanted.Count));
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("rehome.error.toget", ex.Message);
        }
    }

    /// <summary>Set by the view, which owns the clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    // ---- state ----

    public StepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (!SetField(ref _selectedStep, value) || value is null)
                return;
            _step = value.Step;
            OnPropertyChanged(nameof(IsProgramsStep));
            OnPropertyChanged(nameof(IsFoldersStep));
            OnPropertyChanged(nameof(IsExtrasStep));
            OnPropertyChanged(nameof(IsKeysStep));
            OnPropertyChanged(nameof(IsWayBackStep));
            OnPropertyChanged(nameof(IsOutward));
        }
    }

    public bool IsProgramsStep => _step == RehomeStep.Programs;

    public bool IsFoldersStep => _step == RehomeStep.Folders;

    public bool IsExtrasStep => _step == RehomeStep.Extras;

    public bool IsKeysStep => _step == RehomeStep.Keys;

    public bool IsWayBackStep => _step == RehomeStep.WayBack;

    /// <summary>The four steps before the wipe share the destination panel; the way back has its own.</summary>
    public bool IsOutward => _step != RehomeStep.WayBack;

    public ProgramRowViewModel? SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (!SetField(ref _selectedProgram, value))
                return;
            OnPropertyChanged(nameof(HasSelectedProgram));
            OpenProgramUrlCommand.RaiseCanExecuteChanged();
            RevealProgramCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedProgram => _selectedProgram is not null;

    public FolderRowViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetField(ref _selectedFolder, value))
                RevealFolderCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            PackCommand.RaiseCanExecuteChanged();
            BringBackCommand.RaiseCanExecuteChanged();
            WriteToGetCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (!SetField(ref _hasScanned, value))
                return;
            PackCommand.RaiseCanExecuteChanged();
            WriteToGetCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["rehome.status.start"];
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

    public bool HasPacked => _lastPacked is not null;

    // ---- programs ----

    /// <summary>The ticked drives take these; the rest keep their files and lose only the registry entry.</summary>
    private IReadOnlyList<ProgramMatch> Taken => _matches.Where(m => m.Program.OnWipedDrive(WipedDrives)).ToList();

    private IReadOnlyList<ProgramMatch> Elsewhere => _matches.Where(m => !m.Program.OnWipedDrive(WipedDrives)).ToList();

    public bool OnlyWipedDrives
    {
        get => _settings.OnlyWipedDrives;
        set
        {
            if (_settings.OnlyWipedDrives == value)
                return;
            _settings.OnlyWipedDrives = value;
            SaveSettings();
            OnPropertyChanged();
            RebuildPrograms();
        }
    }

    public string ProgramsHeadline
    {
        get
        {
            if (!HasScanned)
                return "";
            var taken = Taken;
            var winget = taken.Count(m => m.Manager == Manager.Winget);
            var choco = taken.Count(m => m.Manager == Manager.Chocolatey);
            var scoop = taken.Count(m => m.Manager == Manager.Scoop);
            var launchers = taken.Count(m => m.Manager == Manager.Launcher);
            var byHand = taken.Count(m => m.ByHand);
            var headline = _host.Text.Format("rehome.programs.headline", taken.Count, winget, choco, scoop, launchers, byHand);
            var elsewhere = Elsewhere.Count;
            return elsewhere == 0 ? headline : headline + " " + _host.Text.Format("rehome.programs.elsewhere", elsewhere);
        }
    }

    /// <summary>Which managers answered, and which are not here to ask.</summary>
    public string ManagersText
    {
        get
        {
            if (!HasScanned)
                return "";
            var bits = new List<string>
            {
                _managers.HasWinget ? _host.Text.Format("rehome.manager.has", "winget", _managers.Winget.Count) : _host.Text.Format("rehome.manager.hasnot", "winget"),
                _managers.HasChocolatey ? _host.Text.Format("rehome.manager.has", "Chocolatey", _managers.Chocolatey.Count) : _host.Text.Format("rehome.manager.hasnot", "Chocolatey"),
                _managers.HasScoop ? _host.Text.Format("rehome.manager.has", "Scoop", _managers.Scoop.Count) : _host.Text.Format("rehome.manager.hasnot", "Scoop"),
            };
            return string.Join(" · ", bits);
        }
    }

    // ---- folders ----

    public IReadOnlyList<string> WipedDrives => Drives.Where(d => d.IsWiped).Select(d => d.Root).ToList();

    public string FoldersHeadline
    {
        get
        {
            if (!HasScanned)
                return "";
            var picked = _allFolders.Where(f => f.IsPicked).ToList();
            return _host.Text.Format("rehome.folders.headline", _allFolders.Count, FolderSize.Humanise(_allFolders.Sum(f => f.Bytes)), picked.Count, FolderSize.Humanise(picked.Sum(f => f.Bytes)));
        }
    }

    public bool DriveListStale
    {
        get => _driveListStale;
        private set => SetField(ref _driveListStale, value);
    }

    private static bool SuggestedPick(FolderRowViewModel row) => row.Candidate.Kind switch
    {
        FolderKind.Library => !row.Survives,
        FolderKind.Cloud => false,
        FolderKind.Profile => !row.Name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase),
        FolderKind.Roaming or FolderKind.LocalLow or FolderKind.Browser or FolderKind.Saves or FolderKind.Root => true,
        _ => false,
    };

    private void PickFolders(Func<FolderRowViewModel, bool> pick)
    {
        foreach (var row in _allFolders)
            row.IsPicked = pick(row);
    }

    private void PickLibrary(bool picked)
    {
        foreach (var row in _allFolders.Where(f => f.IsLibrary))
            row.IsPicked = picked && !row.Survives;
    }

    /// <summary>
    /// The OneDrive option. Off, a cloud-only file is skipped and counted; on, opening it makes
    /// OneDrive fetch it first, so the sizes on the rows grow by what would come down.
    /// </summary>
    public bool PullCloudOnly
    {
        get => _settings.PullCloudOnly;
        set
        {
            if (_settings.PullCloudOnly == value)
                return;
            _settings.PullCloudOnly = value;
            SaveSettings();
            OnPropertyChanged();
            foreach (var row in _allFolders)
            {
                row.IncludeCloud = value;
                row.Reread();
            }
            RereadCounts();
        }
    }

    /// <summary>"3 folders hold 1,204 cloud-only files, 38 GB" under the option, so the cost is said before the tick.</summary>
    public string CloudText
    {
        get
        {
            var rows = _allFolders.Where(f => f.CloudOnly > 0).ToList();
            if (rows.Count == 0)
                return "";
            return _host.Text.Format("rehome.cloud.count", rows.Count, rows.Sum(f => f.CloudOnly), FolderSize.Humanise(rows.Sum(f => f.Measured!.CloudBytes)));
        }
    }

    private void LoadDrives()
    {
        Drives.Clear();
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:";
        var wiped = _settings.WipedDrives.Count > 0 ? _settings.WipedDrives : [system];
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
            {
                var root = drive.Name.TrimEnd('\\');
                var row = new DriveRowViewModel(drive, wiped.Contains(root, StringComparer.OrdinalIgnoreCase), string.Equals(root, system, StringComparison.OrdinalIgnoreCase));
                row.WipedChanged += OnWipedChanged;
                Drives.Add(row);
            }
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Rehome could not list the drives: {ex.Message}");
        }
    }

    private void OnWipedChanged()
    {
        _settings.WipedDrives = WipedDrives.ToList();
        SaveSettings();
        DriveListStale = HasScanned;
        RebuildPrograms();
        OnPropertyChanged(nameof(CanPack));
        OnPropertyChanged(nameof(DestinationWarning));
        PackCommand.RaiseCanExecuteChanged();
    }

    // ---- destination ----

    public string? Destination
    {
        get => _settings.Destination;
        private set
        {
            if (_settings.Destination == value)
                return;
            _settings.Destination = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDestination));
            OnPropertyChanged(nameof(DestinationText));
            OnPropertyChanged(nameof(DestinationWarning));
            OnPropertyChanged(nameof(CanPack));
            PackCommand.RaiseCanExecuteChanged();
            WriteToGetCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasDestination => !string.IsNullOrEmpty(Destination);

    public string DestinationText => Destination ?? _host.Text["rehome.destination.none"];

    /// <summary>The destination on a drive being wiped is no destination; the check is here, not at the end.</summary>
    public string DestinationWarning
    {
        get
        {
            if (Destination is null)
                return "";
            if (Carry.OnWipedDrive(Destination, WipedDrives))
                return _host.Text["rehome.destination.wiped"];
            if (!Directory.Exists(Destination))
                return _host.Text["rehome.destination.gone"];
            try
            {
                var free = new DriveInfo(Path.GetPathRoot(Destination)!).AvailableFreeSpace;
                var need = _allFolders.Where(f => f.IsPicked).Sum(f => f.Bytes);
                if (need > free)
                    return _host.Text.Format("rehome.destination.tight", FolderSize.Humanise(need), FolderSize.Humanise(free));
            }
            catch (Exception)
            {
                // A drive that will not say is one to worry about later.
            }
            return "";
        }
    }

    public bool CanPack => HasDestination && !Carry.OnWipedDrive(Destination!, WipedDrives) && Directory.Exists(Destination);

    public void SetDestination(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;
        Destination = folder;
        Status = Carry.OnWipedDrive(folder, WipedDrives) ? _host.Text["rehome.destination.wiped"] : _host.Text.Format("rehome.status.destination", folder);
    }

    /// <summary>"12 folders, 38 GB, and 5 extras, to F:\" for the button's neighbour.</summary>
    public string PackSummary
    {
        get
        {
            if (!HasScanned)
                return "";
            var picked = _allFolders.Where(f => f.IsPicked).ToList();
            return _host.Text.Format("rehome.pack.summary", picked.Count, FolderSize.Humanise(picked.Sum(f => f.Bytes)), Extras.Count(e => e.IsPicked));
        }
    }

    private string CountFor(RehomeStep step) => step switch
    {
        RehomeStep.Programs => HasScanned ? Programs.Count.ToString() : "",
        RehomeStep.Folders => HasScanned ? $"{_allFolders.Count(f => f.IsPicked)}/{_allFolders.Count}" : "",
        RehomeStep.Extras => HasScanned ? $"{Extras.Count(e => e.IsPicked)}/{Extras.Count}" : "",
        RehomeStep.Keys => HasScanned ? KeyRows.Count(k => k.HasKey).ToString() : "",
        RehomeStep.WayBack => _manifest is null ? "" : _allRestoreRows.Count.ToString(),
        _ => "",
    };

    private void RereadCounts()
    {
        foreach (var s in Steps) s.Reread();
        OnPropertyChanged(nameof(FoldersHeadline));
        OnPropertyChanged(nameof(PackSummary));
        OnPropertyChanged(nameof(DestinationWarning));
        OnPropertyChanged(nameof(CloudText));
    }

    // ---- the scan ----

    private void Scan()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        ErrorMessage = null;
        DriveListStale = false;
        Status = _host.Text["rehome.status.scanning"];
        var generation = ++_generation;
        var wiped = WipedDrives;
        var text = _host.Text;

        _work = _host.Background.Run(_host.Text["rehome.task.scan"], async context =>
        {
            try
            {
                // 1. The registry, then the managers, which take a while and may not be there.
                context.Report(text["rehome.progress.registry"]);
                var programs = await Task.Run(InstalledPrograms.Read, context.Token);
                var managers = await PackageManagers.ProbeAsync(m => context.Report(text.Format("rehome.progress.manager", m)), context.Token);
                var matches = PackageManagers.Match(programs, managers);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _generation) return;
                    ShowPrograms(matches, managers);
                });

                // 2. The folders, listed first so they show, then measured one by one.
                context.Report(text["rehome.progress.folders"]);
                var candidates = await Task.Run(() => WipeList.Gather(wiped, k => text[k]), context.Token);
                var rows = await Dispatcher.UIThread.InvokeAsync(() => generation == _generation ? ShowFolders(candidates) : []);
                var done = 0;
                foreach (var row in rows)
                {
                    context.Token.ThrowIfCancellationRequested();
                    context.Report(text.Format("rehome.progress.measuring", row.Name));
                    context.ReportProgress((double)done++ / Math.Max(1, rows.Count));
                    var measured = await Task.Run(() => WipeList.Measure(row.Candidate, context.Token), context.Token);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (generation != _generation) return;
                        row.Measured = measured;
                        RereadCounts();
                    });
                }
                context.ReportProgress(null);

                // 3. The not-folders and the keys.
                context.Report(text["rehome.progress.extras"]);
                var extras = await Task.Run(Services.Extras.Gather, context.Token);
                context.Report(text["rehome.progress.keys"]);
                var keys = await Keys.ReadAsync(context.Token);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _generation) return;
                    ShowExtras(extras);
                    ShowKeys(keys);
                    HasScanned = true;
                    RereadCounts();
                    OnPropertyChanged(nameof(ProgramsHeadline));
                    OnPropertyChanged(nameof(ManagersText));
                    Status = text.Format("rehome.status.scanned", Taken.Count, _allFolders.Count, FolderSize.Humanise(_allFolders.Sum(f => f.Bytes)), DateTime.Now.ToString("HH:mm"));
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Status = text["rehome.status.cancelled"]);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = text.Format("rehome.error.scan", ex.Message));
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
            }
        });
    }

    private void ShowPrograms(IReadOnlyList<ProgramMatch> matches, ManagerReport managers)
    {
        _matches = matches;
        _managers = managers;
        RebuildPrograms();
        OnPropertyChanged(nameof(ManagersText));
    }

    /// <summary>The list as the toggle and the ticked drives leave it; a program on another drive is marked when shown.</summary>
    private void RebuildPrograms()
    {
        var keep = SelectedProgram?.Name;
        var wiped = WipedDrives;
        Programs.Clear();
        foreach (var match in _matches)
        {
            var taken = match.Program.OnWipedDrive(wiped);
            if (!taken && _settings.OnlyWipedDrives)
                continue;
            if (!Matches(match.Program.Name, match.Program.Publisher, match.Id))
                continue;
            var row = new ProgramRowViewModel(match) { OnWipedDrive = taken };
            row.Wanted(_settings.Wanted.Contains(match.Program.Name, StringComparer.OrdinalIgnoreCase));
            row.WantedChanged += OnWantedChanged;
            Programs.Add(row);
        }
        SelectedProgram = Programs.FirstOrDefault(p => p.Name == keep);
        OnPropertyChanged(nameof(ProgramsHeadline));
        foreach (var s in Steps) s.Reread();
    }

    private IReadOnlyList<FolderRowViewModel> ShowFolders(IReadOnlyList<WipeCandidate> candidates)
    {
        // A tick already given survives a rescan; a new row gets the suggestion.
        var picked = _allFolders.ToDictionary(f => f.Path, f => f.IsPicked, StringComparer.OrdinalIgnoreCase);
        _allFolders.Clear();
        foreach (var candidate in candidates.OrderBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new FolderRowViewModel(candidate, picked.TryGetValue(candidate.Path, out var was) ? was : SuggestedPick(new FolderRowViewModel(candidate, false)))
            {
                IncludeCloud = _settings.PullCloudOnly,
            };
            row.PickedChanged += RereadCounts;
            _allFolders.Add(row);
        }
        RebuildFolders();
        RereadCounts();
        return _allFolders.ToList();
    }

    /// <summary>The two lists as the search box leaves them; the rows themselves, and their ticks, stay.</summary>
    private void RebuildFolders()
    {
        var keep = SelectedFolder;
        LibraryFolders.Clear();
        Folders.Clear();
        foreach (var row in _allFolders)
        {
            if (!Matches(row.Name, row.Path))
                continue;
            if (row.IsLibrary)
                LibraryFolders.Add(row);
            else
                Folders.Add(row);
        }
        SelectedFolder = Folders.Contains(keep!) ? keep : null;
        OnPropertyChanged(nameof(HasLibrary));
    }

    public bool HasLibrary => LibraryFolders.Count > 0;

    private void RebuildRestoreRows()
    {
        RestoreRows.Clear();
        foreach (var row in _allRestoreRows.Where(r => Matches(r.Name, r.Source)))
            RestoreRows.Add(row);
    }

    private void ShowExtras(IReadOnlyList<ExtraItem> extras)
    {
        var picked = Extras.ToDictionary(e => e.Item.Kind, e => e.IsPicked);
        Extras.Clear();
        foreach (var item in extras)
        {
            var row = new ExtraRowViewModel(item);
            if (picked.TryGetValue(item.Kind, out var was))
                row.IsPicked = was && item.Present;
            row.PickedChanged += RereadCounts;
            Extras.Add(row);
        }
    }

    private void ShowKeys(IReadOnlyList<LicenceRow> keys)
    {
        _keys = keys;
        KeyRows.Clear();
        foreach (var row in keys)
            KeyRows.Add(new KeyRowViewModel(row));
    }

    // ---- the pack ----

    private void Pack()
    {
        if (IsBusy || !HasScanned || Destination is null || !CanPack)
            return;
        IsBusy = true;
        ErrorMessage = null;
        var text = _host.Text;
        var when = DateTime.Now;
        var root = Carry.RootFor(Destination, when);
        // A ticked folder inside another ticked folder would be carried twice: Documents that
        // lives inside OneDrive, when both are ticked. The outer one carries it.
        var picked = _allFolders.Where(f => f.IsPicked).ToList();
        var folders = picked.Where(f => !picked.Any(o => !ReferenceEquals(o, f) && f.Path.StartsWith(o.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        var extras = Extras.Where(e => e.IsPicked).Select(e => e.Item).ToList();
        var matches = _matches;
        var wanted = WantedMatches;
        var pullCloud = _settings.PullCloudOnly;
        var managers = _managers;
        var keys = _keys;
        var wiped = WipedDrives.ToList();
        Status = text.Format("rehome.status.packing", root);

        _work = _host.Background.Run(text["rehome.task.pack"], async context =>
        {
            var manifest = new Manifest
            {
                CreatedAt = when,
                Machine = Environment.MachineName,
                User = Environment.UserName,
                WindowsVersion = Environment.OSVersion.VersionString,
                WipedDrives = wiped,
            };
            try
            {
                // The lists first: a list that only ever lived on C: was not a list.
                Directory.CreateDirectory(root);
                context.Report(text["rehome.progress.lists"]);
                await Task.Run(() =>
                {
                    ProgramLists.Write(root, matches, managers, when, wiped);
                    if (wanted.Count > 0)
                        ProgramLists.WriteToGet(root, wanted, when);
                    if (keys.Count > 0)
                        File.WriteAllText(Path.Combine(root, "keys.txt"), Keys.Export(keys, when));
                    manifest.Save(root);
                }, context.Token);

                // Then the folders, each verified, the manifest rewritten after every one so a
                // pack stopped halfway still says what it has.
                var total = folders.Sum(f => f.Bytes);
                long carried = 0;
                foreach (var row in folders)
                {
                    context.Token.ThrowIfCancellationRequested();
                    var entry = new ManifestEntry
                    {
                        Source = row.Path,
                        Stored = Manifest.StoredPathFor(row.Path),
                        Kind = row.Candidate.Kind.ToString(),
                        Note = row.Note,
                    };
                    var before = carried;
                    var outcome = await Task.Run(() => Carry.CopyTree(row.Path, Path.Combine(root, entry.Stored), p =>
                    {
                        context.Report(text.Format("rehome.progress.copying", row.Name, FolderSize.Humanise(before + p.Bytes), FolderSize.Humanise(total)));
                        if (total > 0)
                            context.ReportProgress(Math.Min(1.0, (double)(before + p.Bytes) / total));
                    }, context.Token, pullCloudOnly: pullCloud), context.Token);
                    carried += outcome.Bytes;
                    entry.Files = outcome.Files;
                    entry.Bytes = outcome.Bytes;
                    entry.Verified = outcome.Verified;
                    entry.Skipped = outcome.Skipped;
                    entry.Failed = outcome.Failed.ToList();
                    entry.Newest = outcome.Newest;
                    manifest.Entries.Add(entry);
                    manifest.Save(root);
                    if (outcome.Failed.Count > 0)
                        _host.Log(LogLevel.Warning, $"Rehome: {outcome.Failed.Count} file(s) under {row.Path} did not copy cleanly; the first: {outcome.Failed[0]}");
                }

                context.ReportProgress(null);
                context.Report(text["rehome.progress.extras"]);
                manifest.Extras = (await Services.Extras.CarryAsync(root, extras, context.Token)).ToList();
                manifest.Save(root);

                var failed = manifest.Entries.Sum(e => e.Failed.Count) + manifest.Extras.Count(e => !e.Ok);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _lastPacked = root;
                    OnPropertyChanged(nameof(HasPacked));
                    OpenPackedCommand.RaiseCanExecuteChanged();
                    Status = failed == 0
                        ? text.Format("rehome.status.packed", manifest.Entries.Count, FolderSize.Humanise(carried), root)
                        : text.Format("rehome.status.packed.failed", manifest.Entries.Count, FolderSize.Humanise(carried), root, failed);
                    _host.Store.Record("packed", root, text.Format("rehome.journal.packed", manifest.Entries.Count, FolderSize.Humanise(carried)),
                        new Dictionary<string, string> { ["folders"] = manifest.Entries.Count.ToString(), ["bytes"] = carried.ToString(), ["failed"] = failed.ToString() });
                    _host.Notifications.Post(failed == 0 ? NotificationSeverity.Info : NotificationSeverity.Warning,
                        text["rehome.notify.packed"], Status,
                        new NotificationAction(text["rehome.notify.open"], () => OpenFolder(root), DismissesAfter: true));
                });
            }
            catch (OperationCanceledException)
            {
                try { manifest.Save(root); } catch (Exception) { }
                await Dispatcher.UIThread.InvokeAsync(() => Status = text.Format("rehome.status.pack.cancelled", manifest.Entries.Count, root));
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = text.Format("rehome.error.pack", ex.Message));
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
            }
        });
    }

    // ---- the way back ----

    public string? RehomeFolder => _rehomeFolder;

    public bool HasManifest => _manifest is not null;

    /// <summary>"Packed 21 Sep 2026 on DESKTOP for Dennis: 12 folders, 38 GB, 5 extras."</summary>
    public string ManifestHeadline => _manifest is null
        ? ""
        : _host.Text.Format("rehome.back.headline", _manifest.CreatedAt.ToString("d MMM yyyy HH:mm"), _manifest.Machine, _manifest.User,
            _manifest.Entries.Count, FolderSize.Humanise(_manifest.Entries.Sum(e => e.Bytes)), _manifest.Extras.Count);

    public void LoadRehomeFolder(string folder)
    {
        var manifest = Manifest.Load(folder);
        if (manifest is null)
        {
            ErrorMessage = _host.Text.Format("rehome.error.nomanifest", folder);
            return;
        }
        _manifest = manifest;
        _rehomeFolder = folder;
        _settings.LastRehomeFolder = folder;
        SaveSettings();
        _allRestoreRows.Clear();
        foreach (var entry in manifest.Entries)
        {
            var row = new RestoreRowViewModel(entry, folder, ConflictOptions);
            row.PickedChanged += () => BringBackCommand.RaiseCanExecuteChanged();
            _allRestoreRows.Add(row);
        }
        RebuildRestoreRows();
        RestoreExtras.Clear();
        foreach (var extra in manifest.Extras)
        {
            var row = new RestoreExtraViewModel(extra, folder);
            row.PickedChanged += () => BringBackCommand.RaiseCanExecuteChanged();
            RestoreExtras.Add(row);
        }
        OnPropertyChanged(nameof(RehomeFolder));
        OnPropertyChanged(nameof(HasManifest));
        OnPropertyChanged(nameof(ManifestHeadline));
        OpenRehomeFolderCommand.RaiseCanExecuteChanged();
        BringBackCommand.RaiseCanExecuteChanged();
        RereadCounts();
        SelectedStep = Steps.First(s => s.Step == RehomeStep.WayBack);
        ErrorMessage = null;
        Status = _host.Text.Format("rehome.status.manifest", manifest.Entries.Count, folder);
    }

    private void BringBack()
    {
        if (IsBusy || _manifest is null || _rehomeFolder is null)
            return;
        IsBusy = true;
        ErrorMessage = null;
        var text = _host.Text;
        var rows = _allRestoreRows.Where(r => r.IsPicked && r.HasCopy).ToList();
        var extras = RestoreExtras.Where(r => r.IsPicked && r.HasCopy).ToList();
        Status = text["rehome.status.bringing"];

        _work = _host.Background.Run(text["rehome.task.back"], async context =>
        {
            var failed = 0;
            var brought = 0;
            try
            {
                foreach (var row in rows)
                {
                    context.Token.ThrowIfCancellationRequested();
                    context.Report(text.Format("rehome.progress.back", row.Name));
                    var conflict = row.Conflict.Value;
                    string? said = null;
                    var ok = true;
                    try
                    {
                        if (row.Exists && conflict == Conflict.KeepBoth)
                        {
                            var old = row.Source.TrimEnd('\\') + ".old";
                            var n = 2;
                            while (Directory.Exists(old))
                                old = row.Source.TrimEnd('\\') + $".old{n++}";
                            Directory.Move(row.Source, old);
                            said = text.Format("rehome.back.moved", Path.GetFileName(old));
                        }
                        var outcome = await Task.Run(() => Carry.CopyTree(row.Stored, row.Source,
                            p => context.Report(text.Format("rehome.progress.back.file", row.Name, p.Files, row.Entry.Files)),
                            context.Token,
                            keepExisting: row.Exists && conflict == Conflict.KeepMine ? _ => true : null), context.Token);
                        ok = outcome.Failed.Count == 0;
                        said = (said is null ? "" : said + " ") + (ok
                            ? text.Format("rehome.back.done", outcome.Verified, outcome.Skipped)
                            : text.Format("rehome.back.partly", outcome.Verified, outcome.Failed.Count, outcome.Failed[0]));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        said = ex.Message;
                    }
                    if (ok) brought++; else failed++;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        row.Outcome = said;
                        row.Failed = !ok;
                    });
                }

                foreach (var row in extras)
                {
                    context.Token.ThrowIfCancellationRequested();
                    context.Report(text.Format("rehome.progress.back", row.Name));
                    var (ok, said) = await PutBackExtraAsync(row, context.Token);
                    if (ok) brought++; else failed++;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        row.Outcome = said;
                        row.Failed = !ok;
                    });
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Status = failed == 0 ? text.Format("rehome.status.brought", brought) : text.Format("rehome.status.brought.failed", brought, failed);
                    _host.Store.Record("restored", _rehomeFolder, text.Format("rehome.journal.restored", brought, failed));
                    _host.Notifications.Post(failed == 0 ? NotificationSeverity.Info : NotificationSeverity.Warning, text["rehome.notify.back"], Status);
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Status = text["rehome.status.cancelled"]);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = text.Format("rehome.error.back", ex.Message));
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
            }
        });
    }

    private async Task<(bool, string)> PutBackExtraAsync(RestoreExtraViewModel row, CancellationToken token)
    {
        var text = _host.Text;
        try
        {
            switch (row.Kind)
            {
                case ExtraKind.Wifi:
                {
                    var results = await Services.Extras.AddWifiProfilesAsync(row.Stored, token);
                    var added = results.Count(r => r.Ok);
                    var said = string.Join("; ", results.Select(r => $"{r.Name}: {(r.Ok ? text["rehome.back.wifi.added"] : r.Said)}"));
                    return (added == results.Count && results.Count > 0, said.Length == 0 ? text["rehome.back.wifi.none"] : said);
                }
                case ExtraKind.GitConfig:
                {
                    var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gitconfig");
                    if (File.Exists(target))
                        File.Copy(target, target + ".old", overwrite: true);
                    File.Copy(row.Stored, target, overwrite: true);
                    return (true, text.Format("rehome.back.file", target));
                }
                case ExtraKind.PowerShellProfile:
                {
                    var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var failed = new List<string>();
                    foreach (var folder in Directory.GetDirectories(row.Stored))
                    {
                        var outcome = await Task.Run(() => Carry.CopyTree(folder, Path.Combine(documents, Path.GetFileName(folder)), null, token, keepExisting: _ => true), token);
                        failed.AddRange(outcome.Failed);
                    }
                    return (failed.Count == 0, failed.Count == 0 ? text.Format("rehome.back.file", documents) : failed[0]);
                }
                case ExtraKind.Fonts:
                {
                    // Copying into the per-user fonts folder is not installing; the shell's font folder does the registering.
                    var result = await Run.PowerShellAsync(
                        $"$fonts = (New-Object -ComObject Shell.Application).Namespace(0x14); Get-ChildItem -LiteralPath '{row.Stored.Replace("'", "''")}' -File | ForEach-Object {{ $fonts.CopyHere($_.FullName, 0x10) }}; (Get-ChildItem -LiteralPath '{row.Stored.Replace("'", "''")}' -File).Count",
                        token, TimeSpan.FromMinutes(5));
                    var count = result.Output.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && l.All(char.IsDigit));
                    return (result.Succeeded, result.Succeeded ? text.Format("rehome.back.fonts", count ?? "?") : string.Join(" ", result.Output));
                }
                default:
                    return (false, text["rehome.back.extra.byhand"]);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- small verbs ----

    private void Open(string? target)
    {
        if (string.IsNullOrEmpty(target))
            return;
        try
        {
            Explorer.Open(target);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("rehome.error.open", target, ex.Message);
        }
    }

    private void OpenFolder(string? folder) => Open(folder);

    private void Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (Directory.Exists(path))
                Explorer.Open(path);
            else
                Explorer.Reveal(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("rehome.error.open", path, ex.Message);
        }
    }

    /// <summary>Ctrl+K: a program by name or publisher, a folder by name or path.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();
        foreach (var row in Programs)
        {
            if (hits.Count >= limit) break;
            if (!SearchWords.Match(words, row.Name, row.Publisher)) continue;
            var chosen = row;
            hits.Add(new SearchHit(row.Name, row.HowText, () =>
            {
                SelectedStep = Steps.First(s => s.Step == RehomeStep.Programs);
                SelectedProgram = chosen;
            }));
        }
        foreach (var row in _allFolders)
        {
            if (hits.Count >= limit) break;
            if (!SearchWords.Match(words, row.Name, row.Path)) continue;
            var chosen = row;
            hits.Add(new SearchHit(row.Name, $"{row.SizeText} · {row.Path}", () =>
            {
                SelectedStep = Steps.First(s => s.Step == RehomeStep.Folders);
                SelectedFolder = chosen;
            }));
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
            _host.Log(LogLevel.Warning, $"Could not save Rehome settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _work?.Cancel();
        _language.Dispose();
    }
}
