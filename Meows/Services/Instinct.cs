using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// Which rule, if any, the code running right now is doing work for. Set around the call into a
/// plugin's <see cref="IActionTarget.Perform"/> and carried by the runtime into everything that
/// call awaits or starts, so the store can mark every line written on a rule's behalf.
/// </summary>
public static class InstinctScope
{
    /// <summary>The key the store adds to a line's data when a rule caused it.</summary>
    public const string DataKey = "instinct.rule";

    private static readonly AsyncLocal<string?> Current = new();

    public static string? Rule => Current.Value;

    /// <summary>Runs <paramref name="start"/> as the rule, and only its start: what it awaits keeps the mark, the caller does not.</summary>
    public static T As<T>(string rule, Func<T> start)
    {
        var previous = Current.Value;
        Current.Value = rule;
        try
        {
            return start();
        }
        finally
        {
            Current.Value = previous;
        }
    }
}

/// <summary>
/// One standing rule: when this plugin records this kind of event, ask that plugin to do this.
/// Kept in the shell's preferences, by plugin id and action id rather than by anything a
/// person reads, so a rename or a language change does not break it.
/// </summary>
public sealed class InstinctRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];

    /// <summary>The plugin whose events start it.</summary>
    public string Source { get; set; } = "";

    /// <summary>The kind of event, exactly as the source records it.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Only events whose subject or detail contains this, when set. Case does not matter.</summary>
    public string? Matching { get; set; }

    /// <summary>The plugin asked to act.</summary>
    public string Target { get; set; } = "";

    /// <summary>One of the target's <see cref="IMeowsPlugin.Actions"/>, by id.</summary>
    public string Action { get; set; } = "";

    /// <summary>Switched off by a person. Kept, and does nothing until switched back on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Why the shell stopped it on its own, or null. Only ever set for a rule that fired far
    /// more often than anyone would have meant it to; a person's Resume clears it.
    /// </summary>
    public string? PausedReason { get; set; }

    public DateTime? LastFired { get; set; }

    /// <summary>The last thing the target said, or why it could not be asked.</summary>
    public string? LastOutcome { get; set; }

    public int Fired { get; set; }
}

/// <summary>What the rules can know about one installed plugin without switching it on.</summary>
public sealed record InstinctPlugin(
    string Id,
    string Name,
    IReadOnlyList<PluginAction> Actions,
    IReadOnlyList<RecordedKind> Records);

/// <summary>Whether a rule would fire, and if not, the one reason a person needs.</summary>
public enum RuleState
{
    Ready,
    Off,
    Paused,
    SourceMissing,
    TargetMissing,
    ActionMissing,
}

/// <summary>
/// The rules, run. Every line any plugin writes to the store is offered here; a rule that
/// matches it asks its target plugin to do its action, and what came back is written to the
/// history under Instinct's own name, so the first surprising night can be explained.
///
/// Three things keep it from being the thing that surprises: a line written because a rule
/// asked never starts another rule (one hop); each target does one thing at a time, in the
/// order asked; and a rule that fires far more often than anyone meant is paused and says so.
/// A rule whose plugin has gone is never dropped, only shown as not able to run.
/// </summary>
public sealed class InstinctEngine : IDisposable
{
    /// <summary>The name Instinct writes its lines under in the history.</summary>
    public const string PluginId = "meows.instinct";

    /// <summary>More firings than this inside <see cref="RunawayWindow"/> pauses the rule.</summary>
    public const int RunawayLimit = 30;

    public static readonly TimeSpan RunawayWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long the shell waits on one action before it stops waiting and cancels it.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromHours(1);

    private readonly List<InstinctRule> _rules;
    private readonly Func<IReadOnlyList<InstinctPlugin>> _plugins;
    private readonly Func<string, ActionRequest, CancellationToken, Task<string>> _perform;
    private readonly IMeowsStore _journal;
    private readonly Action _save;
    private readonly Action<string, LogLevel> _log;
    private readonly Action<Action> _post;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _patience;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();

    // Per target, what is waiting and whether something is running. One at a time per plugin:
    // five pictures saved in a burst are five checks in a row, not five at once.
    private readonly Dictionary<string, Queue<(InstinctRule Rule, StoredEvent Cause)>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DateTime>> _recent = new();
    private int _outstanding;
    private TaskCompletionSource _idle = Completed();

    /// <param name="rules">The saved list, changed in place; <paramref name="save"/> writes it.</param>
    /// <param name="plugins">The plugins installed and loadable right now.</param>
    /// <param name="perform">Switches the target on if need be and asks it. Called on the UI thread.</param>
    /// <param name="journal">Where the account goes: the store as seen by <see cref="PluginId"/>.</param>
    /// <param name="post">Onto the UI thread. Lines arrive on whatever thread wrote them.</param>
    public InstinctEngine(
        List<InstinctRule> rules,
        Func<IReadOnlyList<InstinctPlugin>> plugins,
        Func<string, ActionRequest, CancellationToken, Task<string>> perform,
        IMeowsStore journal,
        Action save,
        Action<string, LogLevel> log,
        Action<Action> post,
        Func<DateTime>? now = null,
        TimeSpan? patience = null)
    {
        _rules = rules;
        _plugins = plugins;
        _perform = perform;
        _journal = journal;
        _save = save;
        _log = log;
        _post = post;
        _now = now ?? (() => DateTime.Now);
        _patience = patience ?? Patience;
    }

    /// <summary>Something about a rule changed: added, removed, fired, paused. For the Rules tab.</summary>
    public event Action? Changed;

    /// <summary>A copy, so the Rules tab can read it while a line arriving on another thread is matched against it.</summary>
    public IReadOnlyList<InstinctRule> Rules
    {
        get
        {
            lock (_gate)
                return _rules.ToList();
        }
    }

    private static IMeowsText Text => MeowsText.Current;

    public void Add(InstinctRule rule)
    {
        lock (_gate)
            _rules.Add(rule);
        _save();
        Changed?.Invoke();
    }

    public void Remove(InstinctRule rule)
    {
        bool removed;
        lock (_gate)
            removed = _rules.Remove(rule);
        if (removed)
        {
            _save();
            Changed?.Invoke();
        }
    }

    public void SetEnabled(InstinctRule rule, bool enabled)
    {
        if (rule.Enabled == enabled)
            return;
        rule.Enabled = enabled;
        _save();
        Changed?.Invoke();
    }

    /// <summary>A person saying the pause was fine: the count starts again from nothing.</summary>
    public void Resume(InstinctRule rule)
    {
        if (rule.PausedReason is null)
            return;
        rule.PausedReason = null;
        lock (_gate)
            _recent.Remove(rule.Id);
        _save();
        Changed?.Invoke();
    }

    /// <summary>Whether the rule would fire now, and a sentence when it would not.</summary>
    public RuleState StateOf(InstinctRule rule, out string? why)
    {
        var plugins = _plugins();
        var source = Find(plugins, rule.Source);
        var target = Find(plugins, rule.Target);

        if (source is null)
        {
            why = Text.Format("instinct.state.nosource", rule.Source);
            return RuleState.SourceMissing;
        }
        if (target is null)
        {
            why = Text.Format("instinct.state.notarget", rule.Target);
            return RuleState.TargetMissing;
        }
        if (!target.Actions.Any(a => string.Equals(a.Id, rule.Action, StringComparison.OrdinalIgnoreCase)))
        {
            why = Text.Format("instinct.state.noaction", target.Name, rule.Action);
            return RuleState.ActionMissing;
        }
        if (rule.PausedReason is { } paused)
        {
            why = paused;
            return RuleState.Paused;
        }
        if (!rule.Enabled)
        {
            why = Text["instinct.state.off"];
            return RuleState.Off;
        }

        why = null;
        return RuleState.Ready;
    }

    /// <summary>
    /// The rule as a sentence: "When Birdwatch saved a picture, Portion: Check the queues".
    /// Worked out from what the plugins say now, and from the saved ids when one is gone, so a
    /// rule pointing at nothing still reads as what it was.
    /// </summary>
    public string Describe(InstinctRule rule)
    {
        var plugins = _plugins();
        var source = Find(plugins, rule.Source);
        var target = Find(plugins, rule.Target);

        var kind = source?.Records.FirstOrDefault(r => string.Equals(r.Kind, rule.Kind, StringComparison.OrdinalIgnoreCase)) is { } declared
            ? Text[declared.Label]
            : Text.Format("instinct.kind.plain", rule.Kind);
        var action = target?.Actions.FirstOrDefault(a => string.Equals(a.Id, rule.Action, StringComparison.OrdinalIgnoreCase)) is { } known
            ? Text[known.Label]
            : rule.Action;

        var when = Text.Format("instinct.sentence.when", source?.Name ?? rule.Source, kind);
        if (!string.IsNullOrWhiteSpace(rule.Matching))
            when += " " + Text.Format("instinct.sentence.matching", rule.Matching.Trim());
        return Text.Format("instinct.sentence", when, target?.Name ?? rule.Target, action);
    }

    /// <summary>
    /// A line has landed in the store. Safe from any thread: whether it counts is settled here,
    /// at once, while the plugin that wrote it is still known to be busy or not; the firing
    /// itself goes to the UI thread.
    /// </summary>
    public void Consider(StoredEvent stored)
    {
        // Instinct's own account of what fired is not something to fire on.
        if (string.Equals(stored.Plugin, PluginId, StringComparison.OrdinalIgnoreCase))
            return;

        // One hop. Marked by the store because a rule's action was running when it was written,
        // or written by a plugin that is in the middle of doing what a rule asked, which covers
        // work that hopped threads in a way that lost the mark.
        string? causedBy = stored.Data.TryGetValue(InstinctScope.DataKey, out var marked) ? marked : null;
        List<InstinctRule> matches;
        lock (_gate)
        {
            if (causedBy is null && _busy.GetValueOrDefault(stored.Plugin) > 0)
                causedBy = "busy";
            matches = _rules.Where(r => Matches(r, stored)).ToList();
        }

        if (matches.Count == 0)
            return;

        if (causedBy is not null)
        {
            _log($"Not following '{stored.Kind}' from {stored.Plugin}: a rule asked for it, and rules do not start rules.", LogLevel.Info);
            return;
        }

        foreach (var rule in matches)
        {
            lock (_gate)
            {
                _outstanding++;
                if (_idle.Task.IsCompleted)
                    _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _post(() => Enqueue(rule, stored));
        }
    }

    /// <summary>Done when nothing is waiting and nothing is running. For tests, and for a quit that wants to be tidy.</summary>
    public Task Idle()
    {
        lock (_gate)
            return _idle.Task;
    }

    private static bool Matches(InstinctRule rule, StoredEvent stored)
    {
        if (!rule.Enabled || rule.PausedReason is not null)
            return false;
        if (!string.Equals(rule.Source, stored.Plugin, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(rule.Kind, stored.Kind, StringComparison.OrdinalIgnoreCase))
            return false;
        if (rule.Matching is { } wanted && wanted.Trim() is { Length: > 0 } text)
        {
            return stored.Subject.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                   || (stored.Detail?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false);
        }
        return true;
    }

    private void Enqueue(InstinctRule rule, StoredEvent cause)
    {
        bool start;
        lock (_gate)
        {
            if (!_waiting.TryGetValue(rule.Target, out var queue))
                _waiting[rule.Target] = queue = new Queue<(InstinctRule, StoredEvent)>();
            queue.Enqueue((rule, cause));
            start = _busy.GetValueOrDefault(rule.Target) == 0;
            if (start)
                _busy[rule.Target] = 1;
        }

        if (start)
            _ = Pump(rule.Target);
    }

    /// <summary>One target's queue, drained in order. Runs on the UI thread and awaits back onto it.</summary>
    private async Task Pump(string target)
    {
        while (true)
        {
            (InstinctRule Rule, StoredEvent Cause) next;
            lock (_gate)
            {
                if (!_waiting.TryGetValue(target, out var queue) || queue.Count == 0)
                {
                    _busy.Remove(target);
                    return;
                }
                next = queue.Dequeue();
            }

            try
            {
                await Fire(next.Rule, next.Cause);
            }
            catch (Exception ex)
            {
                _log($"A rule failed in a way nobody expected: {ex}", LogLevel.Warning);
            }
            finally
            {
                lock (_gate)
                {
                    _outstanding--;
                    if (_outstanding == 0)
                        _idle.TrySetResult();
                }
            }
        }
    }

    private async Task Fire(InstinctRule rule, StoredEvent cause)
    {
        // Settled when the line arrived, but the person may have switched it off, or it may
        // have been paused by the firings queued ahead of it, since.
        bool kept;
        lock (_gate)
            kept = _rules.Contains(rule);
        if (!kept || StateOf(rule, out _) != RuleState.Ready)
            return;

        if (IsRunaway(rule))
        {
            rule.PausedReason = Text.Format("instinct.paused.runaway", RunawayLimit, (int)RunawayWindow.TotalMinutes);
            Account(rule, cause, "paused", rule.PausedReason);
            _log($"Paused the rule '{Describe(rule)}': {rule.PausedReason}", LogLevel.Warning);
            _save();
            Changed?.Invoke();
            return;
        }

        var request = new ActionRequest(rule.Action, cause);
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        patience.CancelAfter(_patience);

        string kind;
        string outcome;
        try
        {
            var work = InstinctScope.As(rule.Id, () => _perform(rule.Target, request, patience.Token));
            outcome = await work.WaitAsync(patience.Token);
            kind = "fired";
        }
        catch (ActionDeclinedException declined)
        {
            kind = "declined";
            outcome = declined.Message;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Meows is going. Nothing to write down that anyone will be told.
            return;
        }
        catch (OperationCanceledException) when (patience.IsCancellationRequested)
        {
            kind = "failed";
            outcome = Text.Format("instinct.outcome.patience", (int)_patience.TotalMinutes);
        }
        catch (Exception ex)
        {
            kind = "failed";
            outcome = ex.Message;
        }

        rule.Fired++;
        rule.LastFired = _now();
        rule.LastOutcome = outcome;
        Account(rule, cause, kind, outcome);
        _save();
        Changed?.Invoke();
    }

    /// <summary>Counts this firing, and says whether it is one too many.</summary>
    private bool IsRunaway(InstinctRule rule)
    {
        var now = _now();
        lock (_gate)
        {
            if (!_recent.TryGetValue(rule.Id, out var times))
                _recent[rule.Id] = times = new Queue<DateTime>();
            while (times.Count > 0 && now - times.Peek() > RunawayWindow)
                times.Dequeue();
            if (times.Count >= RunawayLimit)
                return true;
            times.Enqueue(now);
            return false;
        }
    }

    /// <summary>One line in the history: what fired, on what, and what came back.</summary>
    private void Account(InstinctRule rule, StoredEvent cause, string kind, string outcome)
    {
        var detail = Text.Format("instinct.journal", Describe(rule), outcome);
        _journal.Record(kind, cause.Subject, detail, new Dictionary<string, string>
        {
            ["rule"] = rule.Id,
            ["cause"] = cause.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["source"] = rule.Source,
            ["target"] = rule.Target,
            ["action"] = rule.Action,
        });
    }

    private static InstinctPlugin? Find(IReadOnlyList<InstinctPlugin> plugins, string id) =>
        plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
