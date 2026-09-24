using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.ViewModels;

/// <summary>A plugin in one of the rule editor's dropdowns.</summary>
public sealed record RulePluginChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A kind of event in the "records" dropdown: the word as recorded, and how to say it.</summary>
public sealed record RuleKindChoice(string Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An action in the "then" dropdown.</summary>
public sealed record RuleActionChoice(string Id, string Label, string? Description)
{
    public override string ToString() => Label;
}

/// <summary>One saved rule as the tab shows it: the sentence, whether it can run, and what it last did.</summary>
public sealed class RuleRowViewModel : ObservableObject
{
    private readonly InstinctEngine _engine;

    public RuleRowViewModel(InstinctEngine engine, InstinctRule rule)
    {
        _engine = engine;
        Rule = rule;
        Update();
    }

    public InstinctRule Rule { get; }

    public string Sentence { get; private set; } = "";

    /// <summary>Why it will not run, or empty when it will.</summary>
    public string Problem { get; private set; } = "";

    public bool HasProblem => Problem.Length > 0;

    /// <summary>Stopped by the shell for firing too often, which a person can undo.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>The plugin it waits on or the one it asks is not here; the rule is kept and waits.</summary>
    public bool IsOrphaned { get; private set; }

    public string LastText { get; private set; } = "";

    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled == value)
                return;
            _engine.SetEnabled(Rule, value);
            Update();
        }
    }

    public void Update()
    {
        var text = MeowsText.Current;
        Sentence = _engine.Describe(Rule);
        var state = _engine.StateOf(Rule, out var why);
        Problem = state is RuleState.Ready or RuleState.Off ? "" : why ?? "";
        IsPaused = state == RuleState.Paused;
        IsOrphaned = state is RuleState.SourceMissing or RuleState.TargetMissing or RuleState.ActionMissing;
        LastText = Rule.LastFired is { } last
            ? text.Format("rules.last", Rule.Fired, When(last), Rule.LastOutcome ?? "")
            : text["rules.never"];

        OnPropertyChanged(nameof(Sentence));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsOrphaned));
        OnPropertyChanged(nameof(LastText));
        OnPropertyChanged(nameof(Enabled));
    }

    private static string When(DateTime at) =>
        at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("ddd d MMM HH:mm");
}

/// <summary>
/// The Rules tab: the standing rules, and the one sentence that makes a new one. "When
/// [plugin] [records this] (matching [text]), [plugin]: [does this]". Everything offered comes
/// from what the installed plugins say about themselves, plus the kinds of event the history
/// shows a plugin has actually written, so a plugin that never declared anything can still
/// start a rule once it has done something.
/// </summary>
public sealed class RulesViewModel : ObservableObject, IDisposable
{
    private readonly InstinctEngine _engine;
    private readonly Func<IReadOnlyList<InstinctPlugin>> _plugins;
    private readonly Func<string, IReadOnlyList<string>> _seenKinds;
    private RulePluginChoice? _source;
    private RuleKindChoice? _kind;
    private RulePluginChoice? _target;
    private RuleActionChoice? _action;
    private string _matching = "";

    public RulesViewModel(InstinctEngine engine, Func<IReadOnlyList<InstinctPlugin>> plugins, Func<string, IReadOnlyList<string>> seenKinds)
    {
        _engine = engine;
        _plugins = plugins;
        _seenKinds = seenKinds;
        AddCommand = new RelayCommand(Add, () => CanAdd);
        RemoveCommand = new RelayCommand(p => { if (p is RuleRowViewModel row) _engine.Remove(row.Rule); });
        ResumeCommand = new RelayCommand(p => { if (p is RuleRowViewModel row) _engine.Resume(row.Rule); });
        _engine.Changed += OnChanged;
        Refresh();
    }

    public ObservableCollection<RuleRowViewModel> Rows { get; } = [];

    public bool HasRules => Rows.Count > 0;

    public ObservableCollection<RulePluginChoice> Sources { get; } = [];

    public ObservableCollection<RuleKindChoice> Kinds { get; } = [];

    public ObservableCollection<RulePluginChoice> Targets { get; } = [];

    public ObservableCollection<RuleActionChoice> Actions { get; } = [];

    /// <summary>Nothing installed says it can be asked to do anything, so no rule can be made yet.</summary>
    public bool HasNoTargets => Targets.Count == 0;

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RulePluginChoice? SelectedSource
    {
        get => _source;
        set
        {
            if (!SetField(ref _source, value))
                return;
            FillKinds(_kind?.Kind);
            AddCommand.RaiseCanExecuteChanged();
        }
    }

    public RuleKindChoice? SelectedKind
    {
        get => _kind;
        set
        {
            if (SetField(ref _kind, value))
                AddCommand.RaiseCanExecuteChanged();
        }
    }

    public string Matching
    {
        get => _matching;
        set => SetField(ref _matching, value ?? "");
    }

    public RulePluginChoice? SelectedTarget
    {
        get => _target;
        set
        {
            if (!SetField(ref _target, value))
                return;
            FillActions(_action?.Id);
            AddCommand.RaiseCanExecuteChanged();
        }
    }

    public RuleActionChoice? SelectedAction
    {
        get => _action;
        set
        {
            if (!SetField(ref _action, value))
                return;
            OnPropertyChanged(nameof(ActionDescription));
            OnPropertyChanged(nameof(HasActionDescription));
            AddCommand.RaiseCanExecuteChanged();
        }
    }

    public string ActionDescription => _action?.Description ?? "";

    public bool HasActionDescription => ActionDescription.Length > 0;

    public bool CanAdd => _source is not null && _kind is not null && _target is not null && _action is not null;

    /// <summary>
    /// Everything read again: the plugins, their words in the current language, and every row.
    /// Called when the list of plugins is read, when the language changes, and when a rule
    /// fires or is paused. Keeps whatever was chosen in the editor if it is still there.
    /// </summary>
    public void Refresh()
    {
        var plugins = _plugins().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        // Read before anything is cleared: a dropdown whose list is emptied sets its choice to
        // nothing, and that would otherwise be what got restored.
        var source = _source?.Id;
        var kind = _kind?.Kind;
        var target = _target?.Id;
        var action = _action?.Id;

        Sources.Clear();
        foreach (var plugin in plugins)
            Sources.Add(new RulePluginChoice(plugin.Id, plugin.Name));

        Targets.Clear();
        foreach (var plugin in plugins.Where(p => p.Actions.Count > 0))
            Targets.Add(new RulePluginChoice(plugin.Id, plugin.Name));
        OnPropertyChanged(nameof(HasNoTargets));

        _source = Sources.FirstOrDefault(s => s.Id == source);
        _target = Targets.FirstOrDefault(t => t.Id == target);
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(SelectedTarget));
        FillKinds(kind);
        FillActions(action);

        Rows.Clear();
        foreach (var rule in _engine.Rules)
            Rows.Add(new RuleRowViewModel(_engine, rule));
        OnPropertyChanged(nameof(HasRules));
        AddCommand.RaiseCanExecuteChanged();
    }

    private void FillKinds(string? kept)
    {
        Kinds.Clear();
        if (_source is { } source && _plugins().FirstOrDefault(p => p.Id == source.Id) is { } plugin)
        {
            var text = MeowsText.Current;
            foreach (var declared in plugin.Records)
                Kinds.Add(new RuleKindChoice(declared.Kind, text[declared.Label]));
            foreach (var seen in _seenKinds(plugin.Id))
            {
                if (!Kinds.Any(k => string.Equals(k.Kind, seen, StringComparison.OrdinalIgnoreCase)))
                    Kinds.Add(new RuleKindChoice(seen, text.Format("instinct.kind.plain", seen)));
            }
        }

        _kind = Kinds.FirstOrDefault(k => k.Kind == kept) ?? Kinds.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedKind));
        AddCommand.RaiseCanExecuteChanged();
    }

    private void FillActions(string? kept)
    {
        Actions.Clear();
        if (_target is { } target && _plugins().FirstOrDefault(p => p.Id == target.Id) is { } plugin)
        {
            var text = MeowsText.Current;
            foreach (var action in plugin.Actions)
                Actions.Add(new RuleActionChoice(action.Id, text[action.Label], action.Description is { } d ? text[d] : null));
        }

        _action = Actions.FirstOrDefault(a => a.Id == kept) ?? Actions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedAction));
        OnPropertyChanged(nameof(ActionDescription));
        OnPropertyChanged(nameof(HasActionDescription));
        AddCommand.RaiseCanExecuteChanged();
    }

    private void Add()
    {
        if (!CanAdd)
            return;

        _engine.Add(new InstinctRule
        {
            Source = _source!.Id,
            Kind = _kind!.Kind,
            Matching = string.IsNullOrWhiteSpace(_matching) ? null : _matching.Trim(),
            Target = _target!.Id,
            Action = _action!.Id,
        });
        Matching = "";
    }

    /// <summary>A rule added, removed, fired or paused. Rows are rebuilt when the list itself changed, and brought up to date otherwise.</summary>
    private void OnChanged()
    {
        var rules = _engine.Rules;
        if (rules.Count != Rows.Count || rules.Where((r, i) => !ReferenceEquals(r, Rows[i].Rule)).Any())
        {
            Rows.Clear();
            foreach (var rule in rules)
                Rows.Add(new RuleRowViewModel(_engine, rule));
            OnPropertyChanged(nameof(HasRules));
            return;
        }

        foreach (var row in Rows)
            row.Update();
    }

    public void Dispose() => _engine.Changed -= OnChanged;
}
