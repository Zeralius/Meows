using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Meows.Bot;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Perch.Services;

namespace Meows.Plugins.Perch.ViewModels;

public sealed class PerchSettings
{
    public string? BotRoot { get; set; }

    /// <summary>When the bot was started, if known. Null means "as if it started now".</summary>
    public DateTime? BotStartedAt { get; set; }

    public int HorizonHours { get; set; } = 48;
}

/// <summary>One choice in a dropdown, named by the language the window is in.</summary>
public sealed class HorizonOption(int hours, string key)
{
    public int Hours { get; } = hours;

    public TranslatedString Label { get; } = MeowsText.Entry(key);
}

/// <summary>One line of the timeline.</summary>
public sealed class SlotViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _isSelected;

    public SlotViewModel(Slot slot) => Slot = slot;

    public Slot Slot { get; }

    public string GroupName => Slot.Group.Name;

    public string TimeText => Slot.At.ToString("HH:mm");

    public string JitterText => Slot.JitterMinutes > 0 ? $"+0–{Slot.JitterMinutes} min" : "";

    public bool HasJitter => Slot.JitterMinutes > 0;

    public bool IsPost => Slot.Kind == SlotKind.Post;

    public bool IsRandom => Slot.Kind == SlotKind.Random;

    public bool IsRunsDry => Slot.Kind == SlotKind.RunsDry;

    public bool IsNothing => Slot.Kind == SlotKind.Nothing;

    public bool IsStretched => Slot.Stretched;

    public bool IsComic => Slot.Files.Count > 0 && MediaRules.IsComic(Slot.Files[0]);

    public string? FirstFile => Slot.Files.Count > 0 ? Slot.Files[0] : null;

    /// <summary>What goes: the names, or a word for why there are none.</summary>
    public string WhatText => Slot.Kind switch
    {
        SlotKind.Post => string.Join(", ", Slot.Files.Select(Path.GetFileName)),
        SlotKind.Random => MeowsText.Current["perch.slot.random"],
        SlotKind.RunsDry => MeowsText.Current["perch.slot.dry"],
        _ => MeowsText.Current["perch.slot.nothing"],
    };

    /// <summary>The comic's page count, since a long one goes out as several batches.</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Slot.Kind == SlotKind.Post && Slot.Files.Count > 1)
                parts.Add(MeowsText.Current.Format("perch.slot.files", Slot.Files.Count));
            if (IsComic && Pages is { } pages)
            {
                var batches = (int)Math.Ceiling(pages / (double)MediaRules.MediaGroupLimit);
                parts.Add(batches > 1
                    ? MeowsText.Current.Format("perch.slot.comic.batches", pages, batches)
                    : MeowsText.Current.Format("perch.slot.comic", pages));
            }
            if (Slot.Stretched && Slot.IntervalMinutes is { } effective)
                parts.Add(MeowsText.Current.Format("perch.slot.stretched", effective, Slot.Group.Schedule?.IntervalMinutes ?? 0));
            if (Slot.Kind == SlotKind.Post)
                parts.Add(MeowsText.Current.Format("perch.slot.left", Slot.QueuedAfter));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Filled in off the UI thread with the thumbnail, since opening the archive costs.</summary>
    public int? Pages { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            var old = _thumbnail;
            if (!SetField(ref _thumbnail, value))
                return;
            OnPropertyChanged(nameof(HasThumbnail));
            old?.Dispose();
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public bool Asked { get; set; }

    internal void Reread() => OnEverythingChanged();

    public void Dispose() => Thumbnail = null;
}

/// <summary>One day of the timeline, so the list has a heading per day rather than a date on every row.</summary>
public sealed class DayViewModel(DateTime day, DateTime today)
{
    public DateTime Day { get; } = day;

    public string Label
    {
        get
        {
            var text = MeowsText.Current;
            if (Day == today) return text["perch.day.today"];
            if (Day == today.AddDays(1)) return text["perch.day.tomorrow"];
            return Day.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
        }
    }

    public ObservableCollection<SlotViewModel> Slots { get; } = [];
}

/// <summary>One group in the left column: its queue and where it runs out.</summary>
public sealed class GroupSummaryViewModel(GroupConfig group, int queued, DateTime? runsDryAt, bool stretched)
{
    public string Name { get; } = group.Name;

    public int Queued { get; } = queued;

    public bool IsDisabled { get; } = group.Enabled == false;

    public string ScheduleText
    {
        get
        {
            var text = MeowsText.Current;
            if (group.Schedule?.IntervalMinutes is { } i && i > 0)
                return text.Format("perch.group.every", i);
            return text.Format("perch.group.daily", $"{group.Schedule?.Hour ?? Forecast.DefaultHour:00}:{group.Schedule?.Minute ?? 0:00}");
        }
    }

    public string RunwayText { get; } = QueueRunway.Describe(QueueRunway.Days(group, queued));

    public string DryText => runsDryAt is { } when
        ? MeowsText.Current.Format("perch.group.dry", when.ToString("ddd HH:mm"))
        : "";

    public bool RunsDryInView => runsDryAt is not null;

    public bool IsStretched { get; } = stretched;

    public bool IsLow { get; } = QueueRunway.Days(group, queued) is { } days && days < QueueRunway.LowDays;
}

public sealed class PerchViewModel : ObservableObject, IDisposable, ISearchable
{
    private const int ThumbnailWidth = 56;
    private const int PreviewWidth = 720;

    private readonly IMeowsHost _host;
    private readonly PerchSettings _settings;
    private readonly LanguageWatch _language;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _thumbnails;
    private BotWorkspace? _workspace;

    private readonly List<SlotViewModel> _all = [];
    private SlotViewModel? _selected;
    private Bitmap? _preview;
    private bool _isBusy;
    private string? _status;
    private string? _errorMessage;
    private string _startText = "";

    public PerchViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<PerchSettings>() ?? new PerchSettings();
        _startText = _settings.BotStartedAt is { } started ? started.ToString("yyyy-MM-dd HH:mm") : "";

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        StartNowCommand = new RelayCommand(() => { StartText = ""; _ = RefreshAsync(); });
        SelectCommand = new RelayCommand(p => Selected = p as SlotViewModel);

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var slot in _all)
                slot.Reread();
            RebuildDays();
        });

        _ = RefreshAsync();
    }

    public ObservableCollection<DayViewModel> Days { get; } = [];

    public ObservableCollection<GroupSummaryViewModel> Groups { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand StartNowCommand { get; }

    public RelayCommand SelectCommand { get; }

    public string BotRootText => _workspace?.Root ?? MeowsText.Current["perch.nobot"];

    public bool HasWorkspace => _workspace?.LooksValid == true;

    /// <summary>
    /// When the bot was started, as typed. Interval groups fire on a clock that starts with
    /// the bot, which this machine cannot see, so the honest thing is to ask. Empty means
    /// "as if it started now", which is right whenever the bot is about to be restarted.
    /// </summary>
    public string StartText
    {
        get => _startText;
        set
        {
            if (!SetField(ref _startText, value ?? ""))
                return;
            _settings.BotStartedAt = ParseStart(_startText);
            Save();
            OnPropertyChanged(nameof(StartIsUnreadable));
        }
    }

    public bool StartIsUnreadable => _startText.Trim().Length > 0 && ParseStart(_startText) is null;

    public static DateTime? ParseStart(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;

        string[] formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "dd.MM.yyyy HH:mm", "dd.MM.yyyy H:mm", "HH:mm", "H:mm"];
        if (!DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed) ? parsed : null;

        // A bare time means today, or yesterday if that time has not come round yet.
        if (parsed.Date == DateTime.Today && trimmed.Length <= 5 && parsed > DateTime.Now)
            parsed = parsed.AddDays(-1);
        return parsed;
    }

    public IReadOnlyList<HorizonOption> Horizons { get; } =
    [
        new(24, "perch.horizon.24"),
        new(48, "perch.horizon.48"),
        new(168, "perch.horizon.168"),
    ];

    public HorizonOption SelectedHorizon
    {
        get => Horizons.FirstOrDefault(h => h.Hours == _settings.HorizonHours) ?? Horizons[1];
        set
        {
            if (value is null || _settings.HorizonHours == value.Hours)
                return;
            _settings.HorizonHours = value.Hours;
            Save();
            OnPropertyChanged();
            _ = RefreshAsync();
        }
    }

    public SlotViewModel? Selected
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (!SetField(ref _selected, value))
                return;
            if (previous is not null)
                previous.IsSelected = false;
            if (value is not null)
                value.IsSelected = true;
            OnPropertyChanged(nameof(HasSelection));
            _ = ShowPreviewAsync(value);
        }
    }

    public bool HasSelection => _selected is not null;

    public Bitmap? Preview
    {
        get => _preview;
        private set
        {
            var old = _preview;
            if (!SetField(ref _preview, value))
                return;
            OnPropertyChanged(nameof(HasPreview));
            old?.Dispose();
        }
    }

    public bool HasPreview => _preview is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => Days.Count == 0;

    public string Status
    {
        get => _status ?? MeowsText.Current["perch.status.start"];
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

    public void SetBotRoot(string root)
    {
        _settings.BotRoot = root;
        BotLocation.Remember(root);
        Save();
        _ = RefreshAsync();
    }

    /// <summary>
    /// Reads every queue and runs the rules forward. The reading is the slow part on a bot
    /// with a dozen groups of a few hundred files, so it happens off the UI thread.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ErrorMessage = null;
        Status = _host.Text["perch.status.reading"];

        var root = BotLocation.Resolve(_settings.BotRoot);
        _workspace = root is null ? null : new BotWorkspace(root);
        OnPropertyChanged(nameof(BotRootText));
        OnPropertyChanged(nameof(HasWorkspace));

        if (_workspace is not { LooksValid: true } workspace)
        {
            ErrorMessage = _host.Text["perch.error.nobot"];
            Clear();
            IsBusy = false;
            return;
        }

        var now = DateTime.Now;
        var start = _settings.BotStartedAt ?? now;
        var horizon = TimeSpan.FromHours(_settings.HorizonHours);
        var token = _closing.Token;

        try
        {
            var (slots, summaries) = await Task.Run(() =>
            {
                var config = workspace.LoadConfig();
                var queues = new List<GroupQueue>();
                foreach (var group in config.Groups)
                {
                    var files = workspace.Scan(workspace.ToSendFolder(group))
                        .Select(p => (Path: p, Modified: SafeWriteTime(p)));
                    var archive = workspace.Scan(workspace.AlreadySentFolder(group), recursive: true).Count > 0;
                    queues.Add(new GroupQueue(group, Forecast.Order(files, group.PostOrder), archive));
                }

                var slots = Forecast.Build(queues, start, now, horizon);

                var summaries = queues.Select(q => new GroupSummaryViewModel(
                    q.Group,
                    q.Ordered.Count,
                    slots.FirstOrDefault(s => s.Group == q.Group && s.Kind is SlotKind.RunsDry or SlotKind.Nothing)?.At,
                    q.Group.Schedule?.IntervalMinutes is { } i && i > 0 && QueueRunway.IsStretching(q.Group, q.Ordered.Count)))
                    .ToList();

                return (slots, summaries);
            }, token);

            Clear();
            foreach (var slot in slots)
                _all.Add(new SlotViewModel(slot));
            foreach (var summary in summaries)
                Groups.Add(summary);

            RebuildDays();

            var dry = summaries.Count(s => s.RunsDryInView);
            Status = dry > 0
                ? _host.Text.Format("perch.status.dry", _all.Count, dry)
                : _host.Text.Format("perch.status.done", _all.Count);

            _ = LoadThumbnailsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("perch.error.read", ex.Message);
            _host.Log(LogLevel.Warning, $"Perch could not read the bot: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RebuildDays()
    {
        var keep = _selected;
        Days.Clear();

        var today = DateTime.Today;
        DayViewModel? current = null;
        foreach (var slot in _all)
        {
            if (current is null || current.Day != slot.Slot.At.Date)
            {
                current = new DayViewModel(slot.Slot.At.Date, today);
                Days.Add(current);
            }

            current.Slots.Add(slot);
        }

        OnPropertyChanged(nameof(IsEmpty));
        if (keep is not null && _all.Contains(keep))
            Selected = keep;
    }

    private void Clear()
    {
        _thumbnails?.Cancel();
        Selected = null;
        foreach (var slot in _all)
            slot.Dispose();
        _all.Clear();
        Days.Clear();
        Groups.Clear();
        Preview = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>A small picture per row, first file only, a few hundred at most and cancelled by the next refresh.</summary>
    private async Task LoadThumbnailsAsync()
    {
        _thumbnails?.Cancel();
        _thumbnails = new CancellationTokenSource();
        var token = _thumbnails.Token;

        foreach (var slot in _all.Take(400).ToList())
        {
            if (token.IsCancellationRequested)
                return;
            if (slot.Asked || slot.FirstFile is not { } path)
                continue;

            slot.Asked = true;
            try
            {
                var (bitmap, pages) = await Task.Run(() => Decode(path, ThumbnailWidth, countPages: true), token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    slot.Pages = pages;
                    slot.Thumbnail = bitmap;
                    slot.Reread();
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A row without a picture still says what and when.
            }
        }
    }

    private async Task ShowPreviewAsync(SlotViewModel? slot)
    {
        Preview = null;
        if (slot?.FirstFile is not { } path)
            return;

        try
        {
            var (bitmap, _) = await Task.Run(() => Decode(path, PreviewWidth, countPages: false));
            if (ReferenceEquals(_selected, slot))
                Preview = bitmap;
            else
                bitmap?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The picture, and for a comic its page count when asked, since the archive is open anyway.</summary>
    private static (Bitmap? Bitmap, int? Pages) Decode(string path, int width, bool countPages)
    {
        int? pages = null;
        if (countPages && MediaRules.IsComic(path))
        {
            try
            {
                pages = MediaRules.ComicPages(path).Count;
            }
            catch (Exception)
            {
            }
        }

        return (MediaRules.Thumbnail(path, width), pages);
    }

    private static DateTime SafeWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.MaxValue;
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
            _host.Log(LogLevel.Warning, $"Could not save Perch settings: {ex.Message}");
        }
    }

    private bool _disposed;

    /// <summary>
    /// Twice, in practice: the shell disposes the view and then its DataContext, and the view
    /// disposes its DataContext itself. The second call has to be a no-op rather than a throw
    /// from a token source that is already gone.
    /// </summary>
    /// <summary>Ctrl+K reaching into the timeline: a slot by the file going out or the group it goes to.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var slot in Days.SelectMany(day => day.Slots))
        {
            if (hits.Count >= limit)
                break;
            if (!slot.IsPost || !SearchWords.Match(words, slot.WhatText, slot.GroupName))
                continue;

            var chosen = slot;
            hits.Add(new SearchHit(slot.WhatText, $"{slot.GroupName} · {slot.Slot.At:ddd HH:mm}", () => Selected = chosen));
        }

        return hits;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _language.Dispose();
        _closing.Cancel();
        _thumbnails?.Cancel();
        foreach (var slot in _all)
            slot.Dispose();
        Preview = null;
        _closing.Dispose();
    }

    // ---- picking, through the host's dialogs rather than a TopLevel of our own ----

    private RelayCommand? _chooseBotCommand;

    public RelayCommand ChooseBotCommand => _chooseBotCommand ??= new RelayCommand(() => _ = ChooseBotAsync());

    private async Task ChooseBotAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["perch.dialog.botfolder"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;
            SetBotRoot(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }
}
