using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Nest.Services;

namespace Meows.Plugins.Nest.ViewModels;

public sealed class NestSettings
{
    /// <summary>Where copies go: another drive, a memory stick, a share.</summary>
    public string? CopyRoot { get; set; }

    /// <summary>Known places switched off, by key.</summary>
    public List<string> Off { get; set; } = [];

    /// <summary>Folders added by hand.</summary>
    public List<string> Added { get; set; } = [];

    /// <summary>When each place was last copied from here, by key.</summary>
    public Dictionary<string, DateTime> LastCopyUtc { get; set; } = [];
}

/// <summary>One place, with what it holds and how its copy stands.</summary>
public sealed class PlaceViewModel(NestPlace place, bool isOn, Action<PlaceViewModel> switched) : ObservableObject
{
    private bool _isOn = isOn;
    private NestMeasure? _measure;
    private NestStanding? _standing;

    public NestPlace Place { get; } = place;

    public string Name => Place.IsKnown ? MeowsText.Current[Place.NameKey] : Place.NameKey;

    public string Path => Place.Path;

    public bool IsKnown => Place.IsKnown;

    public bool IsAdded => !Place.IsKnown;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetField(ref _isOn, value))
                switched(this);
        }
    }

    public NestMeasure? Measure
    {
        get => _measure;
        set
        {
            if (SetField(ref _measure, value))
                OnPropertyChanged(nameof(SizeText));
        }
    }

    public NestStanding? Standing
    {
        get => _standing;
        set
        {
            if (!SetField(ref _standing, value))
                return;
            OnPropertyChanged(nameof(StandingText));
            OnPropertyChanged(nameof(NeedsCopy));
        }
    }

    /// <summary>When this place was last copied from here, if it ever was.</summary>
    public DateTime? LastCopyUtc { get; set; }

    public string SizeText => Measure is { } m
        ? MeowsText.Current.Format("nest.place.size", FolderSize.Humanise(m.Bytes), m.Files)
        : MeowsText.Current["nest.place.measuring"];

    public bool NeedsCopy => Standing is { UpToDate: false } && Measure is { Files: > 0 };

    public string StandingText
    {
        get
        {
            if (Standing is not { } standing)
                return MeowsText.Current["nest.standing.nocopyroot"];
            var when = LastCopyUtc ?? standing.CopyNewestUtc;
            if (!standing.HasCopy)
                return MeowsText.Current["nest.standing.never"];
            var ago = when is { } w ? NestViewModel.Ago(w.ToLocalTime(), DateTime.Now) : "";
            return standing.Changed == 0
                ? MeowsText.Current.Format("nest.standing.uptodate", ago)
                : MeowsText.Current.Format("nest.standing.changed", standing.Changed, FolderSize.Humanise(standing.ChangedBytes), ago);
        }
    }

    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// Nest: the few things on this machine that cannot be downloaded again, how much there is, and
/// how long since each was copied somewhere else. The inverse of every other plugin here: they
/// answer what can go, this answers what must never go, and the answer usually fits on a stick.
///
/// The known places are a starting point, never the authority, and the tab says so. Copying is
/// one way and additive: new and changed files go into the copy, checked, and nothing in the copy
/// is ever deleted.
/// </summary>
public sealed class NestViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable
{
    /// <summary>After this long with changes not copied, Home says so in red.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromDays(30);

    private readonly IMeowsHost _host;
    private readonly Func<IReadOnlyList<NestPlace>> _known;
    private readonly NestSettings _settings;
    private readonly LanguageWatch _language;
    private IBackgroundTask? _work;
    private bool _isWorking;
    private string? _status;
    private string? _errorMessage;

    /// <summary>What the last copy did, kept on the status line once the measure after it is done.</summary>
    private string? _lastOutcome;

    public NestViewModel(IMeowsHost host) : this(host, NestPlaces.Known)
    {
    }

    /// <summary>With the known places supplied from elsewhere, which is how a test points it at a folder.</summary>
    public NestViewModel(IMeowsHost host, Func<IReadOnlyList<NestPlace>> known)
    {
        _host = host;
        _known = known;
        _settings = host.LoadSettings<NestSettings>() ?? new NestSettings();

        MeasureCommand = new RelayCommand(Measure, () => !IsWorking);
        CopyCommand = new RelayCommand(Copy, () => !IsWorking && HasCopyRoot && Places.Any(p => p.IsOn && p.Measure is { Files: > 0 }));
        PickCopyRootCommand = new RelayCommand(() => _ = PickCopyRootAsync(), () => !IsWorking);
        AddPlaceCommand = new RelayCommand(() => _ = AddPlaceAsync(), () => !IsWorking);
        RemovePlaceCommand = new RelayCommand(p => RemovePlace(p as PlaceViewModel), p => !IsWorking && p is PlaceViewModel { IsAdded: true });
        OpenCommand = new RelayCommand(p => Open((p as PlaceViewModel)?.Path));
        StopCommand = new RelayCommand(() => _work?.Cancel(), () => IsWorking);

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var place in Places)
                place.Reread();
        });

        Rebuild();
        Measure();
    }

    public ObservableCollection<PlaceViewModel> Places { get; } = [];

    public RelayCommand MeasureCommand { get; }

    public RelayCommand CopyCommand { get; }

    public RelayCommand PickCopyRootCommand { get; }

    public RelayCommand AddPlaceCommand { get; }

    public RelayCommand RemovePlaceCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand StopCommand { get; }

    public string? CopyRoot => _settings.CopyRoot;

    public bool HasCopyRoot => !string.IsNullOrWhiteSpace(_settings.CopyRoot);

    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (!SetField(ref _isWorking, value))
                return;
            MeasureCommand.RaiseCanExecuteChanged();
            CopyCommand.RaiseCanExecuteChanged();
            PickCopyRootCommand.RaiseCanExecuteChanged();
            AddPlaceCommand.RaiseCanExecuteChanged();
            RemovePlaceCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["nest.status.ready"];
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

    /// <summary>The number the tab exists for: how much cannot be downloaded again.</summary>
    public string Total
    {
        get
        {
            var measured = Places.Where(p => p.IsOn && p.Measure is not null).ToList();
            if (measured.Count == 0)
                return _host.Text["nest.total.none"];
            return _host.Text.Format("nest.total", FolderSize.Humanise(measured.Sum(p => p.Measure!.Bytes)),
                measured.Sum(p => p.Measure!.Files), measured.Count);
        }
    }

    /// <summary>Known places that exist here, then the ones added by hand.</summary>
    private void Rebuild()
    {
        Places.Clear();
        foreach (var place in _known().Where(p => Directory.Exists(p.Path)))
            Places.Add(Row(place, !_settings.Off.Contains(place.Key)));
        foreach (var path in _settings.Added)
            Places.Add(Row(NestPlaces.Added(path), true));
        OnPropertyChanged(nameof(Total));
        CopyCommand.RaiseCanExecuteChanged();
    }

    private PlaceViewModel Row(NestPlace place, bool on) =>
        new(place, on, Switched) { LastCopyUtc = _settings.LastCopyUtc.TryGetValue(place.Key, out var when) ? when : null };

    private void Switched(PlaceViewModel place)
    {
        if (place.IsOn)
            _settings.Off.Remove(place.Place.Key);
        else if (!_settings.Off.Contains(place.Place.Key))
            _settings.Off.Add(place.Place.Key);
        SaveSettings();
        OnPropertyChanged(nameof(Total));
        CopyCommand.RaiseCanExecuteChanged();
    }

    /// <summary>What each place holds and how its copy stands, off the UI thread.</summary>
    private void Measure()
    {
        if (IsWorking)
            return;
        IsWorking = true;
        Status = _host.Text["nest.status.measuring"];
        var places = Places.ToList();
        var copyRoot = _settings.CopyRoot;

        _work = _host.Background.Run(_host.Text["nest.task.measure"], async context =>
        {
            var results = await Task.Run(() => MeasureAll(places.Select(p => p.Place).ToList(), copyRoot, context.Token), context.Token);
            await Dispatcher.UIThread.InvokeAsync(() => Measured(results));
        });
    }

    public static IReadOnlyList<(NestMeasure Measure, NestStanding? Standing)> MeasureAll(IReadOnlyList<NestPlace> places, string? copyRoot, CancellationToken token) =>
        places.Select(p => (NestPlaces.Measure(p.Path, token),
                string.IsNullOrWhiteSpace(copyRoot) ? null : NestPlaces.Standing(p.Path, NestPlaces.CopyOf(p, copyRoot), token)))
            .ToList();

    /// <summary>Puts a finished measure on screen. Separate so a test can run it itself.</summary>
    public void Measured(IReadOnlyList<(NestMeasure Measure, NestStanding? Standing)> results)
    {
        IsWorking = false;
        for (var i = 0; i < Math.Min(results.Count, Places.Count); i++)
        {
            Places[i].Measure = results[i].Measure;
            Places[i].Standing = results[i].Standing;
        }
        Status = _lastOutcome ?? _host.Text["nest.status.ready"];
        OnPropertyChanged(nameof(Total));
        CopyCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Everything switched on, into the copy folder, new and changed files only.</summary>
    private void Copy()
    {
        if (IsWorking || !HasCopyRoot)
            return;
        var copyRoot = _settings.CopyRoot!;
        var places = Places.Where(p => p.IsOn && p.Measure is { Files: > 0 }).Select(p => p.Place).ToList();
        IsWorking = true;
        ErrorMessage = null;
        Status = _host.Text["nest.status.copying"];

        _work = _host.Background.Run(_host.Text["nest.task.copy"], async context =>
        {
            var progress = new Progress<string>(file => Status = _host.Text.Format("nest.status.file", file));
            var outcomes = await Task.Run(() => CopyAll(places, copyRoot, progress, context.Token), context.Token);
            await Dispatcher.UIThread.InvokeAsync(() => Copied(places, outcomes));
        });
    }

    public static IReadOnlyList<NestCopyOutcome> CopyAll(IReadOnlyList<NestPlace> places, string copyRoot, IProgress<string>? progress, CancellationToken token) =>
        places.Select(p => NestPlaces.CopyChanged(p.Path, NestPlaces.CopyOf(p, copyRoot), progress, token)).ToList();

    /// <summary>After a copy: dates kept, the history told, and everything measured again.</summary>
    public void Copied(IReadOnlyList<NestPlace> places, IReadOnlyList<NestCopyOutcome> outcomes)
    {
        IsWorking = false;
        var now = DateTime.UtcNow;
        for (var i = 0; i < places.Count; i++)
        {
            if (outcomes[i].Failed.Count == 0)
                _settings.LastCopyUtc[places[i].Key] = now;
        }
        SaveSettings();

        var copied = outcomes.Sum(o => o.Copied);
        var bytes = outcomes.Sum(o => o.Bytes);
        var failed = outcomes.SelectMany(o => o.Failed).ToList();
        _host.Store.Record("copied", _settings.CopyRoot ?? "", _host.Text.Format("nest.journal", copied, FolderSize.Humanise(bytes)),
            new Dictionary<string, string> { ["files"] = copied.ToString(), ["bytes"] = bytes.ToString(), ["failed"] = failed.Count.ToString() });
        _host.Log($"Nest copied {copied} file(s), {bytes} bytes, to {_settings.CopyRoot}; {failed.Count} could not be.");

        if (failed.Count > 0)
            ErrorMessage = _host.Text.Format("nest.copy.failed", failed.Count, string.Join(", ", failed.Take(3)));
        _lastOutcome = _host.Text.Format("nest.copy.done", copied, FolderSize.Humanise(bytes));
        Rebuild();
        Measure();
    }

    private async Task PickCopyRootAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["nest.dialog.copyroot"] });
            if (!string.IsNullOrWhiteSpace(picked))
                SetCopyRoot(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    /// <summary>
    /// Where copies go. Refused when it is inside one of the places, or on the same drive as all
    /// of them: a copy that dies with the drive it is copying is not a copy.
    /// </summary>
    public void SetCopyRoot(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (Places.Any(p => full.StartsWith(p.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(full, p.Path, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = _host.Text["nest.copyroot.inside"];
            return;
        }
        ErrorMessage = SameDrive(full) ? _host.Text["nest.copyroot.samedrive"] : null;
        _settings.CopyRoot = full;
        SaveSettings();
        OnPropertyChanged(nameof(CopyRoot));
        OnPropertyChanged(nameof(HasCopyRoot));
        CopyCommand.RaiseCanExecuteChanged();
        Measure();
    }

    /// <summary>A warning, not a refusal: the same drive still guards against a deleted file, just not against the drive.</summary>
    private bool SameDrive(string folder)
    {
        var root = Path.GetPathRoot(folder);
        return OperatingSystem.IsWindows() && root is not null && Places.Count > 0 &&
               Places.All(p => string.Equals(Path.GetPathRoot(p.Path), root, StringComparison.OrdinalIgnoreCase));
    }

    private async Task AddPlaceAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["nest.dialog.place"] });
            if (!string.IsNullOrWhiteSpace(picked))
                AddPlace(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    public void AddPlace(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (_settings.Added.Contains(full, StringComparer.OrdinalIgnoreCase) ||
            Places.Any(p => string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase)))
            return;
        _settings.Added.Add(full);
        SaveSettings();
        Rebuild();
        Measure();
    }

    private void RemovePlace(PlaceViewModel? place)
    {
        if (place is not { IsAdded: true })
            return;
        _settings.Added.RemoveAll(a => string.Equals(a, place.Path, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        Rebuild();
        Measure();
    }

    private void Open(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path))
                Explorer.Open(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public static string Ago(DateTime when, DateTime now)
    {
        var days = (now - when).TotalDays;
        return days switch
        {
            < 1 => MeowsText.Current["nest.ago.today"],
            < 2 => MeowsText.Current["nest.ago.yesterday"],
            < 60 => MeowsText.Current.Format("nest.ago.days", (int)days),
            < 730 => MeowsText.Current.Format("nest.ago.months", (int)(days / 30.44)),
            _ => MeowsText.Current.Format("nest.ago.years", (int)(days / 365.25)),
        };
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        return Places
            .Where(p => SearchWords.Match(words, p.Name, p.Path))
            .Take(limit)
            .Select(p => new SearchHit(p.Name, $"{p.SizeText} · {p.StandingText}", () => Open(p.Path)))
            .ToList();
    }

    /// <summary>
    /// Home's line. With no copy folder it is a fact: this much, never copied. With one, it is
    /// trouble once changes have gone uncopied for a month.
    /// </summary>
    public Glance? Glance()
    {
        var on = Places.Where(p => p.IsOn && p.Measure is { Files: > 0 }).ToList();
        if (on.Count == 0)
            return null;
        var bytes = FolderSize.Humanise(on.Sum(p => p.Measure!.Bytes));
        if (!HasCopyRoot)
            return new Glance(_host.Text.Format("nest.glance.nocopy", bytes));

        var waiting = on.Where(p => p.NeedsCopy).ToList();
        if (waiting.Count == 0)
            return new Glance(_host.Text.Format("nest.glance.fine", bytes));
        var oldest = waiting.Min(p => p.LastCopyUtc ?? DateTime.MinValue);
        var stale = DateTime.UtcNow - oldest > Stale;
        return new Glance(_host.Text.Format("nest.glance.waiting", waiting.Count, bytes), IsTrouble: stale);
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Nest settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _language.Dispose();
        _work?.Cancel();
    }
}
