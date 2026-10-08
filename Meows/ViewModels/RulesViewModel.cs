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

/// <summary>What starts a rule, in the trigger dropdown. The clock's own words, not a plugin's.</summary>
public sealed class RuleTriggerChoice(RuleTrigger value, string key) : ObservableObject
{
    public RuleTrigger Value { get; } = value;

    public string Name => MeowsText.Current[key];

    public override string ToString() => Name;

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

/// <summary>Which days a rule may fire on, in the scope dropdown.</summary>
public sealed class RuleScopeChoice(RuleDayScope value, string key) : ObservableObject
{
    public RuleDayScope Value { get; } = value;

    public string Name => MeowsText.Current[key];

    public override string ToString() => Name;

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

/// <summary>One weekday in the weekly picker.</summary>
public sealed class RuleDayChoice : ObservableObject
{
    private bool _checked;

    public RuleDayChoice(DayOfWeek day, Action? changed = null)
    {
        Day = day;
        Changed = changed;
    }

    public DayOfWeek Day { get; }

    public Action? Changed { get; set; }

    public string Name => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(Day);

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (SetField(ref _checked, value))
                Changed?.Invoke();
        }
    }

    internal void Reread() => OnPropertyChanged(nameof(Name));
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

    /// <summary>Trying it fires the rule right now, so only a rule that is ready can be tried.</summary>
    public bool CanTry { get; private set; }

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
        CanTry = state == RuleState.Ready;
        LastText = Rule.LastFired is { } last
            ? text.Format("rules.last", Rule.Fired, When(last), Rule.LastOutcome ?? "")
            : text["rules.never"];

        OnPropertyChanged(nameof(Sentence));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsOrphaned));
        OnPropertyChanged(nameof(CanTry));
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
    private RuleTriggerChoice? _trigger;
    private RuleScopeChoice? _scope;
    private RulePluginChoice? _source;
    private RuleKindChoice? _kind;
    private RulePluginChoice? _target;
    private RuleActionChoice? _action;
    private string _matching = "";
    private TimeSpan? _at = new TimeSpan(7, 0, 0);
    private string _note = "";

    public RulesViewModel(InstinctEngine engine, Func<IReadOnlyList<InstinctPlugin>> plugins, Func<string, IReadOnlyList<string>> seenKinds)
    {
        _engine = engine;
        _plugins = plugins;
        _seenKinds = seenKinds;
        AddCommand = new RelayCommand(Add, () => CanAdd);
        RemoveCommand = new RelayCommand(p => { if (p is RuleRowViewModel row) _engine.Remove(row.Rule); });
        ResumeCommand = new RelayCommand(p => { if (p is RuleRowViewModel row) _engine.Resume(row.Rule); });
        TryCommand = new RelayCommand(p => { if (p is RuleRowViewModel row) _engine.FireNow(row.Rule); });
        _engine.Changed += OnChanged;
        Refresh();
    }

    public ObservableCollection<RuleRowViewModel> Rows { get; } = [];

    public bool HasRules => Rows.Count > 0;

    public ObservableCollection<RulePluginChoice> Sources { get; } = [];

    public ObservableCollection<RuleKindChoice> Kinds { get; } = [];

    public ObservableCollection<RuleTriggerChoice> Triggers { get; } = [];

    public ObservableCollection<RuleScopeChoice> Scopes { get; } = [];

    public ObservableCollection<RuleDayChoice> Days { get; } = [];

    public ObservableCollection<RulePluginChoice> Targets { get; } = [];

    public ObservableCollection<RuleActionChoice> Actions { get; } = [];

    /// <summary>Nothing installed says it can be asked to do anything, so no rule can be made yet.</summary>
    public bool HasNoTargets => Targets.Count == 0;

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RelayCommand TryCommand { get; }

    public RuleTriggerChoice? SelectedTrigger
    {
        get => _trigger;
        set
        {
            if (!SetField(ref _trigger, value))
                return;
            OnPropertyChanged(nameof(IsEventTrigger));
            OnPropertyChanged(nameof(IsClockTrigger));
            OnPropertyChanged(nameof(IsWeeklyTrigger));
            AddCommand.RaiseCanExecuteChanged();
        }
    }

    public RuleScopeChoice? SelectedScope
    {
        get => _scope;
        set => SetField(ref _scope, value);
    }

    /// <summary>The event half of the editor. Hidden while the clock is the trigger.</summary>
    public bool IsEventTrigger => (_trigger?.Value ?? RuleTrigger.Event) == RuleTrigger.Event;

    /// <summary>The clock half of the editor: a time, and days for the weekly one.</summary>
    public bool IsClockTrigger => !IsEventTrigger;

    public bool IsWeeklyTrigger => (_trigger?.Value ?? RuleTrigger.Event) == RuleTrigger.Weekly;

    /// <summary>
    /// When the clock starts it, as the time picker hands it over. Null means the picker is
    /// empty, and an empty picker is seven in the morning.
    /// </summary>
    public TimeSpan? AtTime
    {
        get => _at;
        set => SetField(ref _at, value);
    }

    /// <summary>What a clock firing is about, in the person's own words. Unused by event rules.</summary>
    public string Note
    {
        get => _note;
        set => SetField(ref _note, value ?? "");
    }

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

    public bool CanAdd => _target is not null && _action is not null &&
        ((_trigger?.Value ?? RuleTrigger.Event) == RuleTrigger.Event
            ? _source is not null && _kind is not null
            : _trigger!.Value != RuleTrigger.Weekly || Days.Any(d => d.IsChecked));

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
        var trigger = _trigger?.Value ?? RuleTrigger.Event;
        var scope = _scope?.Value ?? RuleDayScope.Any;
        var days = Days.Where(d => d.IsChecked).Select(d => d.Day).ToList();
        var source = _source?.Id;
        var kind = _kind?.Kind;
        var target = _target?.Id;
        var action = _action?.Id;

        Triggers.Clear();
        Triggers.Add(new RuleTriggerChoice(RuleTrigger.Event, "rules.trigger.event"));
        Triggers.Add(new RuleTriggerChoice(RuleTrigger.Daily, "rules.trigger.daily"));
        Triggers.Add(new RuleTriggerChoice(RuleTrigger.Weekly, "rules.trigger.weekly"));
        _trigger = Triggers.FirstOrDefault(t => t.Value == trigger) ?? Triggers[0];
        OnPropertyChanged(nameof(SelectedTrigger));
        OnPropertyChanged(nameof(IsEventTrigger));
        OnPropertyChanged(nameof(IsClockTrigger));
        OnPropertyChanged(nameof(IsWeeklyTrigger));

        Scopes.Clear();
        Scopes.Add(new RuleScopeChoice(RuleDayScope.Any, "rules.scope.any"));
        Scopes.Add(new RuleScopeChoice(RuleDayScope.Weekdays, "rules.scope.weekdays"));
        Scopes.Add(new RuleScopeChoice(RuleDayScope.Weekend, "rules.scope.weekend"));
        _scope = Scopes.FirstOrDefault(s => s.Value == scope) ?? Scopes[0];
        OnPropertyChanged(nameof(SelectedScope));

        Days.Clear();
        // Monday first, like the week the habits keep.
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
            Days.Add(new RuleDayChoice(day, () => AddCommand.RaiseCanExecuteChanged()) { IsChecked = days.Contains(day) });

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

        var trigger = _trigger?.Value ?? RuleTrigger.Event;
        _engine.Add(new InstinctRule
        {
            Trigger = trigger,
            DayScope = _scope?.Value ?? RuleDayScope.Any,
            Source = trigger == RuleTrigger.Event ? _source!.Id : "",
            Kind = trigger == RuleTrigger.Event ? _kind!.Kind : "",
            Matching = trigger == RuleTrigger.Event && !string.IsNullOrWhiteSpace(_matching) ? _matching.Trim() : null,
            At = trigger == RuleTrigger.Event ? TimeSpan.Zero : (_at ?? new TimeSpan(7, 0, 0)),
            Days = trigger == RuleTrigger.Weekly ? Days.Where(d => d.IsChecked).Select(d => d.Day).ToList() : [],
            Note = trigger == RuleTrigger.Event || string.IsNullOrWhiteSpace(_note) ? null : _note.Trim(),
            Target = _target!.Id,
            Action = _action!.Id,
        });
        Matching = "";
        Note = "";
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
