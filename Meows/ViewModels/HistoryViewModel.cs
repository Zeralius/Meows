using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.ViewModels;

/// <summary>One line of history, with the words for it worked out once.</summary>
public sealed class HistoryLineViewModel(StoredEvent stored, string pluginName)
{
    public StoredEvent Event { get; } = stored;

    public string When => Event.At.Date == DateTime.Today
        ? Event.At.ToString("HH:mm")
        : Event.At.ToString("ddd d MMM HH:mm");

    public string Plugin { get; } = pluginName;

    public string Kind => Event.Kind;

    public string Subject => Event.Subject;

    public string ShortSubject => Path.GetFileName(Event.Subject) is { Length: > 0 } name ? name : Event.Subject;

    public string Detail => Event.Detail ?? "";

    public bool HasDetail => !string.IsNullOrEmpty(Event.Detail);

    public bool IsPath => Event.Subject.Length > 2 && Event.Subject[1] == ':';
}

/// <summary>
/// The History tab: what every plugin did, newest first, in one list.
///
/// The shell's own view of the store, across plugins, which is the one thing a plugin cannot
/// see through its scoped store. Filtered by a word and by plugin, read again on demand rather
/// than watched, since history is read occasionally and written constantly.
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly MeowsStore _store;
    private readonly Func<string, string> _pluginName;
    private readonly Func<string, IUndoTarget?> _undoTargetFor;
    private readonly Action<string> _showPlugin;
    private string _filter = "";
    private string? _plugin;
    private HistoryLineViewModel? _selected;
    private string? _notice;

    public HistoryViewModel(MeowsStore store, Func<string, string> pluginName)
        : this(store, pluginName, _ => null, _ => { })
    {
    }

    /// <param name="undoTargetFor">The open plugin's view model as an <see cref="IUndoTarget"/>, or null when it is not open or does not reverse anything.</param>
    /// <param name="showPlugin">Brings that plugin's tab to the front before a line is put back.</param>
    public HistoryViewModel(MeowsStore store, Func<string, string> pluginName,
        Func<string, IUndoTarget?> undoTargetFor, Action<string> showPlugin)
    {
        _store = store;
        _pluginName = pluginName;
        _undoTargetFor = undoTargetFor;
        _showPlugin = showPlugin;
        RefreshCommand = new RelayCommand(Refresh);
        RevealCommand = new RelayCommand(Reveal, () => Selected is { IsPath: true });
        PutBackCommand = new RelayCommand(PutBack, () => CanPutBack);
        AskToForgetCommand = new RelayCommand(_ => AskToForget(SelectedForgetAge.Days));
        ForgetCommand = new RelayCommand(Forget, () => _forgetCutoff is not null);
        CancelForgetCommand = new RelayCommand(() => ForgetCutoff = null);
        CompactCommand = new RelayCommand(Compact);
        Refresh();
    }

    // ---- keeping the file small ----

    private DateTime? _forgetCutoff;
    private long _forgetCount;
    private string? _forgetPlugin;
    private ForgetAge _selectedForgetAge = ForgetAges[3];

    /// <summary>One choice of how far back to forget. Zero days is everything.</summary>
    public sealed record ForgetAge(int Days, string Key)
    {
        public override string ToString() => MeowsText.Current[Key];
    }

    public static readonly IReadOnlyList<ForgetAge> ForgetAges =
    [
        new(30, "history.age.month"),
        new(91, "history.age.threemonths"),
        new(182, "history.age.sixmonths"),
        new(365, "history.age.year"),
        new(0, "history.age.everything"),
    ];

    public IReadOnlyList<ForgetAge> ForgetChoices => ForgetAges;

    public ForgetAge SelectedForgetAge
    {
        get => _selectedForgetAge;
        set => SetField(ref _selectedForgetAge, value ?? ForgetAges[3]);
    }

    /// <summary>Asks first. The parameter is a number of days; the confirmation says how many lines go.</summary>
    public RelayCommand AskToForgetCommand { get; }

    public RelayCommand ForgetCommand { get; }

    public RelayCommand CancelForgetCommand { get; }

    /// <summary>Gives the room back after forgetting. Safe to press any time; it only ever makes the file smaller.</summary>
    public RelayCommand CompactCommand { get; }

    /// <summary>The database file, in words: how big, how many lines.</summary>
    public string StoreSizeText => MeowsText.Current.Format("history.store.size", Humanise(_store.FileSize), _store.Count());

    private DateTime? ForgetCutoff
    {
        get => _forgetCutoff;
        set
        {
            if (_forgetCutoff == value)
                return;
            _forgetCutoff = value;
            OnPropertyChanged(nameof(IsAskingToForget));
            OnPropertyChanged(nameof(ForgetPrompt));
            ForgetCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAskingToForget => _forgetCutoff is not null;

    /// <summary>
    /// The question, with everything that matters in it: how many lines, how old, and whose.
    /// A plugin chosen in the dropdown narrows the forgetting to that plugin's lines, which is
    /// how "clear what Purrge did" is asked without touching what Kibble did.
    /// </summary>
    public string ForgetPrompt
    {
        get
        {
            if (_forgetCutoff is not { } cutoff)
                return "";

            var text = MeowsText.Current;
            var whose = _forgetPlugin is null ? text["history.forget.every"] : _pluginName(_forgetPlugin);
            return cutoff > DateTime.UtcNow
                ? text.Format("history.forget.prompt.all", _forgetCount, whose)
                : text.Format("history.forget.prompt", _forgetCount, cutoff.ToLocalTime().ToString("d MMM yyyy"), whose);
        }
    }

    private void AskToForget(int days)
    {
        // Zero days is everything: a cutoff just past now catches every line there is.
        var cutoff = days <= 0 ? DateTime.UtcNow.AddMinutes(1) : DateTime.UtcNow.AddDays(-days);
        _forgetPlugin = _plugin;
        _forgetCount = _store.CountOlderThan(cutoff, _forgetPlugin);
        if (_forgetCount == 0)
        {
            Notice = MeowsText.Current["history.forget.nothing"];
            return;
        }
        ForgetCutoff = cutoff;
    }

    private void Forget()
    {
        if (_forgetCutoff is not { } cutoff)
            return;

        var gone = _store.Forget(cutoff, _forgetPlugin);
        _store.Compact();
        ForgetCutoff = null;
        Notice = MeowsText.Current.Format("history.forget.done", gone, Humanise(_store.FileSize));
        Refresh();
    }

    private void Compact()
    {
        var before = _store.FileSize;
        Notice = _store.Compact()
            ? MeowsText.Current.Format("history.compact.done", Humanise(before), Humanise(_store.FileSize))
            : MeowsText.Current["history.compact.failed"];
        OnPropertyChanged(nameof(StoreSizeText));
    }

    private static string Humanise(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };

    public RelayCommand PutBackCommand { get; }

    /// <summary>
    /// Whether the selected line can be reversed from here: its plugin is open, says it can
    /// undo lines of this kind, and says this one is still undoable. Asked of the plugin rather
    /// than guessed from the kind, since only Kibble knows whether the file is still in the queue.
    /// </summary>
    public bool CanPutBack =>
        Selected is { } line && _undoTargetFor(line.Event.Plugin) is { } target && Safely(() => target.CanUndo(line.Event));

    /// <summary>What the last put-back said, or nothing.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetField(ref _notice, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    private void PutBack()
    {
        if (Selected is not { } line || _undoTargetFor(line.Event.Plugin) is not { } target)
            return;

        _showPlugin(line.Event.Plugin);
        string? failure;
        try
        {
            failure = target.Undo(line.Event);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        Notice = failure is null
            ? MeowsText.Current.Format("history.putback.done", line.ShortSubject)
            : MeowsText.Current.Format("history.putback.failed", failure);
        Refresh();
    }

    private static bool Safely(Func<bool> ask)
    {
        try
        {
            return ask();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ObservableCollection<HistoryLineViewModel> Lines { get; } = [];

    public ObservableCollection<string> PluginChoices { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand RevealCommand { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? ""))
                Refresh();
        }
    }

    /// <summary>The plugin id being shown, or null for all of them. The dropdown shows names.</summary>
    public string? SelectedPluginChoice
    {
        get => _plugin is null ? MeowsText.Current["history.all"] : _pluginName(_plugin);
        set
        {
            var id = value is null || value == MeowsText.Current["history.all"]
                ? null
                : _store.Plugins().FirstOrDefault(p => _pluginName(p) == value);
            if (_plugin == id)
                return;
            _plugin = id;
            OnPropertyChanged();
            Refresh();
        }
    }

    public HistoryLineViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanPutBack));
                RevealCommand.RaiseCanExecuteChanged();
                PutBackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public bool IsEmpty => Lines.Count == 0;

    public string CountText => MeowsText.Current.Format("history.count", Lines.Count, _store.Count());

    public string StorePath => _store.FilePath;

    public void Refresh()
    {
        var chosen = SelectedPluginChoice;
        PluginChoices.Clear();
        PluginChoices.Add(MeowsText.Current["history.all"]);
        foreach (var plugin in _store.Plugins())
            PluginChoices.Add(_pluginName(plugin));
        OnPropertyChanged(nameof(SelectedPluginChoice));

        Lines.Clear();
        foreach (var stored in _store.Events(_plugin, null, _filter.Trim(), 300))
            Lines.Add(new HistoryLineViewModel(stored, _pluginName(stored.Plugin)));

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(StoreSizeText));
    }

    private void Reveal()
    {
        if (Selected is not { IsPath: true } line)
            return;

        try
        {
            if (File.Exists(line.Subject))
                Explorer.Reveal(line.Subject);
            else if (Directory.Exists(line.Subject))
                Explorer.Open(line.Subject);
            else if (Path.GetDirectoryName(line.Subject) is { } folder && Directory.Exists(folder))
                Explorer.Open(folder);
        }
        catch (Exception)
        {
            // Gone, or unreachable. The line is still history.
        }
    }

    /// <summary>Everything worked out in code reads differently now.</summary>
    public void Retranslate()
    {
        OnEverythingChanged();
        Refresh();
    }
}
