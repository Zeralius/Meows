using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.WeighIn.Services;

namespace Meows.Plugins.WeighIn.ViewModels;

public sealed class WeighInSettings
{
    /// <summary>Drive roots to read, like C:\. Empty means every ready fixed drive.</summary>
    public List<string> Drives { get; set; } = [];

    /// <summary>How many folder levels under the drive a reading keeps. Two is a few thousand numbers on a full drive.</summary>
    public int Depth { get; set; } = 2;

    /// <summary>Hours between readings. Once a day is the point; a drive does not change by the hour.</summary>
    public int EveryHours { get; set; } = 24;

    /// <summary>How many daily readings to keep. Sixty is two months of history in a few megabytes.</summary>
    public int KeepReadings { get; set; } = 60;

    /// <summary>Movement smaller than this is noise, in megabytes.</summary>
    public int ThresholdMegabytes { get; set; } = 100;

    public bool SkipSystemFolders { get; set; } = true;

    /// <summary>Say so on the notification surface when a drive is under this many percent free and shrinking.</summary>
    public int WarnBelowPercentFree { get; set; } = 10;
}

/// <summary>How far back the comparison looks.</summary>
public sealed record Window(int Days, string Key)
{
    public override string ToString() => MeowsText.Current[Key];
}

/// <summary>One drive in the left column: what it holds now and what changed over the window.</summary>
public sealed class DriveViewModel(DriveReading now, DriveReading? before, DateTime? beforeAt) : ObservableObject
{
    public DriveReading Now { get; } = now;

    public DriveReading? Before { get; } = before;

    public string Root => Now.Root;

    public string UsedText => MeowsText.Current.Format("weighin.drive.used", Readings.Humanise(Now.Used), Readings.Humanise(Now.Total));

    public double Fraction => Now.Total <= 0 ? 0 : (double)Now.Used / Now.Total;

    public int PercentFree => Now.Total <= 0 ? 0 : (int)Math.Round(100.0 * Now.Free / Now.Total);

    public long Delta => Before is null ? 0 : Now.Used - Before.Used;

    public bool HasBefore => Before is not null;

    public bool Grew => Delta > 0;

    public bool Shrank => Delta < 0;

    public string DeltaText => Before is null
        ? MeowsText.Current["weighin.drive.nobefore"]
        : MeowsText.Current.Format("weighin.drive.delta", Readings.Signed(Delta), beforeAt?.ToString("d MMM") ?? "");

    public void Reread() => OnEverythingChanged();
}

/// <summary>One folder that moved, in the middle column.</summary>
public sealed class GrowthViewModel(Growth growth, long driveDelta) : ObservableObject
{
    public Growth Growth { get; } = growth;

    public string Path => Growth.Path;

    public string Name => System.IO.Path.GetFileName(Growth.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : Growth.Path;

    public string Parent => System.IO.Path.GetDirectoryName(Growth.Path) ?? "";

    public string DeltaText => Readings.Signed(Growth.Delta);

    public string SizeText => Growth.IsGone
        ? MeowsText.Current["weighin.growth.gone"]
        : Growth.IsNew
            ? MeowsText.Current.Format("weighin.growth.new", Readings.Humanise(Growth.After))
            : MeowsText.Current.Format("weighin.growth.was", Readings.Humanise(Growth.Before), Readings.Humanise(Growth.After));

    public bool Grew => Growth.Delta > 0;

    /// <summary>This folder's share of the drive's whole movement, as a bar the eye can compare.</summary>
    public double Fraction => driveDelta == 0 ? 0 : Math.Clamp((double)Growth.Delta / driveDelta, 0, 1);

    public void Reread() => OnEverythingChanged();
}

/// <summary>
/// Chonk answers where the room went, right now. This answers what grew, which is the more
/// useful question, because nobody notices a drive filling until it is full. One reading of
/// every drive a day, kept to a fixed depth, and the tab says what moved between then and now.
/// </summary>
public sealed class WeighInViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable
{
    private readonly IMeowsHost _host;
    private readonly WeighInSettings _settings;
    private readonly LanguageWatch _language;
    private readonly string _folder;
    private IBackgroundTask? _schedule;
    private IBackgroundTask? _reading;

    private IReadOnlyList<Reading> _readings = [];
    private DriveViewModel? _selectedDrive;
    private GrowthViewModel? _selectedGrowth;
    private Window _window;
    private string? _status;
    private string? _errorMessage;
    private bool _isReading;

    public static readonly IReadOnlyList<Window> Windows =
    [
        new(1, "weighin.window.day"),
        new(7, "weighin.window.week"),
        new(30, "weighin.window.month"),
        new(90, "weighin.window.quarter"),
    ];

    public WeighInViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<WeighInSettings>() ?? new WeighInSettings();
        _folder = Path.Combine(host.DataDirectory, "readings");
        _window = Windows[1];

        ReadNowCommand = new RelayCommand(() => TakeReading(byHand: true), () => !IsReading);
        CancelCommand = new RelayCommand(() => _reading?.Cancel(), () => IsReading);
        MeasureInChonkCommand = new RelayCommand(MeasureInChonk, () => SelectedGrowth is not null && CanReachChonk);
        ExploreCommand = new RelayCommand(() => Open(SelectedGrowth?.Path), () => SelectedGrowth is not null);

        Reload();

        // The reading itself, on the shell's clock. It waits its interval first: the tab
        // opening is not a reason to walk six drives, and a reading from earlier today is on
        // disk already if there was one.
        _schedule = _host.Background.Schedule(_host.Text["weighin.task.daily"], TimeSpan.FromHours(Math.Max(1, _settings.EveryHours)),
            context => RunReading(context, byHand: false), runImmediately: false);

        // Unless there has never been one, in which case the first is now.
        if (_readings.Count == 0)
            TakeReading(byHand: false);

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var d in Drives) d.Reread();
            foreach (var g in Growth) g.Reread();
        });
    }

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public ObservableCollection<GrowthViewModel> Growth { get; } = [];

    public IReadOnlyList<Window> WindowChoices => Windows;

    public RelayCommand ReadNowCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand MeasureInChonkCommand { get; }

    public RelayCommand ExploreCommand { get; }

    public bool CanReachChonk => _host.Handoff.CanReach(KnownPlugins.Chonk);

    public Window Window
    {
        get => _window;
        set
        {
            if (SetField(ref _window, value ?? Windows[1]))
                Rebuild();
        }
    }

    public DriveViewModel? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (!SetField(ref _selectedDrive, value))
                return;
            OnPropertyChanged(nameof(HasDrive));
            OnPropertyChanged(nameof(DriveHeadline));
            RebuildGrowth();
        }
    }

    public bool HasDrive => _selectedDrive is not null;

    public GrowthViewModel? SelectedGrowth
    {
        get => _selectedGrowth;
        set
        {
            if (!SetField(ref _selectedGrowth, value))
                return;
            OnPropertyChanged(nameof(HasGrowth));
            MeasureInChonkCommand.RaiseCanExecuteChanged();
            ExploreCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasGrowth => _selectedGrowth is not null;

    public bool IsReading
    {
        get => _isReading;
        private set
        {
            if (!SetField(ref _isReading, value))
                return;
            ReadNowCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => Drives.Count == 0;

    public int ReadingsKept => _readings.Count;

    /// <summary>The headline: when the last reading was, and how many there are to compare against.</summary>
    public string SummaryText
    {
        get
        {
            var text = _host.Text;
            if (_readings.Count == 0)
                return text["weighin.summary.none"];
            var last = _readings[^1];
            return text.Format("weighin.summary", last.At.ToString("d MMM HH:mm"), _readings.Count, _readings[0].At.ToString("d MMM"));
        }
    }

    /// <summary>
    /// On the Home tab: the selected drive's headline when there is one to tell, else when the
    /// last reading was. Nothing here is trouble; a full drive is Chonk's word to say.
    /// </summary>
    public Glance? Glance() => new(DriveHeadline.Length > 0 ? DriveHeadline : SummaryText);

    /// <summary>The selected drive over the window: "F lost 200 GB since 7 Sep; the three folders responsible".</summary>
    public string DriveHeadline
    {
        get
        {
            if (SelectedDrive is not { } drive)
                return "";
            var text = _host.Text;
            if (!drive.HasBefore)
                return text.Format("weighin.headline.nobefore", drive.Root, Window.ToString());
            if (drive.Delta == 0)
                return text.Format("weighin.headline.same", drive.Root, Window.ToString());
            return text.Format(drive.Grew ? "weighin.headline.grew" : "weighin.headline.shrank",
                drive.Root, Readings.Humanise(drive.Delta), Window.ToString(), drive.PercentFree);
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["weighin.status.start"];
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

    public int ThresholdMegabytes
    {
        get => _settings.ThresholdMegabytes;
        set
        {
            if (value <= 0 || _settings.ThresholdMegabytes == value)
                return;
            _settings.ThresholdMegabytes = value;
            SaveSettings();
            OnPropertyChanged();
            RebuildGrowth();
        }
    }

    public int Depth
    {
        get => _settings.Depth;
        set
        {
            if (value is < 1 or > 4 || _settings.Depth == value)
                return;
            _settings.Depth = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    // ---- readings ----

    /// <summary>The drives to read: what the settings say, or every ready fixed drive.</summary>
    private IReadOnlyList<string> Roots()
    {
        if (_settings.Drives.Count > 0)
            return _settings.Drives;
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void TakeReading(bool byHand)
    {
        if (IsReading)
            return;
        IsReading = true;
        Status = _host.Text["weighin.status.reading"];
        _reading = _host.Background.Run(_host.Text["weighin.task.reading"], context => RunReading(context, byHand));
    }

    /// <summary>One reading, off the UI thread, saved, pruned, journaled, and the tab rebuilt.</summary>
    private async Task RunReading(IBackgroundContext context, bool byHand)
    {
        await Dispatcher.UIThread.InvokeAsync(() => IsReading = true);
        try
        {
            var roots = Roots();
            var depth = _settings.Depth;
            var skip = _settings.SkipSystemFolders;
            var reading = await Task.Run(
                () => Readings.Take(roots, depth, skip, root => context.Report(_host.Text.Format("weighin.progress", root)), context.Token),
                context.Token);

            Readings.Save(_folder, reading);
            Readings.Prune(_folder, Math.Max(2, _settings.KeepReadings));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Reload();
                Journal(reading);
                Warn(reading);
                Status = _host.Text.Format("weighin.status.read", reading.Drives.Count, reading.At.ToString("HH:mm"));
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = _host.Text["weighin.status.cancelled"]);
            if (byHand)
                return;
            throw;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _host.Text.Format("weighin.error.read", ex.Message));
            throw;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsReading = false);
        }
    }

    /// <summary>One line per drive in the history: what it holds and what moved since the last reading.</summary>
    private void Journal(Reading reading)
    {
        var previous = _readings.Count >= 2 ? _readings[^2] : null;
        foreach (var drive in reading.Drives)
        {
            var before = previous?.Drive(drive.Root);
            var detail = before is null
                ? _host.Text.Format("weighin.journal.first", Readings.Humanise(drive.Used), Readings.Humanise(drive.Total))
                : _host.Text.Format("weighin.journal.reading", Readings.Humanise(drive.Used), Readings.Signed(drive.Used - before.Used));
            _host.Store.Record("reading", drive.Root, detail, new Dictionary<string, string>
            {
                ["used"] = drive.Used.ToString(),
                ["free"] = drive.Free.ToString(),
                ["total"] = drive.Total.ToString(),
            });
        }
    }

    /// <summary>A drive that is nearly full and still filling is a standing condition, cleared the day it is not.</summary>
    private void Warn(Reading reading)
    {
        var weekAgo = Readings.Before(_readings, reading.At.AddDays(-7)) ?? (_readings.Count >= 2 ? _readings[^2] : null);
        var filling = new List<string>();

        foreach (var drive in reading.Drives.Where(d => d.Total > 0))
        {
            var percentFree = 100.0 * drive.Free / drive.Total;
            var before = weekAgo?.Drive(drive.Root);
            var shrinking = before is not null && drive.Free < before.Free;
            if (percentFree < _settings.WarnBelowPercentFree && shrinking)
                filling.Add(_host.Text.Format("weighin.warn.line", drive.Root, Readings.Humanise(drive.Free), Readings.Humanise(before!.Free - drive.Free)));
        }

        if (filling.Count == 0)
        {
            _host.Notifications.ClearCondition("filling");
            return;
        }

        _host.Notifications.SetCondition("filling", NotificationSeverity.Warning,
            filling.Count == 1 ? _host.Text["weighin.warn.one"] : _host.Text.Format("weighin.warn.many", filling.Count),
            string.Join("\n", filling));
    }

    // ---- the tab ----

    private void Reload()
    {
        _readings = Readings.Load(_folder);
        Rebuild();
    }

    private void Rebuild()
    {
        var keep = SelectedDrive?.Root;
        Drives.Clear();

        if (_readings.Count > 0)
        {
            var now = _readings[^1];
            var before = Readings.Before(_readings, now.At.AddDays(-Window.Days).AddHours(12));
            // No reading that old: the oldest one is the best there is, and the card says which date.
            if (before is null || ReferenceEquals(before, now))
                before = _readings.Count >= 2 ? _readings[0] : null;

            foreach (var drive in now.Drives)
                Drives.Add(new DriveViewModel(drive, before?.Drive(drive.Root), before?.At));
        }

        SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Root, keep, StringComparison.OrdinalIgnoreCase)) ?? Drives.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ReadingsKept));
        RebuildGrowth();
    }

    private void RebuildGrowth()
    {
        Growth.Clear();
        SelectedGrowth = null;
        if (SelectedDrive is { } drive)
        {
            var threshold = _settings.ThresholdMegabytes * 1_000_000L;
            var moved = Readings.Compare(drive.Before, drive.Now, threshold);
            foreach (var growth in Readings.Responsible(moved, 40))
                Growth.Add(new GrowthViewModel(growth, drive.Delta == 0 ? growth.Delta : drive.Delta));
        }
        OnPropertyChanged(nameof(DriveHeadline));
        OnPropertyChanged(nameof(HasGrowthRows));
    }

    public bool HasGrowthRows => Growth.Count > 0;

    private void MeasureInChonk()
    {
        if (SelectedGrowth is not { } growth || growth.Growth.IsGone)
            return;
        if (!_host.Handoff.Send(KnownPlugins.Chonk, Handoff.Folder(growth.Path)))
            ErrorMessage = _host.Text["weighin.error.chonk"];
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
            ErrorMessage = _host.Text.Format("weighin.error.open", path, ex.Message);
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
            _host.Log(LogLevel.Warning, $"Could not save Weigh-In settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the growth list: a folder that moved, by name.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();
        foreach (var growth in Growth)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, growth.Path))
                continue;
            var chosen = growth;
            hits.Add(new SearchHit(growth.Name, $"{growth.DeltaText} · {growth.Parent}", () => SelectedGrowth = chosen));
        }
        return hits;
    }

    public void Dispose()
    {
        _schedule?.Cancel();
        _reading?.Cancel();
        _language.Dispose();
    }
}
