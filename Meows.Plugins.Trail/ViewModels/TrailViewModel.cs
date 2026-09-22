using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Trail.Services;

namespace Meows.Plugins.Trail.ViewModels;

public sealed class TrailSettings
{
    /// <summary>The last command asked about, since it is usually asked about twice.</summary>
    public string? LastCommand { get; set; }
}

/// <summary>One entry on the list, with what is wrong with it if anything is.</summary>
public sealed class EntryViewModel(PathEntry entry, Action picked) : ObservableObject
{
    private bool _isPicked;

    public PathEntry Entry { get; } = entry;

    public string Raw => Entry.Raw.Length == 0 ? MeowsText.Current["trail.entry.empty"] : Entry.Raw;

    /// <summary>Only shown when it differs, which is exactly when a variable is involved.</summary>
    public string Expanded => Entry.Expanded;

    public bool ShowsExpanded => !string.Equals(Entry.Raw, Entry.Expanded, StringComparison.Ordinal);

    public string ScopeText => MeowsText.Current[Entry.Scope switch
    {
        PathScope.Machine => "trail.scope.machine",
        PathScope.User => "trail.scope.user",
        _ => "trail.scope.process",
    }];

    public string Position => (Entry.Position + 1).ToString();

    public bool IsTrouble => Entry.IsTrouble;

    public string FaultText => Entry.Fault switch
    {
        PathFault.Missing => MeowsText.Current["trail.fault.missing"],
        PathFault.Duplicate => MeowsText.Current.Format("trail.fault.duplicate", Entry.Note ?? "?"),
        PathFault.Empty => MeowsText.Current["trail.fault.empty"],
        PathFault.Malformed => MeowsText.Current["trail.fault.malformed"],
        PathFault.Unexpanded => MeowsText.Current["trail.fault.unexpanded"],
        _ => "",
    };

    /// <summary>Only the user's half can be ticked; the machine's is not Meows' to change.</summary>
    public bool CanPick => Entry.CanEdit && Entry.IsTrouble;

    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            // The button says how many would go, so it has to be told when that changes.
            if (SetField(ref _isPicked, value))
                picked();
        }
    }
}

/// <summary>
/// PATH, read and explained. What is on it, in the order Windows searches it, what is wrong with
/// each entry, and which copy of a typed command actually wins.
///
/// Read-only apart from one verb, and that verb is fenced: the user's half only, a copy of the
/// old value written to the plugin's folder before anything changes, the raw form kept so a
/// variable is still a variable afterwards, and the change announced so open programs are not
/// left disagreeing with the registry.
/// </summary>
public sealed class TrailViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable
{
    private readonly IMeowsHost _host;
    private readonly TrailSettings _settings;
    private readonly LanguageWatch _language;

    private IReadOnlyList<PathEntry> _entries = [];
    private EntryViewModel? _selected;
    private string _command = "";
    private Winner? _winner;
    private string? _status;
    private string? _errorMessage;
    private string? _lastBackup;
    private bool _isConfirmingTidy;
    private bool _hasRead;

    public TrailViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<TrailSettings>() ?? new TrailSettings();
        _command = _settings.LastCommand ?? "";
        _language = new LanguageWatch(OnEverythingChanged);

        RefreshCommand = new RelayCommand(Refresh);
        ResolveCommand = new RelayCommand(Resolve, () => Command.Trim().Length > 0);
        TidyCommand = new RelayCommand(Tidy, () => Picked.Count > 0);
        UndoCommand = new RelayCommand(Undo, () => _lastBackup is not null);
        ShowFolderCommand = new RelayCommand(ShowFolder, () => Selected is { Entry.Fault: not PathFault.Missing });

        Refresh();
    }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ResolveCommand { get; }

    public RelayCommand TidyCommand { get; }

    public RelayCommand UndoCommand { get; }

    public RelayCommand ShowFolderCommand { get; }

    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    public EntryViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
                ShowFolderCommand.RaiseCanExecuteChanged();
        }
    }

    private List<EntryViewModel> Picked => Entries.Where(e => e is { IsPicked: true, CanPick: true }).ToList();

    public bool IsEmpty => _hasRead && Entries.Count == 0;

    /// <summary>The headline: how long the list is and how much of it is not doing anything.</summary>
    public string SummaryText
    {
        get
        {
            var text = _host.Text;
            if (!_hasRead)
                return text["trail.status.ready"];

            var trouble = _entries.Count(e => e.IsTrouble);
            return trouble == 0
                ? text.Format("trail.summary.clean", _entries.Count)
                : text.Format("trail.summary", _entries.Count, trouble);
        }
    }

    public string Command
    {
        get => _command;
        set
        {
            if (!SetField(ref _command, value))
                return;
            ResolveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>What a typed command would run, and every copy of it that will never run.</summary>
    public string WinnerText
    {
        get
        {
            if (_winner is not { } winner)
                return "";
            var text = _host.Text;
            var line = text.Format("trail.winner", winner.Command, winner.FoundAt, winner.Position + 1);
            if (winner.Shadowed.Count > 0)
                line += " " + text.Format("trail.winner.shadowed", winner.Shadowed.Count);
            return line;
        }
    }

    public bool HasWinner => _winner is not null;

    public string ShadowedText => _winner is { Shadowed.Count: > 0 } winner
        ? string.Join("\n", winner.Shadowed)
        : "";

    public bool HasShadowed => _winner is { Shadowed.Count: > 0 };

    public string Status
    {
        get => _status ?? _host.Text["trail.status.ready"];
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

    // ---- Reading -----------------------------------------------------------------------------

    /// <summary>
    /// A registry read and one Directory.Exists per entry, which is tens of entries and not
    /// hundreds, so it happens in place rather than as a background task. Everything slower than
    /// this in Meows goes through the Tasks panel; this genuinely does not need to.
    /// </summary>
    private void Refresh()
    {
        try
        {
            _entries = PathRead.All();
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _entries = [];
            ErrorMessage = ex.Message;
        }

        _hasRead = true;
        var was = Selected?.Entry.Raw;

        Entries.Clear();
        foreach (var entry in _entries)
            Entries.Add(new EntryViewModel(entry, PickChanged));

        Selected = Entries.FirstOrDefault(e => e.Entry.Raw == was);
        _isConfirmingTidy = false;

        if (_winner is not null)
            Resolve();

        Status = _host.Text.Format("trail.status.read", DateTime.Now.ToString("HH:mm"));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsEmpty));
        RaiseTidy();
    }

    private void Resolve()
    {
        var command = Command.Trim();
        if (command.Length == 0)
            return;

        _settings.LastCommand = command;
        SaveSettings();

        try
        {
            _winner = PathRead.Resolve(command, _entries);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        if (_winner is null)
            Status = _host.Text.Format("trail.winner.none", command);
        else
            Status = _host.Text.Format("trail.status.read", DateTime.Now.ToString("HH:mm"));

        OnPropertyChanged(nameof(WinnerText));
        OnPropertyChanged(nameof(HasWinner));
        OnPropertyChanged(nameof(ShadowedText));
        OnPropertyChanged(nameof(HasShadowed));
    }

    private void ShowFolder()
    {
        if (Selected is not { } row)
            return;
        try
        {
            if (Directory.Exists(row.Entry.Expanded))
                Explorer.Open(row.Entry.Expanded);
            else
                Status = _host.Text.Format("trail.fault.gone", row.Entry.Expanded);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    // ---- The one verb ------------------------------------------------------------------------

    public bool IsConfirmingTidy => _isConfirmingTidy;

    public string TidyText
    {
        get
        {
            var count = Picked.Count;
            var text = _host.Text;
            if (count == 0)
                return text["trail.tidy"];
            return _isConfirmingTidy ? text.Format("trail.tidy.sure", count) : text.Format("trail.tidy.count", count);
        }
    }

    /// <summary>
    /// Removes the ticked entries from the user's PATH, having first written the old value to
    /// the plugin's own folder. A backup that could not be written stops the edit: this is a
    /// variable that can break the logon, so there is always a way back before there is a change.
    /// </summary>
    private void Tidy()
    {
        var picked = Picked;
        if (picked.Count == 0)
            return;

        if (!_isConfirmingTidy)
        {
            _isConfirmingTidy = true;
            RaiseTidy();
            return;
        }

        _isConfirmingTidy = false;
        RaiseTidy();

        var stored = PathRead.Stored(PathScope.User);
        var plan = PathWrite.Plan(stored, picked.Select(p => p.Entry));

        if (!plan.ChangesAnything)
        {
            Status = _host.Text["trail.tidy.nothing"];
            return;
        }

        string backup;
        try
        {
            backup = PathWrite.Backup(_host.DataDirectory, plan.Before);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("trail.tidy.nobackup", ex.Message);
            return;
        }

        if (PathWrite.Apply(plan.After) is { } failure)
        {
            ErrorMessage = _host.Text.Format("trail.tidy.failed", failure);
            return;
        }

        _lastBackup = backup;
        UndoCommand.RaiseCanExecuteChanged();
        ErrorMessage = null;
        Status = _host.Text.Format("trail.tidy.done", plan.Removed.Count, Path.GetFileName(backup));

        _host.Log(LogLevel.Warning, $"Trail removed {plan.Removed.Count} entr(ies) from the user PATH. Backup: {backup}");
        _host.Store.Record("tidied", "PATH", _host.Text.Format("trail.journal.tidied", plan.Removed.Count),
            new Dictionary<string, string> { ["backup"] = backup, ["removed"] = string.Join(";", plan.Removed) });

        Refresh();
    }

    private void Undo()
    {
        if (_lastBackup is not { } backup)
            return;

        if (PathWrite.Restore(backup) is { } failure)
        {
            ErrorMessage = _host.Text.Format("trail.tidy.failed", failure);
            return;
        }

        ErrorMessage = null;
        Status = _host.Text["trail.undo.done"];
        _host.Log("Trail put the previous PATH back.");
        _host.Store.Record("restored", "PATH", _host.Text["trail.journal.restored"]);
        _lastBackup = null;
        UndoCommand.RaiseCanExecuteChanged();
        Refresh();
    }

    /// <summary>Ticking a box changes what the button says, so the button has to be asked again.</summary>
    public void PickChanged()
    {
        _isConfirmingTidy = false;
        RaiseTidy();
    }

    private void RaiseTidy()
    {
        OnPropertyChanged(nameof(IsConfirmingTidy));
        OnPropertyChanged(nameof(TidyText));
        TidyCommand.RaiseCanExecuteChanged();
    }

    // ---- The rest of the shell ----------------------------------------------------------------

    /// <summary>On the Home card, and only when there is something wrong: a clean PATH is not news.</summary>
    public Glance? Glance()
    {
        if (!_hasRead)
            return null;
        var trouble = _entries.Count(e => e.IsTrouble);
        return trouble == 0 ? null : new Glance(SummaryText, IsTrouble: false);
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        return Entries
            .Where(e => SearchWords.Match(words, e.Entry.Raw, e.Entry.Expanded))
            .Take(limit)
            .Select(e => new SearchHit(e.Entry.Expanded, e.IsTrouble ? e.FaultText : e.ScopeText,
                () => Selected = e))
            .ToList();
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Trail settings: {ex.Message}");
        }
    }

    public void Dispose() => _language.Dispose();
}
