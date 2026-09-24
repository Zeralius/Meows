using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Purrge.Services;

namespace Meows.Plugins.Purrge.ViewModels;

/// <summary>One file in the rename preview.</summary>
public sealed class GroomRowViewModel(GroomRow row) : ObservableObject
{
    public GroomRow Row { get; } = row;

    public string From => Row.From;

    public string To => Row.To;

    public bool Changes => Row.Changes;

    public bool IsProblem => Row.Problem != GroomProblem.None;

    public string ProblemText => Row.Problem switch
    {
        GroomProblem.Taken => MeowsText.Current["purrge.groom.problem.taken"],
        GroomProblem.Twice => MeowsText.Current["purrge.groom.problem.twice"],
        GroomProblem.Invalid => MeowsText.Current["purrge.groom.problem.invalid"],
        GroomProblem.BadPattern => MeowsText.Current["purrge.groom.problem.pattern"],
        _ => "",
    };

    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// Purrge's fourth mode, Groom: rename the files in the folder picked in the tree. Every change is
/// in the preview before anything moves, one clash holds the whole run back, and the last run is
/// kept, across a restart too, so it can be undone.
/// </summary>
public sealed class GroomViewModel : ObservableObject
{
    public static readonly IReadOnlyList<(GroomCase Case, string Key)> Cases =
    [
        (GroomCase.Unchanged, "purrge.groom.case.unchanged"),
        (GroomCase.Lower, "purrge.groom.case.lower"),
        (GroomCase.Upper, "purrge.groom.case.upper"),
        (GroomCase.Title, "purrge.groom.case.title"),
    ];

    private readonly IMeowsHost _host;
    private readonly Func<PurrgeSettings> _settings;
    private readonly Action _save;
    private string _folder = "";
    private string? _status;
    private IReadOnlyList<GroomRow> _plan = [];

    public GroomViewModel(IMeowsHost host, Func<PurrgeSettings> settings, Action save)
    {
        _host = host;
        _settings = settings;
        _save = save;
        RunCommand = new RelayCommand(Run, () => CanRun);
        UndoCommand = new RelayCommand(Undo, () => CanUndo);
        RefreshCommand = new RelayCommand(Refresh, () => Folder.Length > 0);
    }

    public ObservableCollection<GroomRowViewModel> Rows { get; } = [];

    public RelayCommand RunCommand { get; }

    public RelayCommand UndoCommand { get; }

    public RelayCommand RefreshCommand { get; }

    private GroomRule Rule => _settings().GroomRule;

    private void Change(Func<GroomRule, GroomRule> change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _settings().GroomRule = change(Rule);
        _save();
        OnPropertyChanged(name);
        Refresh();
    }

    /// <summary>The folder being groomed: whichever is picked in the tree.</summary>
    public string Folder
    {
        get => _folder;
        set
        {
            if (!SetField(ref _folder, value ?? ""))
                return;
            RefreshCommand.RaiseCanExecuteChanged();
            Refresh();
        }
    }

    public string Filter
    {
        get => Rule.Filter;
        set { if (Rule.Filter != (value ?? "")) Change(r => r with { Filter = value ?? "" }); }
    }

    public bool DropCopyCounter
    {
        get => Rule.DropCopyCounter;
        set { if (Rule.DropCopyCounter != value) Change(r => r with { DropCopyCounter = value }); }
    }

    public string Find
    {
        get => Rule.Find;
        set { if (Rule.Find != (value ?? "")) Change(r => r with { Find = value ?? "" }); }
    }

    public string Replace
    {
        get => Rule.Replace;
        set { if (Rule.Replace != (value ?? "")) Change(r => r with { Replace = value ?? "" }); }
    }

    public bool UseRegex
    {
        get => Rule.UseRegex;
        set { if (Rule.UseRegex != value) Change(r => r with { UseRegex = value }); }
    }

    public IReadOnlyList<string> CaseChoices => Cases.Select(c => MeowsText.Current[c.Key]).ToList();

    public int CaseIndex
    {
        get => Math.Max(0, Cases.ToList().FindIndex(c => c.Case == Rule.Case));
        set
        {
            if (value < 0 || value >= Cases.Count || Cases[value].Case == Rule.Case)
                return;
            Change(r => r with { Case = Cases[value].Case });
        }
    }

    public string Template
    {
        get => Rule.Template;
        set { if (Rule.Template != (value ?? "")) Change(r => r with { Template = value ?? "" }); }
    }

    public decimal? Start
    {
        get => Rule.Start;
        set { var v = (int)(value ?? 1); if (Rule.Start != v) Change(r => r with { Start = v }); }
    }

    public decimal? Digits
    {
        get => Rule.Digits;
        set { var v = Math.Clamp((int)(value ?? 3), 1, 9); if (Rule.Digits != v) Change(r => r with { Digits = v }); }
    }

    public int ChangeCount => _plan.Count(r => r.Changes);

    public int ProblemCount => _plan.Count(r => r.Problem != GroomProblem.None);

    public bool HasRows => Rows.Count > 0;

    /// <summary>Something would change and nothing clashes. One clash holds the whole run back.</summary>
    public bool CanRun => ChangeCount > 0 && ProblemCount == 0;

    public bool CanUndo => _settings().LastGroomRun is { } run && Directory.Exists(run.Folder);

    public string Summary => Folder.Length == 0
        ? MeowsText.Current["purrge.groom.pick"]
        : ProblemCount > 0
            ? MeowsText.Current.Format("purrge.groom.summary.problems", ChangeCount, _plan.Count, ProblemCount)
            : MeowsText.Current.Format("purrge.groom.summary", ChangeCount, _plan.Count);

    public string UndoText => _settings().LastGroomRun is { } run
        ? MeowsText.Current.Format("purrge.groom.undo.what", run.Moves.Count, Path.GetFileName(run.Folder))
        : "";

    public string Status
    {
        get => _status ?? "";
        private set
        {
            if (SetField(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_status);

    /// <summary>Works the preview out again from the folder as it is now.</summary>
    public void Refresh()
    {
        _plan = Folder.Length == 0 || !Directory.Exists(Folder) ? [] : Groom.Plan(Folder, Rule);
        Rows.Clear();
        foreach (var row in _plan.OrderByDescending(r => r.Problem != GroomProblem.None).ThenByDescending(r => r.Changes))
            Rows.Add(new GroomRowViewModel(row));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(Summary));
        RunCommand.RaiseCanExecuteChanged();
    }

    private void Run()
    {
        // The folder may have changed since the preview; what runs is what the folder says now.
        Refresh();
        if (!CanRun)
            return;

        var (outcome, run) = Groom.Apply(_plan);
        if (run is not null)
        {
            _settings().LastGroomRun = run;
            _save();
            _host.Store.Record("renamed", run.Folder, _host.Text.Format("purrge.groom.journal", run.Moves.Count),
                new Dictionary<string, string> { ["count"] = run.Moves.Count.ToString() });
            _host.Log($"Groom renamed {run.Moves.Count} file(s) in {run.Folder}.");
        }

        Status = outcome.Failed.Count == 0
            ? _host.Text.Format("purrge.groom.done", outcome.Moved)
            : _host.Text.Format("purrge.groom.done.failed", outcome.Moved, outcome.Failed.Count, string.Join(", ", outcome.Failed.Take(3)));
        Settled();
    }

    private void Undo()
    {
        if (_settings().LastGroomRun is not { } run)
            return;

        var outcome = Groom.Undo(run);
        _settings().LastGroomRun = null;
        _save();
        _host.Store.Record("renamed", run.Folder, _host.Text.Format("purrge.groom.journal.undo", outcome.Moved),
            new Dictionary<string, string> { ["count"] = outcome.Moved.ToString(), ["undo"] = "true" });
        _host.Log($"Groom put back {outcome.Moved} name(s) in {run.Folder}.");

        Status = outcome.Failed.Count == 0
            ? _host.Text.Format("purrge.groom.undone", outcome.Moved)
            : _host.Text.Format("purrge.groom.undone.failed", outcome.Moved, outcome.Failed.Count, string.Join(", ", outcome.Failed.Take(3)));
        Settled();
    }

    private void Settled()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoText));
        UndoCommand.RaiseCanExecuteChanged();
        Refresh();
    }

    internal void Reread()
    {
        OnPropertyChanged(nameof(CaseChoices));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(UndoText));
        foreach (var row in Rows)
            row.Reread();
    }
}
