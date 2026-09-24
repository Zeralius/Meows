using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Carry.ViewModels;

/// <summary>A folder that was carried: where it was, where it went, and what it weighed.</summary>
public sealed record CarriedFolder(string Source, string Target, long Bytes, int Files, DateTime WhenUtc);

public sealed class CarrySettings
{
    /// <summary>The drive last carried to, so the next one starts there.</summary>
    public string? DestinationRoot { get; set; }

    public List<CarriedFolder> Carried { get; set; } = [];
}

/// <summary>A drive that can take a folder: fixed, ready, with its room.</summary>
public sealed class DriveChoiceViewModel(CarryDrive drive, bool isSelected) : ObservableObject
{
    private bool _isSelected = isSelected;

    public CarryDrive Drive { get; } = drive;

    public string Root => Drive.Volume;

    public string FreeText => MeowsText.Current.Format("carry.drive.free", FolderSize.Humanise(Drive.Free));

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

/// <summary>One carried folder, with whether its junction still leads where it should.</summary>
public sealed class CarriedViewModel(CarriedFolder folder) : ObservableObject
{
    public CarriedFolder Folder { get; } = folder;

    public string Source => Folder.Source;

    public string Target => Folder.Target;

    public string SizeText => FolderSize.Humanise(Folder.Bytes);

    /// <summary>The junction still there and leading to a copy that is there. Worked out when the list is read.</summary>
    public CarryStanding Standing { get; } = StandingOf(folder);

    public bool IsBroken => Standing != CarryStanding.Fine;

    public string StandingText => MeowsText.Current[Standing switch
    {
        CarryStanding.Fine => "carry.standing.fine",
        CarryStanding.TargetMissing => "carry.standing.missing",
        _ => "carry.standing.unlinked",
    }];

    public static CarryStanding StandingOf(CarriedFolder folder)
    {
        var target = FolderMove.LinkTarget(folder.Source);
        if (target is null)
            return CarryStanding.Unlinked;
        return Directory.Exists(folder.Target) ? CarryStanding.Fine : CarryStanding.TargetMissing;
    }

    internal void Reread() => OnEverythingChanged();
}

public enum CarryStanding
{
    Fine,

    /// <summary>The junction is there but the drive it leads to is not, or the copy went.</summary>
    TargetMissing,

    /// <summary>The source is no longer a junction: brought back by hand, or replaced.</summary>
    Unlinked,
}

/// <summary>
/// Carry: a folder moved to a drive with room, checked, and a junction left behind. Every other
/// plugin answers a full drive by deleting; this answers it by moving what is worth keeping but
/// does not need the fastest drive. It asks first, with the cost; it refuses before writing
/// anything when it should; and it keeps a list of what it carried, with the way back.
/// </summary>
public sealed class CarryViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable, IHandoffTarget
{
    private readonly IMeowsHost _host;
    private readonly Func<IReadOnlyList<CarryDrive>> _drives;
    private readonly Func<string, CarryDrive?> _driveOf;
    private readonly Func<string, string, bool> _makeLink;
    private readonly CarrySettings _settings;
    private readonly LanguageWatch _language;

    private string _source = "";
    private CarryPlan? _plan;
    private bool _isPlanning;
    private bool _isAsking;
    private IBackgroundTask? _work;
    private bool _isWorking;
    private string? _status;
    private string? _errorMessage;
    private int _planGeneration;

    public CarryViewModel(IMeowsHost host) : this(host, FixedDrives, FolderMove.DriveOf, Junction.Create)
    {
    }

    /// <summary>With the drives, and the way a junction is made, supplied from elsewhere, which is how a test runs it.</summary>
    public CarryViewModel(IMeowsHost host, Func<IReadOnlyList<CarryDrive>> drives, Func<string, CarryDrive?> driveOf, Func<string, string, bool> makeLink)
    {
        _host = host;
        _drives = drives;
        _driveOf = driveOf;
        _makeLink = makeLink;
        _settings = host.LoadSettings<CarrySettings>() ?? new CarrySettings();

        PickSourceCommand = new RelayCommand(() => _ = PickSourceAsync(), () => !IsWorking);
        ChooseDriveCommand = new RelayCommand(p => ChooseDrive(p as DriveChoiceViewModel), _ => !IsWorking);
        CarryCommand = new RelayCommand(() => IsAsking = true, () => CanCarry);
        ConfirmCommand = new RelayCommand(StartCarry, () => IsAsking && CanCarry);
        CancelAskCommand = new RelayCommand(() => IsAsking = false);
        StopCommand = new RelayCommand(() => _work?.Cancel(), () => IsWorking);
        BringBackCommand = new RelayCommand(p => StartBringBack(p as CarriedViewModel), p => !IsWorking && p is CarriedViewModel { Standing: CarryStanding.Fine });
        RevealCommand = new RelayCommand(p => Reveal(p as CarriedViewModel));
        ForgetCommand = new RelayCommand(p => Forget(p as CarriedViewModel), p => p is CarriedViewModel { IsBroken: true });
        RefreshCommand = new RelayCommand(RefreshAll);

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var carried in Carried)
                carried.Reread();
        });

        RefreshAll();
    }

    public ObservableCollection<DriveChoiceViewModel> Drives { get; } = [];

    public ObservableCollection<CarriedViewModel> Carried { get; } = [];

    public RelayCommand PickSourceCommand { get; }

    public RelayCommand ChooseDriveCommand { get; }

    public RelayCommand CarryCommand { get; }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelAskCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand BringBackCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand ForgetCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public string Source
    {
        get => _source;
        set
        {
            if (!SetField(ref _source, value ?? ""))
                return;
            OnPropertyChanged(nameof(HasSource));
            Replan();
        }
    }

    public bool HasSource => Source.Length > 0;

    public string? DestinationRoot => _settings.DestinationRoot;

    public CarryPlan? Plan
    {
        get => _plan;
        private set
        {
            if (!SetField(ref _plan, value))
                return;
            OnPropertyChanged(nameof(PlanText));
            OnPropertyChanged(nameof(RefusalText));
            OnPropertyChanged(nameof(HasRefusal));
            OnPropertyChanged(nameof(CanCarry));
            OnPropertyChanged(nameof(AskText));
            CarryCommand.RaiseCanExecuteChanged();
            ConfirmCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsPlanning
    {
        get => _isPlanning;
        private set => SetField(ref _isPlanning, value);
    }

    public bool CanCarry => Plan is { CanCarry: true } && !IsWorking && !IsPlanning;

    public string PlanText => Plan is { CanCarry: true } plan
        ? _host.Text.Format("carry.plan", plan.Files, FolderSize.Humanise(plan.Bytes), plan.Target)
        : "";

    public bool HasRefusal => Plan is { CanCarry: false };

    public string RefusalText => Plan is { CanCarry: false } plan ? Refusal(plan) : "";

    private string Refusal(CarryPlan plan) => plan.Refusal switch
    {
        CarryRefusal.NotThere => _host.Text["carry.refused.notthere"],
        CarryRefusal.IsLink => _host.Text["carry.refused.islink"],
        CarryRefusal.Protected => _host.Text["carry.refused.protected"],
        CarryRefusal.SameDrive => _host.Text["carry.refused.samedrive"],
        CarryRefusal.NotFixed => _host.Text["carry.refused.notfixed"],
        CarryRefusal.TargetThere => _host.Text.Format("carry.refused.there", plan.Target),
        CarryRefusal.NoRoom => _host.Text.Format("carry.refused.room", FolderSize.Humanise(plan.Bytes), FolderSize.Humanise(plan.Free ?? 0)),
        CarryRefusal.CloudOnly => _host.Text["carry.refused.cloud"],
        CarryRefusal.HoldsLinks => _host.Text["carry.refused.links"],
        CarryRefusal.Overlaps => _host.Text["carry.refused.overlaps"],
        _ => _host.Text["carry.refused.unreadable"],
    };

    public bool IsAsking
    {
        get => _isAsking;
        private set
        {
            if (!SetField(ref _isAsking, value))
                return;
            ConfirmCommand.RaiseCanExecuteChanged();
        }
    }

    public string AskText => Plan is { } plan
        ? _host.Text.Format("carry.ask", Path.GetFileName(plan.Source), FolderSize.Humanise(plan.Bytes), plan.Target)
        : "";

    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (!SetField(ref _isWorking, value))
                return;
            OnPropertyChanged(nameof(CanCarry));
            CarryCommand.RaiseCanExecuteChanged();
            ConfirmCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            PickSourceCommand.RaiseCanExecuteChanged();
            ChooseDriveCommand.RaiseCanExecuteChanged();
            BringBackCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["carry.status.ready"];
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

    public bool HasCarried => Carried.Count > 0;

    /// <summary>Fixed drives that are ready, with their room. Removable and network drives are never offered.</summary>
    public static IReadOnlyList<CarryDrive> FixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => new CarryDrive(d.RootDirectory.FullName, d.DriveType, d.AvailableFreeSpace))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void RefreshAll()
    {
        Drives.Clear();
        foreach (var drive in _drives().OrderByDescending(d => d.Free))
            Drives.Add(new DriveChoiceViewModel(drive, string.Equals(drive.Volume, _settings.DestinationRoot, StringComparison.OrdinalIgnoreCase)));

        Carried.Clear();
        foreach (var folder in _settings.Carried.OrderByDescending(c => c.WhenUtc))
            Carried.Add(new CarriedViewModel(folder));
        OnPropertyChanged(nameof(HasCarried));
        Replan();
    }

    private void ChooseDrive(DriveChoiceViewModel? drive)
    {
        if (drive is null)
            return;
        foreach (var d in Drives)
            d.IsSelected = ReferenceEquals(d, drive);
        _settings.DestinationRoot = drive.Root;
        SaveSettings();
        OnPropertyChanged(nameof(DestinationRoot));
        Replan();
    }

    /// <summary>Measures again whenever the folder or the drive changes. Measuring a big folder takes a moment, so it runs off the UI thread.</summary>
    private void Replan()
    {
        IsAsking = false;
        var source = Source;
        var destination = _settings.DestinationRoot;
        var generation = ++_planGeneration;
        if (source.Length == 0 || string.IsNullOrEmpty(destination))
        {
            Plan = null;
            return;
        }

        IsPlanning = true;
        Plan = null;
        Task.Run(() => FolderMove.Plan(source, destination, _driveOf)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _planGeneration)
                    return;
                IsPlanning = false;
                Plan = t.IsCompletedSuccessfully ? t.Result : null;
                OnPropertyChanged(nameof(CanCarry));
            });
        });
    }

    /// <summary>For a test: measure now, on this thread.</summary>
    public void PlanNow()
    {
        _planGeneration++;
        IsPlanning = false;
        Plan = Source.Length == 0 || string.IsNullOrEmpty(_settings.DestinationRoot)
            ? null
            : FolderMove.Plan(Source, _settings.DestinationRoot, _driveOf);
        OnPropertyChanged(nameof(CanCarry));
    }

    private void StartCarry()
    {
        IsAsking = false;
        if (Plan is not { CanCarry: true } plan)
            return;

        IsWorking = true;
        ErrorMessage = null;
        Status = _host.Text.Format("carry.status.copying", Path.GetFileName(plan.Source));
        _work = _host.Background.Run(_host.Text.Format("carry.task", Path.GetFileName(plan.Source)), async context =>
        {
            var progress = new Progress<CarryProgress>(p =>
                Status = _host.Text.Format("carry.status.progress", FolderSize.Humanise(p.Bytes), FolderSize.Humanise(p.Of)));
            CarryOutcome outcome;
            try
            {
                outcome = await Task.Run(() => FolderMove.Carry(plan, progress, context.Token, _makeLink));
            }
            catch (OperationCanceledException)
            {
                outcome = new CarryOutcome(false, "stopped", 0, 0);
            }
            await Dispatcher.UIThread.InvokeAsync(() => Finished(plan, outcome));
        });
    }

    /// <summary>What happens after a carry, on the UI thread. Public so a test can run the carry itself.</summary>
    public void Finished(CarryPlan plan, CarryOutcome outcome)
    {
        IsWorking = false;
        if (outcome.Done)
        {
            _settings.Carried.RemoveAll(c => string.Equals(c.Source, plan.Source, StringComparison.OrdinalIgnoreCase));
            _settings.Carried.Add(new CarriedFolder(plan.Source, plan.Target, outcome.Bytes, outcome.Files, DateTime.UtcNow));
            SaveSettings();
            _host.Store.Record("carried", plan.Source, _host.Text.Format("carry.journal", FolderSize.Humanise(outcome.Bytes), plan.Target),
                new Dictionary<string, string> { ["target"] = plan.Target, ["bytes"] = outcome.Bytes.ToString(), ["files"] = outcome.Files.ToString() });
            _host.Log($"Carried {plan.Source} to {plan.Target}: {outcome.Files} file(s), {outcome.Bytes} bytes, junction left.");
            Status = outcome.Left is { } left
                ? _host.Text.Format("carry.done.left", FolderSize.Humanise(outcome.Bytes), left)
                : _host.Text.Format("carry.done", FolderSize.Humanise(outcome.Bytes), plan.Target);
            Source = "";
        }
        else
        {
            ErrorMessage = Failure(outcome);
            Status = _host.Text["carry.status.ready"];
            _host.Log(LogLevel.Warning, $"Carry of {plan.Source} stopped: {outcome.Failure}");
        }
        RefreshAll();
    }

    private string Failure(CarryOutcome outcome) => outcome.Failure switch
    {
        "inuse" => _host.Text["carry.failed.inuse"],
        "changed" => _host.Text["carry.failed.changed"],
        "link" when outcome.Left is { } left => _host.Text.Format("carry.failed.link.left", left),
        "link" => _host.Text["carry.failed.link"],
        "stopped" => _host.Text["carry.failed.stopped"],
        "notlinked" => _host.Text["carry.failed.notlinked"],
        nameof(CarryRefusal.NoRoom) => _host.Text["carry.failed.room"],
        { } other when outcome.Left is { } left => _host.Text.Format("carry.failed.other.left", other, left),
        { } other => _host.Text.Format("carry.failed.other", other),
        _ => _host.Text["carry.failed.other"],
    };

    private void StartBringBack(CarriedViewModel? carried)
    {
        if (carried is not { Standing: CarryStanding.Fine })
            return;
        var folder = carried.Folder;
        IsWorking = true;
        ErrorMessage = null;
        Status = _host.Text.Format("carry.status.returning", Path.GetFileName(folder.Source));
        _work = _host.Background.Run(_host.Text.Format("carry.task.back", Path.GetFileName(folder.Source)), async context =>
        {
            var progress = new Progress<CarryProgress>(p =>
                Status = _host.Text.Format("carry.status.progress", FolderSize.Humanise(p.Bytes), FolderSize.Humanise(p.Of)));
            CarryOutcome outcome;
            try
            {
                outcome = await Task.Run(() => FolderMove.BringBack(folder.Source, progress, context.Token, _driveOf));
            }
            catch (OperationCanceledException)
            {
                outcome = new CarryOutcome(false, "stopped", 0, 0);
            }
            await Dispatcher.UIThread.InvokeAsync(() => BroughtBack(folder, outcome));
        });
    }

    /// <summary>What happens after a bring-back, on the UI thread.</summary>
    public void BroughtBack(CarriedFolder folder, CarryOutcome outcome)
    {
        IsWorking = false;
        if (outcome.Done)
        {
            _settings.Carried.RemoveAll(c => string.Equals(c.Source, folder.Source, StringComparison.OrdinalIgnoreCase));
            SaveSettings();
            _host.Store.Record("broughtback", folder.Source, _host.Text.Format("carry.journal.back", FolderSize.Humanise(outcome.Bytes), folder.Target),
                new Dictionary<string, string> { ["target"] = folder.Target, ["bytes"] = outcome.Bytes.ToString() });
            _host.Log($"Brought {folder.Source} back from {folder.Target}.");
            Status = _host.Text.Format("carry.back.done", Path.GetFileName(folder.Source));
        }
        else
        {
            ErrorMessage = Failure(outcome);
            Status = _host.Text["carry.status.ready"];
        }
        RefreshAll();
    }

    /// <summary>Drops a broken entry from the list. Nothing on disk is touched.</summary>
    private void Forget(CarriedViewModel? carried)
    {
        if (carried is not { IsBroken: true })
            return;
        _settings.Carried.RemoveAll(c => string.Equals(c.Source, carried.Source, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        RefreshAll();
    }

    private void Reveal(CarriedViewModel? carried)
    {
        if (carried is null)
            return;
        try
        {
            if (Directory.Exists(carried.Target))
                Explorer.Open(carried.Target);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task PickSourceAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["carry.dialog.source"] });
            if (!string.IsNullOrWhiteSpace(picked))
                Source = picked;
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    public bool Accepts(Handoff handoff) => handoff.Verb == HandoffVerbs.Folder && handoff.Paths.Count == 1 && !IsWorking;

    /// <summary>A folder from Chonk: measured against the drive last used, nothing moved until asked.</summary>
    public void Receive(Handoff handoff)
    {
        if (!Accepts(handoff))
            return;
        Source = handoff.Paths[0];
        handoff.Answer(_host.Text.Format("carry.handoff.answer", Path.GetFileName(Source)));
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        return Carried
            .Where(c => SearchWords.Match(words, c.Source, c.Target))
            .Take(limit)
            .Select(c => new SearchHit(Path.GetFileName(c.Source), $"{c.SizeText} · {c.Target}", () => { }))
            .ToList();
    }

    /// <summary>Home's line: trouble when a junction leads nowhere, which breaks every path that used it.</summary>
    public Glance? Glance()
    {
        var broken = Carried.Where(c => c.Standing == CarryStanding.TargetMissing).ToList();
        if (broken.Count > 0)
            return new Glance(_host.Text.Format("carry.glance.broken", broken.Count, Path.GetFileName(broken[0].Source)), IsTrouble: true);
        if (Carried.Count == 0)
            return null;
        return new Glance(_host.Text.Format("carry.glance", Carried.Count, FolderSize.Humanise(Carried.Sum(c => c.Folder.Bytes))));
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Carry settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _language.Dispose();
        _work?.Cancel();
    }
}
