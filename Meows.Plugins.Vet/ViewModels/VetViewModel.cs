using System.Collections.ObjectModel;
using System.Globalization;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Vet.Services;

namespace Meows.Plugins.Vet.ViewModels;

public sealed class VetSettings
{
    public string? BackupFolder { get; set; }

    public int WarnDays { get; set; } = Checkup.DefaultWarnDays;

    public CheckupSummary? LastCheckup { get; set; }
}

public sealed class DriveViewModel : ObservableObject
{
    public DriveViewModel(DriveRow drive)
    {
        Drive = drive;
    }

    public DriveRow Drive { get; }

    public string Name => Drive.Name;

    public bool IsLow => Drive.IsLow;

    public string RoomText => Checkup.Humanise(Drive.FreeBytes) + " / " + Checkup.Humanise(Drive.TotalBytes);

    public double FreeFraction => Drive.TotalBytes <= 0
        ? 0
        : Math.Clamp((double)Drive.FreeBytes / Drive.TotalBytes, 0, 1);
}

/// <summary>
/// Vet: the family PC in big text. Room on the drives, whether Windows wants a reboot, how old
/// the backup is, and a diagnostic zip for whoever helps. Reading only, always; the zip is the
/// one file it ever writes, only when asked.
/// </summary>
public sealed class VetViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable
{
    /// <summary>The condition key. One per plugin scope, so it replaces rather than stacks.</summary>
    private const string HealthKey = "health";

    private readonly IMeowsHost _host;
    private readonly VetSettings _settings;
    private readonly LanguageWatch _language;

    private DriveViewModel? _selected;
    private List<DriveRow> _drives = [];
    private bool? _reboot;
    private int? _backupDays;
    private string? _status;
    private string? _errorMessage;

    private readonly MachineReadings _machine;

    public VetViewModel(IMeowsHost host, MachineReadings? machine = null)
    {
        _host = host;
        _machine = machine ?? MachineReadings.Real;
        _settings = host.LoadSettings<VetSettings>() ?? new VetSettings();

        CheckupCommand = new RelayCommand(CheckupNow);
        ExportCommand = new RelayCommand(() => _ = ExportAsync());
        SetBackupCommand = new RelayCommand(() => _ = SetBackupAsync());
        ClearBackupCommand = new RelayCommand(ClearBackup, () => HasBackup);

        _language = new LanguageWatch(Retranslate);

        CheckupNow();
    }

    /// <summary>Dates as the window's language writes them, not as the machine does.</summary>
    public static CultureInfo Culture => MeowsText.Current.Language == "de"
        ? CultureInfo.GetCultureInfo("de-DE")
        : CultureInfo.GetCultureInfo("en-GB");

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public RelayCommand CheckupCommand { get; }

    public RelayCommand ExportCommand { get; }

    public RelayCommand SetBackupCommand { get; }

    public RelayCommand ClearBackupCommand { get; }

    public DriveViewModel? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public bool HasBackup => !string.IsNullOrWhiteSpace(_settings.BackupFolder);

    public string BackupText => HasBackup
        ? _settings.BackupFolder!
        : _host.Text["vet.nobackup"];

    public string BackupAgeText
    {
        get
        {
            if (!HasBackup)
                return _host.Text["vet.backup.unset"];
            if (_backupDays is not { } days)
                return _host.Text["vet.backup.unknown"];
            return days == 0
                ? _host.Text["vet.backup.today"]
                : _host.Text.Format("vet.backup.days", days);
        }
    }

    public string RebootText => _reboot switch
    {
        true => _host.Text["vet.reboot.yes"],
        false => _host.Text["vet.reboot.no"],
        null => _host.Text["vet.reboot.unknown"],
    };

    /// <summary>Days before a backup counts as stale, as typed. Anything that is not a positive number is a week.</summary>
    public string WarnDaysText
    {
        get => _settings.WarnDays.ToString(CultureInfo.CurrentCulture);
        set
        {
            var days = int.TryParse(value, CultureInfo.CurrentCulture, out var parsed) && parsed > 0
                ? parsed
                : Checkup.DefaultWarnDays;
            if (_settings.WarnDays == days)
                return;
            _settings.WarnDays = days;
            OnPropertyChanged();
            Save();
            OnPropertyChanged(nameof(Summary));
            Recheck();
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["vet.status.ready"];
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

    public bool IsEmpty => Drives.Count == 0;

    /// <summary>The whole checkup in one line, from the last run.</summary>
    public string Summary => Checkup.SummaryOf(_settings.LastCheckup, HasBackup, _settings.WarnDays, _host.Text);

    /// <summary>The same line on the Home tab, red while anything wants doing.</summary>
    public Glance? Glance() => Checkup.GlanceOf(_settings.LastCheckup, HasBackup, _settings.WarnDays, _host.Text);

    public string EmptyText => _host.Text["vet.empty"];

    /// <summary>Looks at everything again: drives, reboot key, backup folder. Reading only.</summary>
    public void CheckupNow()
    {
        ErrorMessage = null;

        _drives = _machine.Drives();
        _reboot = _machine.RebootNeeded();
        _backupDays = Checkup.BackupDays(_settings.BackupFolder, DateTime.UtcNow);

        var summary = Checkup.Run(_settings.BackupFolder, DateTime.UtcNow, _drives, _reboot);
        summary.BackupStale = HasBackup && _backupDays is { } days && days > _settings.WarnDays;
        _settings.LastCheckup = summary;
        Save();

        var keep = Selected?.Name;
        Drives.Clear();
        foreach (var drive in _drives)
            Drives.Add(new DriveViewModel(drive));
        Selected = Drives.FirstOrDefault(d => d.Name == keep);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(BackupAgeText));
        OnPropertyChanged(nameof(RebootText));
        Recheck();
        _host.Store.Record("checked", Summary);
        Status = _host.Text.Format("vet.status.checked", DateTime.Now.ToString("HH:mm"));
    }

    /// <summary>
    /// What wants doing is said out loud from wherever the user happens to be, and stays said
    /// until the next checkup says otherwise.
    ///
    /// One key, so a re-check replaces the entry rather than stacking another one on top, and
    /// both branches are here: a condition set and never cleared is worse than no condition.
    /// </summary>
    private void Recheck()
    {
        if (_settings.LastCheckup is not { } summary)
        {
            _host.Notifications.ClearCondition(HealthKey);
            return;
        }

        var judged = Checkup.Judge(summary, HasBackup, _settings.WarnDays, _host.Text);
        if (!judged.IsTrouble)
        {
            _host.Notifications.ClearCondition(HealthKey);
            return;
        }

        _host.Notifications.SetCondition(HealthKey, NotificationSeverity.Warning,
            _host.Text["vet.notify"], string.Join(", ", judged.Parts.Take(3)),
            new NotificationAction(_host.Text["vet.notify.recheck"], CheckupNow));
    }

    private void ClearBackup()
    {
        _settings.BackupFolder = null;
        Save();
        OnPropertyChanged(nameof(HasBackup));
        OnPropertyChanged(nameof(BackupText));
        ClearBackupCommand.RaiseCanExecuteChanged();
        CheckupNow();
        Status = _host.Text["vet.status.nobackup"];
    }

    private void Save()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not save Vet settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the drives: by name, from what is already read.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var drive in Drives)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, drive.Name))
                continue;

            var chosen = drive;
            hits.Add(new SearchHit(chosen.Name, chosen.RoomText, () => Selected = chosen));
        }

        return hits;
    }

    public void Dispose() => _language.Dispose();

    // ---- picking folders by hand, through the host's dialogs rather than a TopLevel of our own ----

    private async Task SetBackupAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["vet.dialog.backup"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;

            _settings.BackupFolder = picked;
            Save();
            OnPropertyChanged(nameof(HasBackup));
            OnPropertyChanged(nameof(BackupText));
            ClearBackupCommand.RaiseCanExecuteChanged();
            CheckupNow();
            Status = _host.Text.Format("vet.status.backup", picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    internal async Task ExportAsync()
    {
        if (_settings.LastCheckup is not { } summary)
            return;

        try
        {
            var picked = await _host.Pick.Save(new PickOptions
            {
                Title = _host.Text["vet.dialog.export"],
                SuggestedName = $"vet-{DateTime.Today:yyyy-MM-dd}.zip",
            });
            if (string.IsNullOrWhiteSpace(picked))
                return;

            Checkup.WriteDiagnostics(picked, summary, HasBackup, _settings.WarnDays, _drives, _host.Text);
            Status = _host.Text.Format("vet.status.exported", System.IO.Path.GetFileName(picked));
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("vet.error.export", ex.Message);
        }
    }

    private void Retranslate()
    {
        OnEverythingChanged();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(BackupText));
        OnPropertyChanged(nameof(BackupAgeText));
        OnPropertyChanged(nameof(RebootText));

        // The standing condition was written in the old language and nobody would rewrite it, so
        // it is raised again in the new one.
        Recheck();
    }
}
