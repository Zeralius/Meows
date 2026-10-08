using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Naptime.Services;

namespace Meows.Plugins.Naptime.ViewModels;

public sealed class NaptimeSettings
{
    public List<Habit> Habits { get; set; } = [];
}

public sealed class HabitRowViewModel : ObservableObject
{
    private readonly NaptimeViewModel _owner;

    public HabitRowViewModel(Habit habit, NaptimeViewModel owner)
    {
        Habit = habit;
        _owner = owner;

        DeleteCommand = new RelayCommand(() => _owner.DeleteRow(this));
        TargetUpCommand = new RelayCommand(() => _owner.ShiftTarget(this, +1));
        TargetDownCommand = new RelayCommand(() => _owner.ShiftTarget(this, -1));
    }

    public Habit Habit { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand TargetUpCommand { get; }

    public RelayCommand TargetDownCommand { get; }

    public string Id => Habit.Id;

    public string Name
    {
        get => Habit.Name;
        set
        {
            if (Habit.Name == value)
                return;
            Habit.Name = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Shown));
            _owner.Touched();
        }
    }

    public string Shown => Habit.Name.Trim().Length > 0
        ? Habit.Name
        : MeowsText.Current["naptime.untitled"];

    public int Target
    {
        get => Habit.Target;
        set
        {
            var target = Math.Clamp(value, 1, 7);
            if (Habit.Target == target)
                return;
            Habit.Target = target;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetText));
            OnPropertyChanged(nameof(WeekText));
            _owner.Touched();
        }
    }

    public string TargetText => MeowsText.Current.Format("naptime.target", Habit.Target);

    /// <summary>Today's tick. Unticking takes back today's line in the history, not the habit.</summary>
    public bool DoneToday
    {
        get => Habit.DoneDays.Any(d => d.Date == DateTime.Today);
        set
        {
            var has = Habit.DoneDays.Any(d => d.Date == DateTime.Today);
            if (has == value)
                return;

            if (value)
                Habit.DoneDays.Add(DateTime.Today);
            else
                Habit.DoneDays.RemoveAll(d => d.Date == DateTime.Today);

            OnPropertyChanged();
            OnPropertyChanged(nameof(WeekText));
            OnPropertyChanged(nameof(StreakText));
            _owner.TickChanged(this, value);
        }
    }

    public string WeekText
    {
        get
        {
            var done = Streaks.ThisWeek(Habit, DateTime.Today);
            return MeowsText.Current.Format("naptime.week", done, Habit.Target);
        }
    }

    public string StreakText => Streaks.StreakText(Streaks.Of(Habit, DateTime.Today), MeowsText.Current);

    public bool HasStreak => Streaks.Of(Habit, DateTime.Today) > 0;

    /// <summary>Everything on the row reads differently after midnight or a language change.</summary>
    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// Naptime: small habits, ticked daily. Weekly targets, streaks with one miss forgiven, and a
/// line that says what today looks like. Nothing syncs anywhere: the ticks live in one file.
/// </summary>
public sealed class NaptimeViewModel : ObservableObject, IDisposable, ISearchable, IHandoffTarget, IGlanceable, IActionTarget
{
    /// <summary>The condition key. One per plugin scope, so it replaces rather than stacks.</summary>
    private const string IdleKey = "idle";

    private readonly IMeowsHost _host;
    private readonly NaptimeSettings _settings;
    private readonly LanguageWatch _language;
    private readonly IBackgroundTask? _watch;

    private HabitRowViewModel? _selected;
    private string? _status;
    private string? _errorMessage;
    private string _newHabitName = "";

    public NaptimeViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<NaptimeSettings>() ?? new NaptimeSettings();

        AddCommand = new RelayCommand(AddHabit, () => !string.IsNullOrWhiteSpace(NewHabitName));
        RefreshCommand = new RelayCommand(Refresh);

        Rebuild();

        // Nothing here is expensive; the point of the timer is the calendar. A tab left open
        // across midnight would otherwise still be showing yesterday's ticks and streaks.
        _watch = host.Background.Schedule(
            host.Text["naptime.task.watch"], TimeSpan.FromHours(6),
            async context =>
            {
                if (context.Token.IsCancellationRequested)
                    return;

                await Dispatcher.UIThread.InvokeAsync(Refresh);
            },
            runImmediately: false);

        _language = new LanguageWatch(Retranslate);
    }

    public ObservableCollection<HabitRowViewModel> Habits { get; } = [];

    public RelayCommand AddCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public HabitRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => Selected is not null;

    public string NewHabitName
    {
        get => _newHabitName;
        set
        {
            if (SetField(ref _newHabitName, value))
                AddCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["naptime.status.ready"];
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

    public bool IsEmpty => Habits.Count == 0;

    /// <summary>The line under the header: ticks today, and weeks met.</summary>
    public string Summary => Streaks.SummaryOf(_settings.Habits, DateTime.Today, _host.Text);

    /// <summary>The same line on the Home tab, red after two days with nothing ticked.</summary>
    public Glance? Glance() => Streaks.GlanceOf(_settings.Habits, DateTime.Today, _host.Text);

    /// <summary>Adds a habit and selects it, because the next thing wanted is to name it.</summary>
    public void AddHabit()
    {
        var name = NewHabitName.Trim();
        if (name.Length == 0)
            return;

        if (_settings.Habits.Any(h => string.Equals(h.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            Status = _host.Text.Format("naptime.status.already", name);
            return;
        }

        var habit = new Habit { Name = name };
        _settings.Habits.Add(habit);
        NewHabitName = "";
        Save();
        Rebuild();
        Selected = Habits.FirstOrDefault(h => h.Id == habit.Id);
        Status = _host.Text.Format("naptime.status.added", name);
    }

    internal void DeleteRow(HabitRowViewModel row)
    {
        // No confirmation, deliberately. A habit is a line in a settings file, not a file.
        var name = row.Shown;
        var wasSelected = Selected?.Id == row.Id;
        _settings.Habits.RemoveAll(h => h.Id == row.Id);
        Save();
        if (wasSelected)
            Selected = null;
        Rebuild();
        Status = _host.Text.Format("naptime.status.deleted", name);
    }

    internal void ShiftTarget(HabitRowViewModel row, int direction) =>
        row.Target = Math.Clamp(row.Target + direction, 1, 7);

    /// <summary>A tick set or taken back on a row: saved, journaled, and shown again.</summary>
    internal void TickChanged(HabitRowViewModel row, bool ticked)
    {
        Save();
        if (ticked)
            _host.Store.Record("ticked", row.Shown);
        OnPropertyChanged(nameof(Summary));
        Recheck();
    }

    /// <summary>Something renamed or retargeted. Saving on every keystroke is cheap here.</summary>
    internal void Touched()
    {
        Save();
        OnPropertyChanged(nameof(Summary));
        Recheck();
    }

    /// <summary>
    /// A rule's "tick it": the habit named after what the rule was about gets today's tick.
    /// Nothing is selected, since nobody is necessarily looking. Asking twice for the same day
    /// is one tick, not two.
    /// </summary>
    public Task<string> Perform(ActionRequest request, CancellationToken token)
    {
        if (request.Action != NaptimePlugin.TickAction)
            throw new ActionDeclinedException(_host.Text.Format("naptime.action.unknown", request.Action));

        var name = request.Subject.Trim();
        var habit = _settings.Habits.FirstOrDefault(h =>
            string.Equals(h.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (habit is null)
            throw new ActionDeclinedException(_host.Text.Format("naptime.action.nohabit", name.Length > 0 ? name : request.Subject));

        if (habit.DoneDays.Any(d => d.Date == DateTime.Today))
            return Task.FromResult(_host.Text.Format("naptime.action.already", habit.Name));

        habit.DoneDays.Add(DateTime.Today);
        _host.Store.Record("ticked", habit.Name);
        Save();
        Rebuild();

        return Task.FromResult(_host.Text.Format("naptime.action.ticked", habit.Name));
    }

    public bool Accepts(Handoff handoff) =>
        handoff.Verb == NaptimePlugin.ShowVerb && handoff.Note is { Length: > 0 };

    public void Receive(Handoff handoff)
    {
        if (!Accepts(handoff))
            return;

        // From Ctrl+K while Naptime was off: the hit, now that there is a list to select in.
        Selected = Habits.FirstOrDefault(h => h.Id == handoff.Note);
    }

    /// <summary>Reads the calendar again without touching what is stored.</summary>
    public void Refresh()
    {
        foreach (var habit in Habits)
            habit.Reread();

        Recheck();
        OnPropertyChanged(nameof(Summary));
        Status = _host.Text.Format("naptime.status.refreshed", DateTime.Now.ToString("HH:mm"));
    }

    private void Rebuild()
    {
        var keep = Selected?.Id;
        Habits.Clear();
        foreach (var habit in _settings.Habits.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase))
            Habits.Add(new HabitRowViewModel(habit, this));
        Selected = Habits.FirstOrDefault(h => h.Id == keep);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        Recheck();
    }

    /// <summary>
    /// Two days with nothing ticked anywhere is said out loud from wherever the user happens to
    /// be, and stays said until something gets its tick.
    ///
    /// One key, so a re-check replaces the entry rather than stacking another one on top, and
    /// both branches are here: a condition set and never cleared is worse than no condition.
    /// </summary>
    private void Recheck()
    {
        var glance = Streaks.GlanceOf(_settings.Habits, DateTime.Today, _host.Text);
        if (glance is null || !glance.IsTrouble)
        {
            _host.Notifications.ClearCondition(IdleKey);
            return;
        }

        _host.Notifications.SetCondition(IdleKey, NotificationSeverity.Info,
            _host.Text["naptime.notify"], glance.Text,
            new NotificationAction(_host.Text["naptime.notify.recheck"], Refresh));
    }

    private void Retranslate()
    {
        foreach (var habit in Habits)
            habit.Reread();

        // The standing condition was written in the old language and nobody would rewrite it, so
        // it is raised again in the new one.
        Recheck();

        OnEverythingChanged();
        OnPropertyChanged(nameof(Summary));
    }

    private void Save()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not save Naptime settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the habits: by name, from what is already loaded. Landing on one selects it.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var habit in Habits)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, habit.Shown))
                continue;

            var chosen = habit;
            hits.Add(new SearchHit(chosen.Shown, chosen.WeekText, () => Selected = chosen));
        }

        return hits;
    }

    public void Dispose()
    {
        _watch?.Cancel();
        _language.Dispose();
    }
}
