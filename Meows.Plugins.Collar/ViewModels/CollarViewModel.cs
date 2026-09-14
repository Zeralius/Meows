using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar.Services;

namespace Meows.Plugins.Collar.ViewModels;

public sealed class CollarSettings
{
    public List<CollarEntry> Entries { get; set; } = [];

    /// <summary>How far ahead something starts being worth saying out loud.</summary>
    public int LeadDays { get; set; } = Dates.DefaultLead;
}

/// <summary>One choice in a dropdown, named by the language the window is in.</summary>
public sealed class Choice(string key, int value) : ObservableObject
{
    public int Value { get; } = value;

    public string Name => MeowsText.Current[key];

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

public sealed class EntryViewModel : ObservableObject
{
    private readonly CollarViewModel _owner;

    public EntryViewModel(CollarEntry entry, CollarViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public CollarEntry Entry { get; }

    public string Id => Entry.Id;

    public string Title
    {
        get => Entry.Title;
        set
        {
            if (Entry.Title == value)
                return;

            Entry.Title = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Shown));
            _owner.Touched(resort: false);
        }
    }

    /// <summary>Something without a name yet still has to be findable in the list.</summary>
    public string Shown => Entry.Title.Trim().Length > 0
        ? Entry.Title
        : MeowsText.Current["collar.untitled"];

    public string Note
    {
        get => Entry.Note;
        set
        {
            if (Entry.Note == value)
                return;

            Entry.Note = value ?? "";
            OnPropertyChanged();
            _owner.Touched(resort: false);
        }
    }

    /// <summary>
    /// The date as the picker wants it. Avalonia hands out a <see cref="DateTimeOffset"/>; only
    /// the day is kept, because an offset would put a date into a different day depending on
    /// where the machine thinks it is.
    /// </summary>
    public DateTimeOffset? DueOn
    {
        get => new DateTimeOffset(Entry.Due.Date, TimeSpan.Zero);
        set
        {
            var date = (value?.Date ?? DateTime.Today).Date;
            if (Entry.Due.Date == date)
                return;

            Entry.Due = date;
            OnPropertyChanged();
            Reread();
            _owner.Touched(resort: true);
        }
    }

    public Kind Kind
    {
        get => Entry.Kind;
        set
        {
            if (Entry.Kind == value)
                return;

            Entry.Kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KindChoice));
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(KindText));
            _owner.Touched(resort: false);
        }
    }

    /// <summary>
    /// The same thing as a dropdown row. A ComboBox hands back the object it was given, and the
    /// object it was given has to be one of the very items in the list or it shows blank, so
    /// these look the choice up rather than making a new one.
    /// </summary>
    public Choice? KindChoice
    {
        get => _owner.Kinds.FirstOrDefault(c => c.Value == (int)Entry.Kind);
        set
        {
            if (value is not null)
                Kind = (Kind)value.Value;
        }
    }

    public Choice? RepeatChoice
    {
        get => _owner.Repeats.FirstOrDefault(c => c.Value == Entry.RepeatMonths);
        set
        {
            if (value is not null)
                RepeatMonths = value.Value;
        }
    }

    public int RepeatMonths
    {
        get => Entry.RepeatMonths;
        set
        {
            if (Entry.RepeatMonths == value)
                return;

            Entry.RepeatMonths = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RepeatChoice));
            Reread();
            _owner.Touched(resort: true);
        }
    }

    public string? File => Entry.File;

    public bool HasFile => !string.IsNullOrWhiteSpace(Entry.File);

    public string FileName => Entry.File is { } file ? System.IO.Path.GetFileName(file) : "";

    public string KindText => MeowsText.Current[Dates.Describe(Entry.Kind)];

    public string Glyph => Entry.Kind switch
    {
        Services.Kind.Warranty => "🧾",
        Services.Kind.Insurance => "🛡",
        Services.Kind.Inspection => "🔧",
        Services.Kind.Subscription => "🔁",
        Services.Kind.Service => "🧰",
        Services.Kind.Document => "📄",
        _ => "🏷",
    };

    public Standing Standing => Dates.Of(Entry, DateTime.Today, _owner.LeadDays);

    public bool IsOverdue => Standing == Standing.Overdue;

    public bool IsSoon => Standing == Standing.Soon;

    public bool IsDone => Standing == Standing.Done;

    public string DueText
    {
        get
        {
            var date = Entry.Due.ToString("d", CollarViewModel.Culture);

            if (IsDone)
                return MeowsText.Current.Format("collar.when.done", date);

            var days = Dates.DaysLeft(Entry, DateTime.Today);

            return days switch
            {
                0 => MeowsText.Current.Format("collar.when.today", date),
                1 => MeowsText.Current.Format("collar.when.tomorrow", date),
                < 0 => MeowsText.Current.Format("collar.when.late", date, -days),
                _ => MeowsText.Current.Format("collar.when.left", date, days),
            };
        }
    }

    public string RepeatText => Entry.RepeatMonths <= 0
        ? MeowsText.Current["collar.repeat.once"]
        : MeowsText.Current.Format("collar.repeat.every", Entry.RepeatMonths);

    /// <summary>Everything on the row reads differently after a date change or a language change.</summary>
    internal void Reread() => OnEverythingChanged();
}

public sealed class CollarViewModel : ObservableObject, IDisposable, ISearchable
{
    /// <summary>The condition key. One per plugin scope, so it replaces rather than stacks.</summary>
    private const string DueKey = "due";

    private readonly IMeowsHost _host;
    private readonly CollarSettings _settings;
    private readonly LanguageWatch _language;
    private readonly IBackgroundTask? _watch;

    private EntryViewModel? _selected;
    private string? _status;
    private string? _errorMessage;

    public CollarViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<CollarSettings>() ?? new CollarSettings();

        AddCommand = new RelayCommand(() => Add(new CollarEntry
        {
            Due = DateTime.Today.AddMonths(Receipt.WarrantyMonths),
        }));

        HandleCommand = new RelayCommand(Handle, () => Selected is not null);
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null);
        OpenFileCommand = new RelayCommand(() => Open(Selected?.File), () => Selected?.HasFile == true);
        DetachCommand = new RelayCommand(Detach, () => Selected?.HasFile == true);

        Rebuild();

        // Nothing here is expensive; the point of the timer is the calendar. A tab left open
        // across midnight, or across a month, would otherwise still be showing yesterday's
        // arithmetic and would never raise the thing that fell due while it sat there.
        _watch = host.Background.Schedule(
            host.Text["collar.task.watch"], TimeSpan.FromHours(6),
            async context =>
            {
                if (context.Token.IsCancellationRequested)
                    return;

                await Dispatcher.UIThread.InvokeAsync(Refresh);
            },
            runImmediately: false);

        _language = new LanguageWatch(Retranslate);
    }

    /// <summary>Dates as the window's language writes them, not as the machine does.</summary>
    public static CultureInfo Culture => MeowsText.Current.Language == "de"
        ? CultureInfo.GetCultureInfo("de-DE")
        : CultureInfo.GetCultureInfo("en-GB");

    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    public ObservableCollection<Choice> Kinds { get; } =
    [
        new("collar.kind.warranty", (int)Kind.Warranty),
        new("collar.kind.insurance", (int)Kind.Insurance),
        new("collar.kind.inspection", (int)Kind.Inspection),
        new("collar.kind.subscription", (int)Kind.Subscription),
        new("collar.kind.service", (int)Kind.Service),
        new("collar.kind.document", (int)Kind.Document),
        new("collar.kind.other", (int)Kind.Other),
    ];

    public ObservableCollection<Choice> Repeats { get; } =
    [
        new("collar.every.once", 0),
        new("collar.every.month", 1),
        new("collar.every.quarter", 3),
        new("collar.every.half", 6),
        new("collar.every.year", 12),
        new("collar.every.twoyears", 24),
    ];

    public RelayCommand AddCommand { get; }

    public RelayCommand HandleCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand OpenFileCommand { get; }

    public RelayCommand DetachCommand { get; }

    public int LeadDays
    {
        get => _settings.LeadDays;
        set
        {
            var days = Math.Clamp(value, 0, 365);
            if (_settings.LeadDays == days)
                return;

            _settings.LeadDays = days;
            Save();
            OnPropertyChanged();
            Refresh();
        }
    }

    public EntryViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;

            OnPropertyChanged(nameof(HasSelection));
            foreach (var command in new[] { HandleCommand, DeleteCommand, OpenFileCommand, DetachCommand })
                command.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Entries.Count == 0;

    public string Status
    {
        get => _status ?? MeowsText.Current["collar.status.start"];
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

    /// <summary>The line under the header: what is late, what is close, and nothing else.</summary>
    public string Summary
    {
        get
        {
            var overdue = Entries.Count(e => e.IsOverdue);
            var soon = Entries.Count(e => e.IsSoon);

            if (overdue > 0 && soon > 0)
                return _host.Text.Format("collar.summary.both", overdue, soon);
            if (overdue > 0)
                return _host.Text.Format("collar.summary.overdue", overdue);
            if (soon > 0)
                return _host.Text.Format("collar.summary.soon", soon, LeadDays);

            return Entries.Count == 0 ? "" : _host.Text.Format("collar.summary.clear", Entries.Count);
        }
    }

    /// <summary>Adds an entry and selects it, because the next thing wanted is to name it.</summary>
    public void Add(CollarEntry entry)
    {
        _settings.Entries.Add(entry);
        Save();
        Rebuild();
        Selected = Entries.FirstOrDefault(e => e.Id == entry.Id);
        Status = _host.Text.Format("collar.status.added", entry.Title.Trim().Length > 0
            ? entry.Title
            : _host.Text["collar.untitled"]);
    }

    /// <summary>A file dropped on the tab, or picked with the button, becomes most of an entry.</summary>
    public void AddFromFile(string path)
    {
        if (Selected is { } selected && !selected.HasFile)
        {
            selected.Entry.File = path;
            selected.Reread();
            Touched(resort: false);
            Status = _host.Text.Format("collar.status.attached", System.IO.Path.GetFileName(path));
            OnPropertyChanged(nameof(Selected));
            OpenFileCommand.RaiseCanExecuteChanged();
            DetachCommand.RaiseCanExecuteChanged();
            return;
        }

        Add(Receipt.From(path, DateTime.Today));
    }

    /// <summary>Dealt with: a repeat moves to its next date, a one-off is finished.</summary>
    private void Handle()
    {
        if (Selected is not { } selected)
            return;

        var name = selected.Shown;
        var repeats = selected.Entry.RepeatMonths > 0;

        Dates.Handle(selected.Entry, DateTime.Today);
        _host.Store.Record("handled", name, repeats ? selected.Entry.Due.ToString("yyyy-MM-dd") : null);
        Save();
        Rebuild();
        Selected = Entries.FirstOrDefault(e => e.Id == selected.Id);

        Status = repeats
            ? _host.Text.Format("collar.status.moved", name,
                selected.Entry.Due.ToString("d", Culture))
            : _host.Text.Format("collar.status.done", name);
    }

    private void Delete()
    {
        if (Selected is not { } selected)
            return;

        // No confirmation, deliberately. This is one line in a settings file, not a file on disk,
        // and a wrongly deleted date costs a retype rather than the thing itself.
        _settings.Entries.RemoveAll(e => e.Id == selected.Id);
        Save();
        Selected = null;
        Rebuild();
        Status = _host.Text.Format("collar.status.deleted", selected.Shown);
    }

    private void Detach()
    {
        if (Selected is not { } selected)
            return;

        selected.Entry.File = null;
        selected.Reread();
        Touched(resort: false);
        OnPropertyChanged(nameof(Selected));
        OpenFileCommand.RaiseCanExecuteChanged();
        DetachCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Something on a row changed. Saving on every keystroke is cheap here, and the alternative
    /// is a Save button that eventually gets left unpressed.
    /// </summary>
    internal void Touched(bool resort)
    {
        Save();

        if (resort)
        {
            var was = Selected?.Id;
            Rebuild();
            Selected = Entries.FirstOrDefault(e => e.Id == was);
        }
        else
        {
            OnPropertyChanged(nameof(Summary));
            Recheck();
        }
    }

    /// <summary>Reads the calendar again without touching what is stored.</summary>
    public void Refresh()
    {
        foreach (var entry in Entries)
            entry.Reread();

        var was = Selected?.Id;
        Rebuild();
        Selected = Entries.FirstOrDefault(e => e.Id == was);
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var entry in Dates.InOrder(_settings.Entries, DateTime.Today, LeadDays))
            Entries.Add(new EntryViewModel(entry, this));

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        Recheck();
    }

    /// <summary>
    /// The whole point of the plugin: a date that has come round is said out loud from wherever
    /// the user happens to be, and stays said until it is dealt with.
    ///
    /// One key, so a re-check replaces the entry rather than stacking another one on top, and
    /// both branches are here: a condition set and never cleared is worse than no condition.
    /// </summary>
    private void Recheck()
    {
        var overdue = _settings.Entries
            .Where(e => Dates.Of(e, DateTime.Today, LeadDays) == Standing.Overdue)
            .OrderBy(e => e.Due)
            .ToList();

        var soon = _settings.Entries
            .Where(e => Dates.Of(e, DateTime.Today, LeadDays) == Standing.Soon)
            .OrderBy(e => e.Due)
            .ToList();

        if (overdue.Count == 0 && soon.Count == 0)
        {
            _host.Notifications.ClearCondition(DueKey);
            return;
        }

        var worst = overdue.Count > 0 ? overdue : soon;
        var severity = overdue.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Info;

        var title = overdue.Count > 0
            ? _host.Text.Format("collar.notify.overdue", overdue.Count)
            : _host.Text.Format("collar.notify.soon", soon.Count, LeadDays);

        var named = string.Join(", ", worst.Take(3).Select(e =>
            e.Title.Trim().Length > 0 ? e.Title.Trim() : _host.Text["collar.untitled"]));

        var message = worst.Count > 3
            ? _host.Text.Format("collar.notify.more", named, worst.Count - 3)
            : named;

        // One date due is the common case and the one worth a verb: dealt with, or not this
        // week. Several due is a list, and the tab is the place for a list.
        if (worst.Count == 1)
        {
            var only = worst[0];
            _host.Notifications.SetCondition(DueKey, severity, title, message,
                new NotificationAction(_host.Text["collar.notify.done"], () => HandleFromNotification(only)),
                new NotificationAction(_host.Text["collar.notify.snooze"], () => SnoozeFromNotification(only)),
                new NotificationAction(_host.Text["collar.notify.recheck"], Refresh));
            return;
        }

        _host.Notifications.SetCondition(DueKey, severity, title, message,
            new NotificationAction(_host.Text["collar.notify.recheck"], Refresh));
    }

    /// <summary>The Done button on the notification: the same as Handle on the tab, for that entry.</summary>
    private void HandleFromNotification(CollarEntry entry)
    {
        var name = entry.Title.Trim().Length > 0 ? entry.Title.Trim() : _host.Text["collar.untitled"];
        Dates.Handle(entry, DateTime.Today);
        _host.Store.Record("handled", name, entry.RepeatMonths > 0 ? entry.Due.ToString("yyyy-MM-dd") : null);
        Save();
        Rebuild();
        Recheck();
    }

    /// <summary>
    /// A week from today, not a week from when it was due: snoozing something three weeks
    /// overdue by a week would land it two weeks in the past and ring again at once.
    /// </summary>
    private void SnoozeFromNotification(CollarEntry entry)
    {
        var name = entry.Title.Trim().Length > 0 ? entry.Title.Trim() : _host.Text["collar.untitled"];
        entry.Due = DateTime.Today.AddDays(7);
        _host.Store.Record("snoozed", name, entry.Due.ToString("yyyy-MM-dd"));
        Save();
        Rebuild();
        Recheck();
    }

    private void Retranslate()
    {
        foreach (var entry in Entries)
            entry.Reread();
        foreach (var choice in Kinds.Concat(Repeats))
            choice.Reread();

        // The standing condition was written in the old language and nobody would rewrite it, so
        // it is raised again in the new one.
        Recheck();

        OnEverythingChanged();
    }

    private void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!System.IO.File.Exists(path))
        {
            ErrorMessage = _host.Text.Format("collar.error.gone", path);
            return;
        }

        ErrorMessage = null;

        try
        {
            Explorer.Open(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("collar.error.open", path, ex.Message);
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
            _host.Log($"Could not save Collar settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the dates: by title, kind or the file attached. Landing on one selects it.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var entry in Entries)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, entry.Shown, entry.KindText, entry.FileName))
                continue;

            var chosen = entry;
            hits.Add(new SearchHit(entry.Shown, $"{entry.KindText} · {entry.DueText}", () => Selected = chosen));
        }

        return hits;
    }

    public void Dispose()
    {
        _watch?.Cancel();
        _language.Dispose();
    }
}
