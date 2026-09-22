using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Purr.ViewModels;

public sealed class PurrSettings
{
    // Nothing to remember yet. Purr shows what the shell knows and chooses nothing.
}

/// <summary>One watch, with the words for it worked out here so the view only binds.</summary>
public sealed class WatchViewModel(WatchInfo watch) : ObservableObject
{
    public WatchInfo Watch { get; } = watch;

    public string Plugin => Watch.PluginName;

    public string Title => Watch.Title;

    public bool IsStopped => Watch.IsStopped;

    public bool HasFailed => Watch.LastFailure is not null;

    public bool IsBusy => Watch.PassInProgress;

    public bool IsPaused => Watch.IsPaused;

    /// <summary>Running and not paused: the ones the pause buttons apply to.</summary>
    public bool CanPause => !IsStopped && !IsPaused;

    public string Glyph => HasFailed ? "✕" : IsStopped ? "–" : IsPaused ? "‖" : IsBusy ? "…" : "●";

    public string EveryText => MeowsText.Current.Format("purr.every", PurrClock.Span(Watch.Interval));

    /// <summary>When it last looked, as "3 min ago", or that it never has.</summary>
    public string LastText => Watch.LastPassAt is { } at
        ? MeowsText.Current.Format("purr.last", PurrClock.Ago(at))
        : MeowsText.Current["purr.last.never"];

    /// <summary>When it will look next, or why it will not.</summary>
    public string NextText
    {
        get
        {
            var text = MeowsText.Current;
            if (Watch.LastFailure is { } failure)
                return text.Format("purr.stopped.failed", PurrClock.Ago(Watch.StoppedAt ?? DateTime.Now), failure);
            if (Watch.IsStopped)
                return text.Format("purr.stopped", PurrClock.Ago(Watch.StoppedAt ?? DateTime.Now));
            if (Watch.PausedUntil is { } until)
                return until == DateTime.MaxValue ? text["purr.paused"] : text.Format("purr.paused.until", PurrClock.Until(until));
            if (Watch.PassInProgress)
                return Watch.Status.Length > 0 ? text.Format("purr.looking.status", Watch.Status) : text["purr.looking"];
            if (Watch.NextDueAt is { } due)
                return due <= DateTime.Now ? text["purr.next.now"] : text.Format("purr.next", PurrClock.Until(due));
            return "";
        }
    }

    public string PassesText => Watch.Passes == 1
        ? MeowsText.Current["purr.passes.one"]
        : MeowsText.Current.Format("purr.passes.many", Watch.Passes);

    /// <summary>The times move on their own, so the words have to be read again on a tick.</summary>
    public void Tick() => OnEverythingChanged();
}

/// <summary>
/// One line per watch: what Meows is looking at while the window is closed, when it last
/// looked, and which watch quietly stopped. The tray dot says "something happened"; this says
/// what is still happening.
/// </summary>
public sealed class PurrViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable
{
    private readonly IMeowsHost _host;
    private readonly PurrSettings _settings;
    private readonly IBackgroundTask _tick;
    private readonly DateTime _openedAt = DateTime.Now;

    private string? _status;
    private string? _errorMessage;
    private WatchViewModel? _selected;

    /// <summary>
    /// Text worked out in code rather than bound with {m:Tr} has to be read again when the
    /// language changes. Nothing moves, but everything reads differently.
    /// </summary>
    private readonly LanguageWatch _language;

    public PurrViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<PurrSettings>() ?? new PurrSettings();

        RefreshCommand = new RelayCommand(Refresh);
        PauseHourCommand = new RelayCommand(() => Pause(DateTime.Now.AddHours(1)), () => Selected is { CanPause: true });
        PauseTomorrowCommand = new RelayCommand(() => Pause(DateTime.Today.AddDays(1).AddHours(8)), () => Selected is { CanPause: true });
        PauseCommand = new RelayCommand(() => Pause(DateTime.MaxValue), () => Selected is { CanPause: true });
        ResumeCommand = new RelayCommand(Resume, () => Selected is { IsPaused: true });

        _host.Watches.Changed += Refresh;

        // "3 min ago" is only true for a minute. The pass itself is nothing: read the list the
        // shell already keeps, so this is the cheapest watch of them all, and it is on the list
        // too, which is as it should be.
        _tick = _host.Background.Schedule(_host.Text["purr.task.tick"], TimeSpan.FromSeconds(30), _ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Tick);
            return Task.CompletedTask;
        }, runImmediately: false);

        Refresh();
        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            Refresh();
        });
    }

    public ObservableCollection<WatchViewModel> Watches { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand PauseHourCommand { get; }

    public RelayCommand PauseTomorrowCommand { get; }

    public RelayCommand PauseCommand { get; }

    public RelayCommand ResumeCommand { get; }

    private void Pause(DateTime until)
    {
        if (Selected is not { } watch)
            return;
        if (!_host.Watches.Pause(watch.Watch.Id, until))
            ErrorMessage = _host.Text["purr.error.pause"];
        Refresh();
    }

    private void Resume()
    {
        if (Selected is not { } watch)
            return;
        if (!_host.Watches.Resume(watch.Watch.Id))
            ErrorMessage = _host.Text["purr.error.pause"];
        Refresh();
    }

    private void RaisePauseCommands()
    {
        PauseHourCommand.RaiseCanExecuteChanged();
        PauseTomorrowCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
    }

    public WatchViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            RaisePauseCommands();
        }
    }

    public bool HasSelection => _selected is not null;

    public bool IsEmpty => Watches.Count == 0;

    public int StoppedCount => Watches.Count(w => w.IsStopped);

    public bool HasStopped => StoppedCount > 0;

    /// <summary>The headline: how many watches, over how many plugins, and how many have stopped.</summary>
    public string SummaryText
    {
        get
        {
            var text = _host.Text;
            if (Watches.Count == 0)
                return text["purr.summary.none"];

            var plugins = Watches.Select(w => w.Watch.PluginId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var line = text.Format("purr.summary", Watches.Count, plugins);
            return StoppedCount > 0 ? $"{line} {text.Format("purr.summary.stopped", StoppedCount)}" : line;
        }
    }

    /// <summary>The headline again, on the Home tab, red while a watch has stopped.</summary>
    public Glance? Glance() => new(SummaryText, HasStopped);

    /// <summary>How long this tab has been open, which is the shortest true answer to "since when".</summary>
    public string SinceText => _host.Text.Format("purr.since", _openedAt.ToString("HH:mm"), PurrClock.Span(DateTime.Now - _openedAt));

    public string Status
    {
        get => _status ?? _host.Text["purr.status.ready"];
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

    /// <summary>Reads the shell's list again. Stopped ones first, then by plugin, so trouble is at the top.</summary>
    private void Refresh()
    {
        var keep = Selected?.Watch;

        IReadOnlyList<WatchInfo> all;
        try
        {
            all = _host.Watches.All();
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("purr.error.read", ex.Message);
            return;
        }

        Watches.Clear();
        foreach (var watch in all
                     .OrderByDescending(w => w.LastFailure is not null)
                     .ThenByDescending(w => w.IsStopped)
                     .ThenBy(w => w.PluginName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase))
            Watches.Add(new WatchViewModel(watch));

        Selected = keep is null
            ? null
            : Watches.FirstOrDefault(w => w.Watch.PluginId == keep.PluginId && w.Watch.Title == keep.Title);

        Status = _host.Text.Format("purr.status.read", DateTime.Now.ToString("HH:mm:ss"));
        RaiseSummary();
    }

    private void Tick()
    {
        foreach (var watch in Watches)
            watch.Tick();
        OnPropertyChanged(nameof(SinceText));
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StoppedCount));
        OnPropertyChanged(nameof(HasStopped));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SinceText));
    }

    /// <summary>Ctrl+K reaching into the list: a watch by its title or the plugin it belongs to.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var watch in Watches)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, watch.Title, watch.Plugin))
                continue;

            var chosen = watch;
            hits.Add(new SearchHit(watch.Title, $"{watch.Plugin} · {watch.NextText}", () => Selected = chosen));
        }

        return hits;
    }

    public void Dispose()
    {
        _host.Watches.Changed -= Refresh;
        _tick.Cancel();
        _language.Dispose();
    }
}

/// <summary>Spans and moments in words, short enough for a list row.</summary>
public static class PurrClock
{
    public static string Span(TimeSpan span)
    {
        var text = MeowsText.Current;
        if (span.TotalSeconds < 90)
            return text.Format("purr.span.seconds", (int)Math.Max(1, span.TotalSeconds));
        // Whole hours read as hours: "every 1 h" beats "every 60 min", but 90 minutes stays minutes.
        if (span.TotalMinutes < 90 && (span.TotalMinutes < 60 || span.TotalMinutes % 60 != 0))
            return text.Format("purr.span.minutes", (int)Math.Round(span.TotalMinutes));
        if (span.TotalHours < 36)
            return text.Format("purr.span.hours", (int)Math.Round(span.TotalHours));
        return text.Format("purr.span.days", (int)Math.Round(span.TotalDays));
    }

    public static string Ago(DateTime when) => MeowsText.Current.Format("purr.ago", Span(DateTime.Now - when));

    public static string Until(DateTime when) => MeowsText.Current.Format("purr.in", Span(when - DateTime.Now));
}
